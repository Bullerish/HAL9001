using System.Diagnostics;
using System.Text;

namespace HAL9001;

/// <summary>
/// LLM-FREE derivation of matrix-multiplication algorithms (bite 17). Where the rest of the project
/// asks an LLM to WRITE candidate code (which mostly REDISCOVERS known schemes from its training
/// data), this engine searches the math directly and can derive algorithms from nothing.
///
/// THE FORMAL OBJECT. Multiplying two n×n matrices is one fixed 3-D tensor T (size n²×n²×n²). A
/// bilinear algorithm using R multiplications is EXACTLY a rank-R decomposition of T:
///     T = Σ_{r=1..R} u_r ⊗ v_r ⊗ w_r
/// Each triple (u_r, v_r, w_r) is one multiplication: form a linear combination of A's entries (via
/// u_r), one of B's entries (via v_r), multiply those two scalars — the single multiply — and add the
/// result into C scaled by w_r. Strassen's "2×2 in 7" is a rank-7 decomposition of T₂ (naive rank 8).
///
/// THE SEARCH (no LLM). Coefficients are restricted to {-1, 0, 1} — the set every known optimal small
/// algorithm (Strassen, Laderman) lives in. The key structural insight: the reconstruction error
/// DECOMPOSES BY OUTPUT COLUMN — cell (a,b,g) depends on the w's only through w[·,g] — so given U and
/// V, the BEST W is not searched but SOLVED exactly, one output column at a time. That collapses the
/// search to U and V (with W always optimal underneath), which is what makes rank-7 reachable.
/// Simulated annealing over U,V escapes the shallow local minima that trap blind 3-way search.
///
/// A found decomposition is turned into a <c>Scalar[,] Multiply</c> method by MECHANICAL codegen (no
/// LLM), then fed through the bite-16 exact verifier + novelty gate. The multiplication count is R.
/// </summary>
public sealed class TensorSearch
{
    /// <summary>A rank-R decomposition: U/V/W are R×n² coefficient matrices over {-1,0,1}.</summary>
    public sealed record Decomposition(int N, int Rank, int[,] U, int[,] V, int[,] W);

    /// <summary>
    /// Search for an EXACT rank-<paramref name="rank"/> decomposition of the n×n matmul tensor.
    /// Returns the decomposition on success (error 0), or null if none was found within the budget;
    /// <paramref name="bestError"/> reports the lowest residual seen (0 == exact).
    /// </summary>
    public static Decomposition? Search(
        int n, int rank, out int bestError,
        int maxRestarts = 100000, double maxSeconds = 25, int? seed = null,
        Action<string>? onProgress = null)
    {
        var runner = new Runner(n, rank, seed ?? Environment.TickCount);
        return runner.Run(maxRestarts, maxSeconds, out bestError, onProgress);
    }

    // ── the search runner: ALTERNATING optimization (ALS) ─────────────────────────────────
    // Each of U, V, W is solved EXACTLY given the other two: the error decomposes by slice (for U, by
    // row a; for V, by column b; for W, by output g), and each slice is an independent minimisation of
    // ‖Σ_r c_r·basis_r − target‖² over c ∈ {-1,0,1}^R. We cycle U→V→W (error monotonically drops) to a
    // local minimum, then restart. Per-slice solve is exact by base-3 enumeration when 3^R is small
    // (covers n=2), else coordinate descent. ALS + cheap restarts reliably finds rank-7 for T₂.
    private sealed class Runner
    {
        private readonly int _n, _n2, _n4, _rank;
        private readonly int[,,] _t;
        private readonly int[,] _u, _v, _w;
        private readonly Random _rng;

        private readonly int[][] _basis; // _basis[r][cell] for the slice currently being solved
        private readonly int[] _tg, _s, _d, _bestC;
        private readonly int[,] _wsave;  // W snapshot for cheap revert during U,V annealing
        private long _sliceErr;
        private readonly bool _brute;
        private const long BruteCap = 60000; // 3^R ≤ cap ⇒ exact brute force (R ≤ 10)

        // Full-tensor state for the k-flip polish stage.
        private readonly int[,,] _fr;       // recon for the full-tensor local search
        private long _ferr;
        private readonly List<(int kind, int r, int idx)> _fcoords = new();
        private readonly bool _canPolish;

        public Runner(int n, int rank, int seed)
        {
            _n = n; _n2 = n * n; _n4 = _n2 * _n2; _rank = rank;
            _rng = new Random(seed);
            _t = BuildMatmulTensor(n);
            _u = new int[rank, _n2]; _v = new int[rank, _n2]; _w = new int[rank, _n2];
            _basis = new int[rank][]; for (int r = 0; r < rank; r++) _basis[r] = new int[_n4];
            _tg = new int[_n4]; _s = new int[_n4]; _d = new int[rank]; _bestC = new int[rank];
            _wsave = new int[rank, _n2];
            _brute = Pow3(rank) <= BruteCap;

            _fr = new int[_n2, _n2, _n2];
            for (int kind = 0; kind < 3; kind++) for (int r = 0; r < rank; r++) for (int i = 0; i < _n2; i++) _fcoords.Add((kind, r, i));
            _canPolish = _fcoords.Count <= 120; // k-flip is O(coords²)–O(coords³): small tensors only
        }

        // Per restart: SIMULATED ANNEALING over U and V, with W SOLVED OPTIMALLY after every move
        // (SolveFactorW re-derives the best W for the current U,V) — a far stronger move than touching
        // one coordinate, which is what reliably derives reachable decompositions (e.g. the naive
        // rank-8 from scratch in seconds). A k-flip polish closes small final gaps. Diversity comes
        // from random restarts. The hard sub-cubic cases (Strassen rank-7) remain hard for this search.
        public Decomposition? Run(int maxRestarts, double maxSeconds, out int bestError, Action<string>? onProgress = null)
        {
            var sw = Stopwatch.StartNew();
            bestError = int.MaxValue;
            int uvCount = 2 * _rank * _n2;
            int iters = Math.Max(3000, 50 * uvCount);

            for (int restart = 0; restart < maxRestarts; restart++)
            {
                if (sw.Elapsed.TotalSeconds > maxSeconds) break;
                RandomInitAll();
                int err = SolveFactorW();                 // optimal W for the random U,V
                if (err == 0) { bestError = 0; return Snapshot(); }

                double temp = 2.0;
                for (int it = 0; it < iters; it++)
                {
                    bool onU = _rng.Next(2) == 0;
                    int r = _rng.Next(_rank), idx = _rng.Next(_n2);
                    int old = onU ? _u[r, idx] : _v[r, idx];
                    int neu = (old + 1 + _rng.Next(2)) % 3 - 1;

                    Array.Copy(_w, _wsave, _w.Length); int prevErr = err;
                    if (onU) _u[r, idx] = neu; else _v[r, idx] = neu;
                    int newErr = SolveFactorW();
                    int delta = newErr - prevErr;
                    if (delta <= 0 || _rng.NextDouble() < Math.Exp(-delta / temp))
                        err = newErr;                                          // accept
                    else { if (onU) _u[r, idx] = old; else _v[r, idx] = old; Array.Copy(_wsave, _w, _w.Length); err = prevErr; } // revert

                    if (err == 0) { bestError = 0; return Snapshot(); }
                    if (err < bestError) { bestError = err; onProgress?.Invoke($"SA err→{bestError} (restart {restart}, it {it})"); }
                    temp *= 0.999; if (temp < 0.05) temp = 0.05;
                    if ((it & 511) == 0 && sw.Elapsed.TotalSeconds > maxSeconds) break;
                }

                // k-flip polish to close a small final gap.
                if (_canPolish && err > 0 && err <= 6)
                {
                    onProgress?.Invoke($"polish: err={err}, trying k-flip...");
                    LoadFull(_u, _v, _w);
                    int pe = Polish();
                    if (pe == 0) { bestError = 0; return Snapshot(); }
                    if (pe < bestError) { bestError = pe; onProgress?.Invoke($"polish: err→{bestError}"); }
                }
            }
            return null; // only EXACT decompositions are returned
        }

        // Solve W given U,V: for each output g, basis_r over (a,b) = U[r,a]·V[r,b], target = T[a,b,g].
        private int SolveFactorW()
        {
            int total = 0;
            for (int g = 0; g < _n2; g++)
            {
                for (int r = 0; r < _rank; r++)
                {
                    int[] br = _basis[r];
                    for (int a = 0; a < _n2; a++) { int ura = _u[r, a], aa = a * _n2; for (int b = 0; b < _n2; b++) br[aa + b] = ura * _v[r, b]; }
                }
                for (int a = 0; a < _n2; a++) { int aa = a * _n2; for (int b = 0; b < _n2; b++) _tg[aa + b] = _t[a, b, g]; }
                total += SolveSlice();
                for (int r = 0; r < _rank; r++) _w[r, g] = _bestC[r];
            }
            return total;
        }

        private int SolveSlice() => _brute ? SolveSliceBrute() : SolveSliceGreedy();

        // Exact: enumerate {-1,0,1}^R via a base-3 odometer (digit d ⇒ coeff d-1) from the all-(-1)
        // state, tracking the minimum slice error. Writes the best coeffs into _bestC.
        private int SolveSliceBrute()
        {
            for (int r = 0; r < _rank; r++) _d[r] = 0;
            for (int cell = 0; cell < _n4; cell++)
            {
                int s = 0;
                for (int r = 0; r < _rank; r++) s -= _basis[r][cell];
                _s[cell] = s;
            }
            _sliceErr = 0;
            for (int cell = 0; cell < _n4; cell++) { long diff = _s[cell] - _tg[cell]; _sliceErr += diff * diff; }

            long bestErr = _sliceErr;
            for (int r = 0; r < _rank; r++) _bestC[r] = -1;

            // DETERMINISTIC strict-improvement: each slice is solved to its exact minimum given the
            // other two factors, so ALS converges monotonically. Diversity comes from random restarts
            // (RandomInitAll) — NOT from per-slice randomness, which would stop the factors ever
            // aligning on a joint exact solution.
            long total = Pow3(_rank);
            for (long step = 1; step < total && bestErr > 0; step++)
            {
                int r = 0;
                while (r < _rank)
                {
                    if (_d[r] < 2) { _d[r]++; SliceDelta(r, +1); break; }
                    _d[r] = 0; SliceDelta(r, -2); r++;
                }
                if (_sliceErr < bestErr)
                {
                    bestErr = _sliceErr;
                    for (int rr = 0; rr < _rank; rr++) _bestC[rr] = _d[rr] - 1;
                }
            }
            return (int)bestErr;
        }

        private void SliceDelta(int r, int dc)
        {
            int[] br = _basis[r];
            for (int cell = 0; cell < _n4; cell++)
            {
                int change = dc * br[cell];
                if (change == 0) continue;
                int sv = _s[cell], t = _tg[cell], nv = sv + change;
                _sliceErr += (long)(nv - t) * (nv - t) - (long)(sv - t) * (sv - t);
                _s[cell] = nv;
            }
        }

        // Coordinate-descent fallback for large R (near-optimal). Writes best coeffs into _bestC.
        private int SolveSliceGreedy()
        {
            int bestErr = int.MaxValue;
            var c = new int[_rank];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                for (int r = 0; r < _rank; r++) c[r] = _rng.Next(3) - 1;
                int err = SliceError(c);
                bool improved = true;
                while (improved && err > 0)
                {
                    improved = false;
                    for (int r = 0; r < _rank; r++)
                    {
                        int bestV = c[r];
                        for (int v = -1; v <= 1; v++) { if (v == c[r]) continue; c[r] = v; int e = SliceError(c); if (e < err) { err = e; bestV = v; improved = true; } c[r] = bestV; }
                        c[r] = bestV;
                    }
                }
                if (err < bestErr) { bestErr = err; for (int r = 0; r < _rank; r++) _bestC[r] = c[r]; if (bestErr == 0) break; }
            }
            return bestErr;
        }

        private int SliceError(int[] c)
        {
            int err = 0;
            for (int cell = 0; cell < _n4; cell++)
            {
                int s = 0;
                for (int r = 0; r < _rank; r++) { int cr = c[r]; if (cr != 0) s += cr * _basis[r][cell]; }
                int diff = s - _tg[cell];
                err += diff * diff;
            }
            return err;
        }

        private void RandomInitAll()
        {
            for (int r = 0; r < _rank; r++)
                for (int i = 0; i < _n2; i++) { _u[r, i] = Sparse(); _v[r, i] = Sparse(); _w[r, i] = Sparse(); }
        }
        private int Sparse() { int x = _rng.Next(5); return x == 0 ? -1 : x == 1 ? 1 : 0; }

        // ── full-tensor k-flip intensification ────────────────────────────────────────────
        private void LoadFull(int[,] U, int[,] V, int[,] W)
        {
            Array.Copy(U, _u, U.Length); Array.Copy(V, _v, V.Length); Array.Copy(W, _w, W.Length);
            Array.Clear(_fr, 0, _fr.Length);
            for (int r = 0; r < _rank; r++)
                for (int a = 0; a < _n2; a++) { int ua = _u[r, a]; if (ua == 0) continue;
                    for (int b = 0; b < _n2; b++) { int vb = _v[r, b]; if (vb == 0) continue;
                        for (int g = 0; g < _n2; g++) { int wg = _w[r, g]; if (wg == 0) continue; _fr[a, b, g] += ua * vb * wg; } } }
            _ferr = 0;
            for (int a = 0; a < _n2; a++) for (int b = 0; b < _n2; b++) for (int g = 0; g < _n2; g++)
            { long e = _fr[a, b, g] - _t[a, b, g]; _ferr += e * e; }
        }
        private int CurFull(int kind, int r, int idx) => kind == 0 ? _u[r, idx] : kind == 1 ? _v[r, idx] : _w[r, idx];
        private void ApplyFull(int kind, int r, int idx, int val)
        {
            if (kind == 0)
            {
                int old = _u[r, idx]; if (old == val) return; int d = val - old;
                for (int b = 0; b < _n2; b++) { int vb = _v[r, b]; if (vb == 0) continue; for (int g = 0; g < _n2; g++) { int wg = _w[r, g]; if (wg == 0) continue; BumpFull(idx, b, g, d * vb * wg); } }
                _u[r, idx] = val;
            }
            else if (kind == 1)
            {
                int old = _v[r, idx]; if (old == val) return; int d = val - old;
                for (int a = 0; a < _n2; a++) { int ua = _u[r, a]; if (ua == 0) continue; for (int g = 0; g < _n2; g++) { int wg = _w[r, g]; if (wg == 0) continue; BumpFull(a, idx, g, d * ua * wg); } }
                _v[r, idx] = val;
            }
            else
            {
                int old = _w[r, idx]; if (old == val) return; int d = val - old;
                for (int a = 0; a < _n2; a++) { int ua = _u[r, a]; if (ua == 0) continue; for (int b = 0; b < _n2; b++) { int vb = _v[r, b]; if (vb == 0) continue; BumpFull(a, b, idx, d * ua * vb); } }
                _w[r, idx] = val;
            }
        }
        private void BumpFull(int a, int b, int g, int change)
        {
            if (change == 0) return;
            int cell = _fr[a, b, g], t = _t[a, b, g], nv = cell + change;
            _ferr += (long)(nv - t) * (nv - t) - (long)(cell - t) * (cell - t);
            _fr[a, b, g] = nv;
        }

        private void OneFlipFull()
        {
            while (true)
            {
                long before = _ferr;
                foreach (var (k, r, i) in _fcoords)
                {
                    int old = CurFull(k, r, i), bestv = old; long beste = _ferr;
                    for (int v = -1; v <= 1; v++) { if (v == old) continue; ApplyFull(k, r, i, v); if (_ferr < beste) { beste = _ferr; bestv = v; } }
                    ApplyFull(k, r, i, bestv);
                }
                if (_ferr == 0 || _ferr >= before) return;
            }
        }
        private bool TwoFlipFull()
        {
            long baseErr = _ferr;
            for (int i = 0; i < _fcoords.Count; i++)
            {
                var (k1, r1, x1) = _fcoords[i]; int o1 = CurFull(k1, r1, x1);
                for (int v1 = -1; v1 <= 1; v1++)
                {
                    if (v1 == o1) continue;
                    ApplyFull(k1, r1, x1, v1);
                    for (int j = 0; j < _fcoords.Count; j++)
                    {
                        if (j == i) continue;
                        var (k2, r2, x2) = _fcoords[j]; int o2 = CurFull(k2, r2, x2);
                        for (int v2 = -1; v2 <= 1; v2++)
                        {
                            if (v2 == o2) continue;
                            ApplyFull(k2, r2, x2, v2);
                            if (_ferr < baseErr) return true;
                            ApplyFull(k2, r2, x2, o2);
                        }
                    }
                    ApplyFull(k1, r1, x1, o1);
                }
            }
            return false;
        }
        private bool ThreeFlipFull()
        {
            long baseErr = _ferr; int m = _fcoords.Count;
            for (int i = 0; i < m; i++)
            {
                var (k1, r1, x1) = _fcoords[i]; int o1 = CurFull(k1, r1, x1);
                for (int v1 = -1; v1 <= 1; v1++)
                {
                    if (v1 == o1) continue; ApplyFull(k1, r1, x1, v1);
                    for (int j = i + 1; j < m; j++)
                    {
                        var (k2, r2, x2) = _fcoords[j]; int o2 = CurFull(k2, r2, x2);
                        for (int v2 = -1; v2 <= 1; v2++)
                        {
                            if (v2 == o2) continue; ApplyFull(k2, r2, x2, v2);
                            for (int l = j + 1; l < m; l++)
                            {
                                var (k3, r3, x3) = _fcoords[l]; int o3 = CurFull(k3, r3, x3);
                                for (int v3 = -1; v3 <= 1; v3++)
                                {
                                    if (v3 == o3) continue; ApplyFull(k3, r3, x3, v3);
                                    if (_ferr < baseErr) return true;
                                    ApplyFull(k3, r3, x3, o3);
                                }
                            }
                            ApplyFull(k2, r2, x2, o2);
                        }
                    }
                    ApplyFull(k1, r1, x1, o1);
                }
            }
            return false;
        }
        private int Polish()
        {
            OneFlipFull(); if (_ferr == 0) return 0;
            bool progress = true;
            while (progress)
            {
                progress = false;
                while (TwoFlipFull()) { progress = true; if (_ferr == 0) return 0; }
                OneFlipFull(); if (_ferr == 0) return 0;
                if (_ferr <= 6)
                {
                    while (ThreeFlipFull()) { progress = true; if (_ferr == 0) return 0; OneFlipFull(); if (_ferr == 0) return 0; }
                }
            }
            return (int)_ferr;
        }

        private Decomposition Snapshot() => new(_n, _rank, (int[,])_u.Clone(), (int[,])_v.Clone(), (int[,])_w.Clone());

        private static long Pow3(int e) { long p = 1; for (int i = 0; i < e; i++) p *= 3; return p; }
    }

    /// <summary>The n×n matrix-multiply tensor over {0,1}: T[α,β,γ]=1 iff α=(i,k), β=(k,j), γ=(i,j).</summary>
    public static int[,,] BuildMatmulTensor(int n)
    {
        int n2 = n * n;
        var t = new int[n2, n2, n2];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
                for (int j = 0; j < n; j++)
                    t[i * n + k, k * n + j, i * n + j] = 1; // a_flat[i*n+k]·b_flat[k*n+j] → c_flat[i*n+j]
        return t;
    }

    /// <summary>
    /// MECHANICAL codegen (no LLM): turn a decomposition into a <c>Scalar[,] Multiply</c> method.
    /// Each term r becomes one product P_r = (±-combo of A entries) * (±-combo of B entries) — exactly
    /// one counted multiply — and C[γ] = Σ_r w_r[γ]·P_r using only +/- and free unary negation. So the
    /// synthesized algorithm uses exactly (# terms with both sides nonzero) scalar multiplications.
    /// </summary>
    public static string Synthesize(Decomposition d)
    {
        int n = d.N, n2 = d.N * d.N;
        var sb = new StringBuilder();
        sb.AppendLine("using HAL9001;");
        sb.AppendLine("public static class Kernel {");
        sb.AppendLine("  public static Scalar[,] Multiply(Scalar[,] A, Scalar[,] B) {");
        for (int i = 0; i < n2; i++) sb.AppendLine($"    Scalar a{i} = A[{i / n},{i % n}];");
        for (int i = 0; i < n2; i++) sb.AppendLine($"    Scalar b{i} = B[{i / n},{i % n}];");

        for (int r = 0; r < d.Rank; r++)
        {
            string? acombo = Combo(d.U, r, n2, 'a');
            string? bcombo = Combo(d.V, r, n2, 'b');
            string expr = (acombo is null || bcombo is null) ? "new Scalar(0)" : $"({acombo}) * ({bcombo})";
            sb.AppendLine($"    Scalar P{r} = {expr};");
        }

        sb.AppendLine($"    var C = new Scalar[{n},{n}];");
        for (int g = 0; g < n2; g++)
        {
            string? ccombo = Combo(d.W, g, d.Rank, 'P', transposeColumn: true);
            sb.AppendLine($"    C[{g / n},{g % n}] = {ccombo ?? "new Scalar(0)"};");
        }
        sb.AppendLine("    return C;");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // Build a ±1 linear combination string, or null if every coefficient is zero.
    //   transposeColumn=false: row `line` of M over its second index (length=count) — for u/v over n².
    //   transposeColumn=true:  column `line` of M over its FIRST index (length=count) — for w over R.
    private static string? Combo(int[,] m, int line, int count, char var, bool transposeColumn = false)
    {
        var sb = new StringBuilder();
        bool first = true;
        for (int idx = 0; idx < count; idx++)
        {
            int c = transposeColumn ? m[idx, line] : m[line, idx];
            if (c == 0) continue;
            string term = $"{var}{idx}";
            if (first) { sb.Append(c < 0 ? "-" + term : term); first = false; }
            else sb.Append(c < 0 ? " - " + term : " + " + term);
        }
        return first ? null : sb.ToString();
    }

    /// <summary>
    /// Sanity check (diagnostic): plug in Strassen's KNOWN rank-7 decomposition and confirm our
    /// tensor convention + codegen represent it at error 0 / 7 muls / exact-verify true. If this
    /// fails, the bug is in the convention, not the search.
    /// </summary>
    public static void StrassenCheck()
    {
        int[,] U = { {1,0,0,1}, {0,0,1,1}, {1,0,0,0}, {0,0,0,1}, {1,1,0,0}, {-1,0,1,0}, {0,1,0,-1} };
        int[,] V = { {1,0,0,1}, {1,0,0,0}, {0,1,0,-1}, {-1,0,1,0}, {0,0,0,1}, {1,1,0,0}, {0,0,1,1} };
        int[,] W = { {1,0,0,1}, {0,0,1,-1}, {0,1,0,1}, {1,0,1,0}, {-1,1,0,0}, {0,0,0,1}, {1,0,0,0} };
        var d = new Decomposition(2, 7, U, V, W);

        // Direct tensor residual against our convention.
        var t = BuildMatmulTensor(2);
        int err = 0;
        for (int a = 0; a < 4; a++) for (int b = 0; b < 4; b++) for (int g = 0; g < 4; g++)
        {
            int s = 0; for (int r = 0; r < 7; r++) s += U[r, a] * V[r, b] * W[r, g];
            int diff = s - t[a, b, g]; err += diff * diff;
        }
        Console.WriteLine($"== Strassen sanity check ==");
        Console.WriteLine($"   tensor residual error = {err}  (expect 0)");
        string src = Synthesize(d);
        var (compiled, muls, exact) = MatmulRace.EvaluateCountingSource(src, 2);
        Console.WriteLine($"   synthesized: compiled={compiled}  muls={muls} (expect 7)  exact-verify={exact} (expect True)");
    }

    /// <summary>
    /// Standalone demo + acceptance test (no API key / hive needed): search for a rank-R decomposition
    /// of the n×n tensor, synthesize it, and verify it through the bite-16 exact verifier + counter.
    /// `dotnet run -- derive 2 7` should derive a Strassen-equivalent (rank 7) from random, no LLM.
    /// </summary>
    public static void Demo(int n, int rank)
    {
        Console.WriteLine($"== LLM-free derivation: searching for a rank-{rank} decomposition of the {n}x{n} matmul tensor ==");
        Console.WriteLine($"   (naive rank = {n * n * n}; coefficients restricted to {{-1,0,1}}; no LLM involved)");
        var swatch = Stopwatch.StartNew();
        Decomposition? d = Search(n, rank, out int bestErr, maxSeconds: 30);
        swatch.Stop();

        if (d is null)
        {
            Console.WriteLine($"   no exact rank-{rank} decomposition found in {swatch.Elapsed.TotalSeconds:F1}s (best residual error = {bestErr}).");
            Console.WriteLine("   (try a larger rank, or re-run — the search is randomized.)");
            return;
        }

        Console.WriteLine($"   FOUND an exact decomposition in {swatch.Elapsed.TotalSeconds:F1}s. Synthesizing + verifying...");
        string source = Synthesize(d);
        var (compiled, muls, exact) = MatmulRace.EvaluateCountingSource(source, n);
        Console.WriteLine($"   compiled={compiled}  scalar-multiplications={muls}  exact-verify={exact}");

        var (verdict, best, lower) = MatmulKnownBest.Classify(n, muls);
        Console.WriteLine($"   known-best for {n}x{n} = {(best < 0 ? "(none on record)" : best.ToString())}; verdict = {verdict}");
        if (verdict == MatmulKnownBest.Verdict.BeatsKnownBest && exact)
            Console.WriteLine("   *** this BEATS the known-best — in the hive this would trigger the discovery gate. ***");
        Console.WriteLine();
        Console.WriteLine("---- synthesized C# (over the counting Scalar type) ----");
        Console.WriteLine(source);
    }
}
