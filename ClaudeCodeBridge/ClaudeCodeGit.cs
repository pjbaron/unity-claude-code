using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace ClaudeCodeBridge
{
    public class GitCommit
    {
        public string Hash;
        public string Author;
        public string Date;
        public string Message;
    }

    internal static class ClaudeCodeGit
    {
        private static bool RunGit(string args, string workingDir, out string stdout, out string stderr)
        {
            stdout = string.Empty;
            stderr = string.Empty;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "git",
                    Arguments              = args,
                    WorkingDirectory       = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };

                using (var process = Process.Start(psi))
                {
                    process.WaitForExit();
                    stdout = process.StandardOutput.ReadToEnd();
                    stderr = process.StandardError.ReadToEnd();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[CCT-git] git not found on PATH: " + ex.Message);
                stdout = string.Empty;
                stderr = string.Empty;
                return false;
            }
        }

        public static bool IsGitRepo(string dir)
        {
            return RunGit("rev-parse --is-inside-work-tree", dir, out _, out _);
        }

        public static bool Init(string dir)
        {
            if (!RunGit("init", dir, out _, out string stderr))
            {
                UnityEngine.Debug.LogWarning("[CCT-git] git init failed: " + stderr);
                return false;
            }
            string gitignorePath = System.IO.Path.Combine(dir, ".gitignore");
            if (!System.IO.File.Exists(gitignorePath))
            {
                System.IO.File.WriteAllText(gitignorePath,
                    "# Unity generated\n" +
                    "Library/\n" +
                    "Temp/\n" +
                    "Logs/\n" +
                    "UserSettings/\n" +
                    "obj/\n" +
                    "\n" +
                    "# Build output\n" +
                    "Build/\n" +
                    "Builds/\n" +
                    "\n" +
                    "# IDE and project files\n" +
                    "*.csproj\n" +
                    "*.sln\n" +
                    ".vs/\n" +
                    ".idea/\n" +
                    "\n" +
                    "# OS\n" +
                    ".DS_Store\n" +
                    "Thumbs.db\n");
            }
            return true;
        }

        public static string Status(string dir)
        {
            if (!RunGit("status", dir, out string stdout, out _))
                return "[git] git not available";
            return stdout;
        }

        public static bool HasChanges(string dir)
        {
            RunGit("status --porcelain", dir, out string stdout, out _);
            return !string.IsNullOrWhiteSpace(stdout);
        }

        public static bool Commit(string dir, string message)
        {
            RunGit("add .", dir, out _, out _);
            bool ok = RunGit("commit -m " + QuoteForShell(message), dir, out _, out string stderr);
            if (!ok)
                UnityEngine.Debug.LogWarning("[CCT-git] commit failed: " + stderr);
            return ok;
        }

        public static List<GitCommit> Log(string dir, int maxCount = 10)
        {
            var result = new List<GitCommit>();
            if (!RunGit("log --oneline -" + maxCount + " --format=%H|%an|%ad|%s --date=short", dir, out string stdout, out _))
                return result;
            foreach (string line in stdout.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('|');
                if (parts.Length >= 4)
                {
                    result.Add(new GitCommit
                    {
                        Hash    = parts[0],
                        Author  = parts[1],
                        Date    = parts[2],
                        Message = string.Join("|", parts, 3, parts.Length - 3),
                    });
                }
            }
            return result;
        }

        public static string CurrentBranch(string dir)
        {
            if (!RunGit("rev-parse --abbrev-ref HEAD", dir, out string stdout, out _))
                return string.Empty;
            return stdout.Trim();
        }

        public static bool CreateBranch(string dir, string branchName)
        {
            bool ok = RunGit("checkout -b " + branchName, dir, out _, out string stderr);
            if (!ok)
                UnityEngine.Debug.LogWarning("[CCT-git] create branch failed: " + stderr);
            return ok;
        }

        public static bool Checkout(string dir, string refspec)
        {
            bool ok = RunGit("checkout " + refspec, dir, out _, out string stderr);
            if (!ok)
                UnityEngine.Debug.LogWarning("[CCT-git] checkout failed: " + stderr);
            return ok;
        }

        private static string QuoteForShell(string s)
        {
            return '"' + s.Replace("\"", "\\\"") + '"';
        }
    }
}
