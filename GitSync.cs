using System.Diagnostics;

namespace HAL9001;

/// <summary>
/// A thin wrapper around the `git` command line.
///
/// We deliberately shell out to git via <see cref="Process"/> instead of using a git
/// library, because authentication is already solved OUTSIDE this code: the repo's `origin`
/// remote points at an SSH deploy key. When git runs, SSH uses that key automatically and
/// non-interactively. So this class never sees — and never stores — a token or key.
/// </summary>
public sealed class GitSync
{
    private readonly string _repoRoot;

    private GitSync(string repoRoot) => _repoRoot = repoRoot;

    public string RepoRoot => _repoRoot;
    public string HandlersDirectory => Path.Combine(_repoRoot, "handlers");

    /// <summary>
    /// Find the repo root by walking up from the running binary (which lives in
    /// bin/Debug/netX/) until a `.git` directory appears. Returns null if we're somehow
    /// not inside a git repo, so the agent can keep working in-memory.
    /// </summary>
    public static GitSync? Discover()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return new GitSync(dir.FullName);
        }
        return null;
    }

    /// <summary>
    /// Run one git command from the repo root and capture its exit code, stdout, and stderr.
    ///
    /// We pass arguments via <see cref="ProcessStartInfo.ArgumentList"/> (not a single
    /// string) so the OS handles quoting — no manual escaping of paths or messages.
    /// UseShellExecute=false is what lets us redirect and read the streams.
    /// </summary>
    public (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start 'git' — is it installed and on PATH?");

        // Read BOTH streams concurrently, then WaitForExit. If we fully read one stream
        // while the other's OS pipe buffer fills up, git would block writing to it and we'd
        // deadlock. Kicking off both async reads first avoids that classic trap.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>Print the remote URL and current branch so you can see where pushes go.</summary>
    public void PrintRemoteAndBranch()
    {
        var (_, url, _) = Run("remote", "get-url", "origin");
        var (_, branch, _) = Run("rev-parse", "--abbrev-ref", "HEAD");
        Console.WriteLine($"  remote: {url.Trim()}");
        Console.WriteLine($"  branch: {branch.Trim()}");
    }

    /// <summary>
    /// Stage, commit, and push a single file. Every step's exit code and output is printed
    /// so a failed push is debuggable. Returns false (never throws) on the first nonzero
    /// exit, so the caller can carry on with the handler still live in memory.
    /// </summary>
    public bool CommitAndPushFile(string absolutePath, string commitMessage)
    {
        // git wants a repo-relative, forward-slashed path.
        string relative = Path.GetRelativePath(_repoRoot, absolutePath).Replace('\\', '/');

        // Commit with an explicit pathspec ("-- <file>") so we ONLY ever commit this one
        // file, even if something else happens to be staged in the working tree.
        var steps = new (string Label, string[] Args)[]
        {
            ("add",    new[] { "add", "--", relative }),
            ("commit", new[] { "commit", "-m", commitMessage, "--", relative }),
            ("push",   new[] { "push" }),
        };

        foreach (var (label, args) in steps)
        {
            var (exit, stdout, stderr) = Run(args);

            Console.WriteLine($"  [git {label}] exit {exit}");
            if (stdout.Trim().Length > 0) Console.WriteLine(Indent(stdout));
            // git prints normal progress (e.g. "To github.com:...") to STDERR, so we show
            // it on success too — it's not necessarily an error.
            if (stderr.Trim().Length > 0) Console.WriteLine(Indent(stderr));

            if (exit != 0)
            {
                Console.WriteLine(
                    $"  [git {label}] FAILED (exit {exit}). The handler still works in memory for " +
                    "this session; it just wasn't synced to GitHub.");
                return false;
            }
        }

        return true;
    }

    private static string Indent(string text) =>
        "    " + text.TrimEnd().Replace("\n", "\n    ");
}
