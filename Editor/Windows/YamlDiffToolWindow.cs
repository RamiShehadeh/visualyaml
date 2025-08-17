#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace YamlPrefabDiff
{
    public class YamlDiffToolWindow : EditorWindow
    {
        private Vector2 _leftScroll;
        private List<AssetDiffEntry> _entries = new List<AssetDiffEntry>();
        private int _selectedIndex = -1;
        private DiffTreeView _treeView;
        private TreeViewState _treeState;

        private class CommitInfo { public string Hash; public string Title; public string Date; }

        [MenuItem("Tools/YAML Diff Tool")]
        public static void ShowWindow() { GetWindow<YamlDiffToolWindow>("YAML Diff"); }

        private void OnEnable()
        {
            _treeState = _treeState ?? new TreeViewState();
            minSize = new Vector2(900, 560);
        }

        private void OnGUI()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Fetch Changed (git)", EditorStyles.toolbarButton)) FetchChangedAssets();
            if (GUILayout.Button("Manual Diff (pick two)", EditorStyles.toolbarButton)) ManualFileSelection();
            if (GUILayout.Button("Compare to commit…", EditorStyles.toolbarButton)) ShowCommitPickerForSelected();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            var r = position;
            var left = new Rect(0, 22, 320, r.height - 22);
            var right = new Rect(325, 22, r.width - 330, r.height - 22);

            DrawLeft(left);
            DrawRight(right);
        }

        private void DrawLeft(Rect rect)
        {
            GUILayout.BeginArea(rect, (GUIStyle)"box");
            _leftScroll = GUILayout.BeginScrollView(_leftScroll);
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                GUILayout.BeginHorizontal();
                bool pressed = GUILayout.Toggle(_selectedIndex == i, Path.GetFileName(e.AssetPath), "Button");
                GUILayout.Label(e.ChangeType, GUILayout.Width(140));
                GUILayout.EndHorizontal();
                if (pressed && _selectedIndex != i) { _selectedIndex = i; BuildTree(); }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawRight(Rect rect)
        {
            GUILayout.BeginArea(rect);
            if (_selectedIndex < 0 || _selectedIndex >= _entries.Count)
            {
                EditorGUILayout.HelpBox("Select an asset on the left to view a hierarchical diff.", MessageType.Info);
                GUILayout.EndArea();
                return;
            }
            var e = _entries[_selectedIndex];
            GUILayout.Label(e.AssetPath, EditorStyles.boldLabel);
            GUILayout.Label(e.ChangeType, EditorStyles.miniBoldLabel);
            GUILayout.Space(6);

            var h = GUILayoutUtility.GetRect(rect.width, rect.height - 40);
            if (_treeView != null) _treeView.OnGUI(h);
            GUILayout.EndArea();
        }

        private void BuildTree()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _entries.Count) return;
            var e = _entries[_selectedIndex];
            _treeView = new DiffTreeView(_treeState, e);
        }

        // Git + Diff 

        private void FetchChangedAssets()
        {
            _entries.Clear();
            _selectedIndex = -1;

            var projectRoot = Application.dataPath.Replace("/Assets", "");
            var repoRoot = FindRepoRoot(projectRoot);
            if (string.IsNullOrEmpty(repoRoot))
            {
                EditorUtility.DisplayDialog("YAML Diff",
                    "This project does not appear to be inside a Git repository, or git is not available on PATH.",
                    "OK");
                return;
            }

            var changed = GetChangedFiles(repoRoot);
            if (changed.Count == 0)
            {
                EditorUtility.DisplayDialog("YAML Diff", "No changed files detected.", "OK");
                return;
            }

            for (int i = 0; i < changed.Count; i++)
            {
                var tup = changed[i];
                var status = tup.Item1;
                var rel = tup.Item2;

                // scene + prefab (you can expand filter as needed)
                if (!(rel.EndsWith(".prefab") || rel.EndsWith(".unity") || rel.EndsWith(".asset") || rel.EndsWith(".mat")))
                    continue;

                // must be inside Assets/
                if (!rel.StartsWith("Assets/")) continue;

                var entry = new AssetDiffEntry();
                entry.AssetPath = rel;
                entry.ChangeType = status.StartsWith("A") ? "Added" :
                                   status.StartsWith("D") ? "Deleted" :
                                   status.StartsWith("R") ? "Renamed" : "Modified";

                // Only diff when present on both sides
                string current = "";
                string previous = "";

                var full = Path.Combine(projectRoot, rel);
                if (File.Exists(full)) current = File.ReadAllText(full);

                if (HasAtLeastTwoCommits(repoRoot) && (entry.ChangeType == "Modified" || entry.ChangeType == "Renamed"))
                {
                    var prevShow = RunGit("show HEAD~1:" + rel, repoRoot);
                    if (prevShow.code == 0) previous = prevShow.stdout;
                }

                if (!string.IsNullOrEmpty(previous) || !string.IsNullOrEmpty(current))
                {
                    var oldDocs = UnityYamlParsing.ExtractDocuments(previous);
                    var newDocs = UnityYamlParsing.ExtractDocuments(current);
                    entry.OldGraph = PrefabGraphBuilder.Build(oldDocs);
                    entry.NewGraph = PrefabGraphBuilder.Build(newDocs);
                    entry.DiffResults = DiffEngine.Diff(previous, current, entry.OldGraph, entry.NewGraph);
                }

                _entries.Add(entry);
            }

            _selectedIndex = _entries.Count > 0 ? 0 : -1;
            BuildTree();

            if (_entries.Count == 0)
            {
                EditorUtility.DisplayDialog("YAML Diff",
                    "Changed files detected, but none were .prefab/.unity/.asset/.mat under Assets/.\n" +
                    "Adjust filters if needed.",
                    "OK");
            }
        }

        private void ManualFileSelection()
        {
            var a = EditorUtility.OpenFilePanel("Select First YAML", Application.dataPath, "prefab,unity,asset,mat,yaml");
            var b = EditorUtility.OpenFilePanel("Select Second YAML", Application.dataPath, "prefab,unity,asset,mat,yaml");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;

            var entry = new AssetDiffEntry();
            entry.AssetPath = "Manual Diff: " + Path.GetFileName(a) + " vs " + Path.GetFileName(b);
            entry.ChangeType = "Manual";

            var A = File.ReadAllText(a);
            var B = File.ReadAllText(b);

            var oldDocs = UnityYamlParsing.ExtractDocuments(A);
            var newDocs = UnityYamlParsing.ExtractDocuments(B);
            entry.OldGraph = PrefabGraphBuilder.Build(oldDocs);
            entry.NewGraph = PrefabGraphBuilder.Build(newDocs);
            entry.DiffResults = DiffEngine.Diff(A, B, entry.OldGraph, entry.NewGraph);

            _entries.Add(entry);
            _selectedIndex = _entries.Count - 1;
            BuildTree();
        }

        private void ShowCommitPickerForSelected()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _entries.Count)
            {
                EditorUtility.DisplayDialog("YAML Diff", "Select an asset on the left first.", "OK");
                return;
            }
            var asset = _entries[_selectedIndex].AssetPath;
            var repoRoot = Application.dataPath.Replace("/Assets", "");
            var commits = GetCommitHistory(asset, repoRoot, 50);
            if (commits.Count == 0)
            {
                EditorUtility.DisplayDialog("YAML Diff", "No commit history found for this file.", "OK");
                return;
            }

            var menu = new GenericMenu();
            for (int i = 0; i < commits.Count; i++)
            {
                var c = commits[i];
                var label = c.Hash.Substring(0, 7) + "  " + c.Title + "  (" + c.Date + ")";
                menu.AddItem(new GUIContent(label), false, () => CompareSelectedToCommit(asset, c.Hash));
            }
            menu.ShowAsContext();
        }

        private void CompareSelectedToCommit(string assetPath, string commitHash)
        {
            try
            {
                var projectRoot = Application.dataPath.Replace("/Assets", "");
                var prev = RunGit("show " + commitHash + ":" + assetPath, projectRoot).stdout;
                var full = Path.Combine(projectRoot, assetPath);
                var cur = File.Exists(full) ? File.ReadAllText(full) : string.Empty;

                var entry = new AssetDiffEntry
                {
                    AssetPath = assetPath,
                    ChangeType = "Compare to " + commitHash.Substring(0, 7)
                };

                var oldDocs = UnityYamlParsing.ExtractDocuments(prev);
                var newDocs = UnityYamlParsing.ExtractDocuments(cur);
                entry.OldGraph = PrefabGraphBuilder.Build(oldDocs);
                entry.NewGraph = PrefabGraphBuilder.Build(newDocs);
                entry.DiffResults = DiffEngine.Diff(prev, cur, entry.OldGraph, entry.NewGraph);

                if (_selectedIndex >= 0 && _selectedIndex < _entries.Count)
                    _entries[_selectedIndex] = entry;
                else
                    _entries.Add(entry);

                BuildTree();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("Compare to commit failed: " + e.Message);
            }
        }

        // git helpers

        private static (int code, string stdout, string stderr) RunGit(string args, string workDir)
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
                    var so = p.StandardOutput.ReadToEnd();
                    var se = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return (p.ExitCode, so, se);
                }
            }
            catch (Exception e)
            {
                return (-1, "", e.Message);
            }
        }

        private static string FindRepoRoot(string projectRoot)
        {
            var res = RunGit("rev-parse --show-toplevel", projectRoot);
            if (res.code == 0)
            {
                var path = res.stdout.Trim();
                if (Directory.Exists(path)) return path;
            }
            return null;
        }

        private static bool HasAtLeastTwoCommits(string repoRoot)
        {
            var res = RunGit("rev-list --count HEAD", repoRoot);
            if (res.code != 0) return false;
            int n;
            return int.TryParse(res.stdout.Trim(), out n) && n >= 2;
        }

        private static List<Tuple<string, string, string>> GetChangedFiles(string repoRoot)
        {
            var list = new List<Tuple<string, string, string>>();
            string args = HasAtLeastTwoCommits(repoRoot)
                ? "diff --name-status HEAD~1 HEAD --"
                : "status --porcelain";

            var res = RunGit(args, repoRoot);
            if (res.code != 0) { UnityEngine.Debug.LogError("git error: " + res.stderr); return list; }

            var lines = res.stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                string status, p1, p2 = null;
                if (args.StartsWith("status"))
                {
                    status = line.Length >= 2 ? line.Substring(0, 2).Trim() : line;
                    var rest = line.Length > 3 ? line.Substring(3).Trim() : "";
                    if (status.StartsWith("R"))
                    {
                        int arrow = rest.IndexOf("->");
                        if (arrow >= 0)
                        {
                            p1 = rest.Substring(0, arrow).Trim();
                            p2 = rest.Substring(arrow + 2).Trim();
                        }
                        else p1 = rest;
                    }
                    else p1 = rest;

                    if (status.IndexOf("M") >= 0) status = "M";
                    else if (status.IndexOf("A") >= 0) status = "A";
                    else if (status.IndexOf("D") >= 0) status = "D";
                    else status = status.Trim();
                }
                else
                {
                    var parts = line.Split('\t');
                    if (parts.Length == 2) { status = parts[0]; p1 = parts[1]; }
                    else if (parts.Length == 3) { status = parts[0]; p1 = parts[1]; p2 = parts[2]; }
                    else continue;
                }

                list.Add(new Tuple<string, string, string>(status, p1.Replace('\\', '/'), p2 != null ? p2.Replace('\\', '/') : null));
            }
            return list;
        }

        private static List<CommitInfo> GetCommitHistory(string assetPath, string repoRoot, int max)
        {
            var args = "log --follow --max-count=" + max + " --date=short --pretty=format:%H|%s|%ad -- " + assetPath;
            var res = RunGit(args, repoRoot);
            var list = new List<CommitInfo>();
            if (res.code != 0) { UnityEngine.Debug.LogError(res.stderr); return list; }

            var lines = res.stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('|');
                if (parts.Length >= 3)
                {
                    var ci = new CommitInfo();
                    ci.Hash = parts[0];
                    ci.Title = parts[1];
                    ci.Date = parts[2];
                    list.Add(ci);
                }
            }
            return list;
        }
    }
}
#endif
