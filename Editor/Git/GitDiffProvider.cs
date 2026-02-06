using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VisualYAML
{
    internal enum CompareMode
    {
        LastCommit,       // HEAD~1 vs HEAD
        WorkingTree,      // HEAD vs working tree (unstaged)
        Staged,           // HEAD vs staged (index)
    }

    internal class ChangedFile
    {
        public string Status;   // M, A, D, R
        public string Path;
        public string OldPath;  // Non-null for renames
    }

    internal class CommitInfo
    {
        public string Hash;
        public string Title;
        public string Date;

        public string ShortHash => Hash != null && Hash.Length >= 7 ? Hash.Substring(0, 7) : Hash;
    }

    internal static class GitDiffProvider
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".asset", ".mat", ".controller", ".overrideController",
            ".physicMaterial", ".physicsMaterial2D", ".lighting", ".signal"
        };

        public static bool IsSupportedFile(string path)
        {
            var ext = System.IO.Path.GetExtension(path);
            return SupportedExtensions.Contains(ext);
        }

        public static bool HasCommits(string repoRoot, int minCount = 1)
        {
            var res = GitRunner.Run("rev-list --count HEAD", repoRoot);
            if (!res.Success) return false;
            return int.TryParse(res.Stdout.Trim(), out int n) && n >= minCount;
        }

        public static List<ChangedFile> GetChangedFiles(string repoRoot, CompareMode mode)
        {
            string args;
            switch (mode)
            {
                case CompareMode.WorkingTree:
                    args = "diff --name-status HEAD --";
                    break;
                case CompareMode.Staged:
                    args = "diff --name-status --cached HEAD --";
                    break;
                case CompareMode.LastCommit:
                default:
                    args = HasCommits(repoRoot, 2)
                        ? "diff --name-status -M HEAD~1 HEAD --"
                        : "status --porcelain";
                    break;
            }

            var res = GitRunner.Run(args, repoRoot);
            if (!res.Success)
            {
                Debug.LogError("[VisualYAML] git error: " + res.Stderr);
                return new List<ChangedFile>();
            }

            return args.StartsWith("status")
                ? ParsePorcelainOutput(res.Stdout)
                : ParseDiffNameStatus(res.Stdout);
        }

        public static List<ChangedFile> GetChangedFilesBetweenCommits(string repoRoot, string fromCommit, string toCommit)
        {
            var args = "diff --name-status -M " + fromCommit + " " + toCommit + " --";
            var res = GitRunner.Run(args, repoRoot);
            if (!res.Success)
            {
                Debug.LogError("[VisualYAML] git error: " + res.Stderr);
                return new List<ChangedFile>();
            }
            return ParseDiffNameStatus(res.Stdout);
        }

        public static string GetFileAtCommit(string repoRoot, string commit, string filePath)
        {
            var res = GitRunner.Run("show " + commit + ":" + filePath, repoRoot);
            return res.Success ? res.Stdout : null;
        }

        public static string GetCurrentFileContent(string projectRoot, string relativePath)
        {
            var full = System.IO.Path.Combine(projectRoot, relativePath);
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }

        public static List<CommitInfo> GetCommitHistory(string repoRoot, string assetPath, int max = 50)
        {
            var args = "log --follow --max-count=" + max +
                       " --date=short --pretty=format:%H|%s|%ad -- " + assetPath;
            var res = GitRunner.Run(args, repoRoot);
            var list = new List<CommitInfo>();
            if (!res.Success) return list;

            var lines = res.Stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length >= 3)
                {
                    list.Add(new CommitInfo
                    {
                        Hash = parts[0],
                        Title = parts[1],
                        Date = parts[2]
                    });
                }
            }
            return list;
        }

        // --- Parsing helpers ---

        private static List<ChangedFile> ParseDiffNameStatus(string output)
        {
            var list = new List<ChangedFile>();
            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('\t');
                if (parts.Length < 2) continue;

                var cf = new ChangedFile { Status = NormalizeStatus(parts[0]), Path = parts[parts.Length - 1].Replace('\\', '/') };
                if (parts.Length >= 3)
                    cf.OldPath = parts[1].Replace('\\', '/');
                list.Add(cf);
            }
            return list;
        }

        private static List<ChangedFile> ParsePorcelainOutput(string output)
        {
            var list = new List<ChangedFile>();
            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length < 4) continue;

                var statusStr = line.Substring(0, 2).Trim();
                var rest = line.Substring(3).Trim();

                var cf = new ChangedFile();

                if (statusStr.Contains("R") || rest.Contains("->"))
                {
                    int arrow = rest.IndexOf("->");
                    if (arrow >= 0)
                    {
                        cf.OldPath = rest.Substring(0, arrow).Trim().Replace('\\', '/');
                        cf.Path = rest.Substring(arrow + 2).Trim().Replace('\\', '/');
                    }
                    else
                    {
                        cf.Path = rest.Replace('\\', '/');
                    }
                    cf.Status = "R";
                }
                else
                {
                    cf.Path = rest.Replace('\\', '/');
                    cf.Status = NormalizeStatus(statusStr);
                }

                list.Add(cf);
            }
            return list;
        }

        private static string NormalizeStatus(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "M";
            // git diff --name-status can output R100, R095, etc. for renames
            if (raw.StartsWith("R")) return "R";
            if (raw.StartsWith("A")) return "A";
            if (raw.StartsWith("D")) return "D";
            if (raw.StartsWith("M")) return "M";
            return raw.Trim();
        }

        public static string StatusToLabel(string status)
        {
            switch (status)
            {
                case "A": return "Added";
                case "D": return "Deleted";
                case "R": return "Renamed";
                default: return "Modified";
            }
        }
    }
}
