using System;
using System.Diagnostics;
using System.IO;

namespace AssetDiff
{
    internal readonly struct GitResult
    {
        public readonly int ExitCode;
        public readonly string Stdout;
        public readonly string Stderr;

        public GitResult(int exitCode, string stdout, string stderr)
        {
            ExitCode = exitCode;
            Stdout = stdout;
            Stderr = stderr;
        }

        public bool Success => ExitCode == 0;
    }

    internal static class GitRunner
    {
        private const int DefaultTimeoutMs = 30000;

        public static GitResult Run(string args, string workDir, int timeoutMs = DefaultTimeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var p = Process.Start(psi))
                {
                    // Read stdout/stderr asynchronously to avoid deadlocks on large output
                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();

                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return new GitResult(-1, "", "git command timed out after " + timeoutMs + "ms");
                    }

                    return new GitResult(p.ExitCode, stdoutTask.Result, stderrTask.Result);
                }
            }
            catch (Exception e)
            {
                return new GitResult(-1, "", e.Message);
            }
        }

        public static string FindRepoRoot(string startDir)
        {
            var res = Run("rev-parse --show-toplevel", startDir);
            if (res.Success)
            {
                var path = res.Stdout.Trim();
                if (Directory.Exists(path)) return path;
            }
            return null;
        }
    }
}
