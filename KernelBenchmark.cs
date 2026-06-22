using System.Diagnostics;
using System.Runtime;

namespace HAL9001;

/// <summary>The timing summary for one implementation (all numbers in milliseconds).</summary>
/// <param name="MedianMs">The RANKING statistic — robust to outliers (see class doc).</param>
/// <param name="MinMs">Best observed run — the least-disturbed-by-noise sample.</param>
/// <param name="MaxMs">Worst run — together with Min it shows the spread (= the noise floor).</param>
/// <param name="MeanMs">Arithmetic mean (for reference only; we do NOT rank on it).</param>
/// <param name="Iterations">How many timed runs produced these numbers.</param>
public sealed record TimingStat(double MedianMs, double MinMs, double MaxMs, double MeanMs, int Iterations);

/// <summary>
/// Reliable single-threaded benchmarking of a matrix-multiply delegate.
///
/// Trustworthy timing is the foundation of the entire kernel-optimization direction: if the
/// benchmark is noisy it ranks the wrong winner, and every later bite (distributing the search
/// across a swarm) inherits that lie. So the methodology here is deliberate and documented.
///
/// ── METHODOLOGY ──────────────────────────────────────────────────────────────────────────
///
/// 1. IDENTICAL WORKLOAD. Every candidate (and the reference) is timed on the SAME pre-built
///    input matrices A and B. Same data, same size, same cache behaviour → the only variable
///    is the implementation.
///
/// 2. WARMUP (defeating the JIT). .NET uses *tiered compilation*: a method first runs as
///    quick-but-unoptimised "Tier-0" code, and only after it has been called enough times is it
///    recompiled by the optimising JIT to "Tier-1". Long-running loops are additionally upgraded
///    mid-flight by *On-Stack Replacement (OSR)*. If we timed the first call we would be measuring
///    Tier-0 code — and possibly the JIT *compiling* the method — not the optimised steady state
///    we actually care about. So before timing we call the candidate several times and throw the
///    results away. After warmup the hot loops are running as fully-optimised Tier-1/OSR code,
///    and instruction/data caches and branch predictors are warm. (Warmup results are consumed
///    into <see cref="Sink"/> so warmup itself isn't elided.)
///
/// 3. MANY ITERATIONS, ROBUST STATISTIC. A single timing is meaningless — an OS context switch,
///    an interrupt, or a GC pause can land on any given run. We time N runs INDIVIDUALLY and rank
///    on the MEDIAN. The median is robust: if a GC or the scheduler disturbs a couple of runs,
///    those become outliers the median ignores, whereas the mean would be dragged up by them. We
///    also report MIN (the cleanest run — what the code can do undisturbed) and MAX (so the
///    Min↔Max spread exposes how noisy the measurement was; a tight spread = trustworthy).
///
/// 4. GC CONTROL. Before the timed loop we force a full collection so we don't start mid-way to a
///    gen-2 GC, and we ask the runtime for <see cref="GCLatencyMode.SustainedLowLatency"/> for the
///    duration so it avoids blocking gen-2 collections while we measure. Each candidate allocates
///    exactly one result matrix per call (identical allocation pressure across candidates), and
///    any GC that still slips through is absorbed by the median.
///
/// 5. HIGH-RESOLUTION CLOCK. <see cref="Stopwatch"/> wraps the platform performance counter
///    (sub-microsecond), far finer than the millisecond-scale work we're timing.
///
/// 6. DEAD-CODE ELIMINATION DEFENSE. A candidate is an opaque, separately-loaded assembly, but to
///    be doubly safe we consume each result via <see cref="MatrixOps.Checksum"/> into the printed
///    <see cref="Sink"/> — the computation provably escapes, so it cannot be optimised away.
///
/// 7. LOWER SCHEDULING NOISE (best-effort). We raise process priority and (on Windows) pin to a
///    single core for the run, so the OS migrates/preempts us less. Both are wrapped in try/catch
///    — if the platform refuses, we just measure with slightly more noise, never crash.
///
/// Single-threaded ONLY: candidates are instructed not to use threads/Parallel, so we compare
/// ALGORITHMIC and MEMORY-ACCESS efficiency (loop order, tiling, vectorisable inner loops) rather
/// than how many cores a candidate grabbed. Multi-core kernels are a deliberately separate concern.
/// </summary>
public static class KernelBenchmark
{
    /// <summary>
    /// Anti-dead-code-elimination sink. Every produced matrix is folded in here and the final
    /// value is printed, so the JIT can never prove a result is unused. Not used for anything else.
    /// </summary>
    public static double Sink;

    /// <summary>
    /// Warm up, then time <paramref name="fn"/> on (<paramref name="a"/>, <paramref name="b"/>)
    /// for <paramref name="iterations"/> runs and return the robust timing summary.
    /// </summary>
    public static TimingStat Measure(
        Func<double[,], double[,], double[,]> fn,
        double[,] a, double[,] b,
        int warmup, int iterations)
    {
        // ── 2. WARMUP — force Tier-1/OSR compilation and warm the caches. ─────────────────
        for (int w = 0; w < warmup; w++)
        {
            double[,] c = fn(a, b);
            Sink += MatrixOps.Checksum(c); // consume → warmup can't be elided
        }

        // ── 4. GC CONTROL — clean slate, then ask GC to stay out of the way while we time. ─
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GCLatencyMode previousMode = GCSettings.LatencyMode;

        var times = new double[iterations];
        var sw = new Stopwatch();
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            // ── 3 + 5. Time each run individually with the high-resolution clock. ──────────
            for (int it = 0; it < iterations; it++)
            {
                sw.Restart();
                double[,] c = fn(a, b);
                sw.Stop();
                times[it] = sw.Elapsed.TotalMilliseconds;
                Sink += MatrixOps.Checksum(c); // 6. consume result outside the timed region
            }
        }
        finally
        {
            GCSettings.LatencyMode = previousMode; // always restore, even if a candidate throws
        }

        return Summarize(times);
    }

    /// <summary>Median / min / max / mean over the per-iteration times.</summary>
    private static TimingStat Summarize(double[] times)
    {
        var sorted = (double[])times.Clone();
        Array.Sort(sorted);
        int n = sorted.Length;

        // Median: middle element for odd N, average of the two middles for even N.
        double median = (n % 2 == 1)
            ? sorted[n / 2]
            : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);

        double sum = 0.0;
        foreach (double t in sorted) sum += t;

        return new TimingStat(
            MedianMs: median,
            MinMs: sorted[0],
            MaxMs: sorted[n - 1],
            MeanMs: sum / n,
            Iterations: n);
    }

    /// <summary>
    /// Best-effort noise reduction for the whole benchmarking phase (step 7). Returns an
    /// <see cref="IDisposable"/> that restores the previous settings; use it in a <c>using</c>.
    /// Everything is wrapped so an unsupported platform degrades to "a bit noisier", never a crash.
    /// </summary>
    public static IDisposable QuietScope() => new QuietScopeImpl();

    private sealed class QuietScopeImpl : IDisposable
    {
        private readonly ProcessPriorityClass _prevPriority;
        private readonly IntPtr _prevAffinity;
        private readonly bool _affinitySet;

        public QuietScopeImpl()
        {
            Process p = Process.GetCurrentProcess();
            _prevPriority = p.PriorityClass;
            _prevAffinity = IntPtr.Zero;

            try { p.PriorityClass = ProcessPriorityClass.High; } catch { /* may be denied; ignore */ }

            // Pin to one core so the OS doesn't migrate us between cores mid-measurement
            // (a migration flushes per-core caches and shows up as a timing spike). ProcessorAffinity
            // only exists on Windows/Linux, so guard on platform (the analyzer's CA1416) and still
            // try/catch in case it's denied at runtime.
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                try
                {
                    _prevAffinity = p.ProcessorAffinity;
                    p.ProcessorAffinity = (IntPtr)1; // core 0 only
                    _affinitySet = true;
                }
                catch { /* not supported here; measure without pinning */ }
            }
        }

        public void Dispose()
        {
            Process p = Process.GetCurrentProcess();
            try { p.PriorityClass = _prevPriority; } catch { }
            if (_affinitySet && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            {
                try { p.ProcessorAffinity = _prevAffinity; } catch { }
            }
        }
    }
}
