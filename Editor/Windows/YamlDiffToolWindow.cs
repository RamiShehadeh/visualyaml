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
        private List<AssetDiffEntry> _entries = new();
        private int _selectedIndex = -1;
        private DiffTreeView _treeView;
        private TreeViewState _treeState;

        [MenuItem("Window/YAML Prefab Diff")] public static void ShowWindow() => GetWindow<YamlDiffToolWindow>("YAML Prefab Diff");

        private void OnEnable()
        {
            _treeState ??= new TreeViewState();
            minSize = new Vector2(820, 520);
        }

        private void OnGUI()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Fetch Changed (git diff)", EditorStyles.toolbarButton)) FetchChangedAssets();
            if (GUILayout.Button("Manual Diff (pick two)", EditorStyles.toolbarButton)) ManualFileSelection();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // 2-pane layout
            Rect r = position;
            var left = new Rect(0, 22, 280, r.height - 22);
            var right = new Rect(285, 22, r.width - 290, r.height - 22);

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
                var pressed = GUILayout.Toggle(_selectedIndex == i, Path.GetFileName(e.AssetPath), "Button");
                GUILayout.Label(e.ChangeType, GUILayout.Width(80));
                GUILayout.EndHorizontal();
                if (pressed && _selectedIndex != i)
                {
                    _selectedIndex = i; BuildTree();
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawRight(Rect rect)
        {
            GUILayout.BeginArea(rect);
            if (_selectedIndex < 0 || _selectedIndex >= _entries.Count)
            {
                EditorGUILayout.HelpBox("Select an asset on the left to view hierarchical diff.", MessageType.Info);
                GUILayout.EndArea();
                return;
            }
            var e = _entries[_selectedIndex];
            GUILayout.Label(e.AssetPath, EditorStyles.boldLabel);
            GUILayout.Label(e.ChangeType, EditorStyles.miniBoldLabel);
            GUILayout.Space(6);

            var h = GUILayoutUtility.GetRect(rect.width, rect.height - 40);
            _treeView?.OnGUI(h);
            GUILayout.EndArea();
        }

        private void BuildTree()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _entries.Count) return;
            var e = _entries[_selectedIndex];
            _treeView = new DiffTreeView(_treeState, e);
        }

        private void FetchChangedAssets()
        {
            _entries.Clear();
            try
            {
                var repoRoot = Application.dataPath.Replace("/Assets", "");
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "diff --name-status HEAD~1 HEAD",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var p = Process.Start(psi);
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    var code = parts[0].Trim();
                    var path = parts[1].Trim();
                    if (!path.EndsWith(".prefab") && !path.EndsWith(".asset")) continue;

                    var entry = new AssetDiffEntry
                    {
                        AssetPath = path,
                        ChangeType = code == "A" ? "Added" : code == "D" ? "Deleted" : "Modified"
                    };

                    if (code == "M")
                    {
                        var full = Path.Combine(Application.dataPath.Replace("/Assets", ""), path);
                        var cur = File.Exists(full) ? File.ReadAllText(full) : string.Empty;
                        var prev = GetFileFromGit(path, "HEAD~1");

                        // graphs for hierarchy resolution
                        var oldDocs = UnityYamlParsing.ExtractDocuments(prev);
                        var newDocs = UnityYamlParsing.ExtractDocuments(cur);
                        entry.OldGraph = PrefabGraphBuilder.Build(oldDocs);
                        entry.NewGraph = PrefabGraphBuilder.Build(newDocs);

                        entry.DiffResults = DiffEngine.Diff(prev, cur, entry.OldGraph, entry.NewGraph);
                    }

                    _entries.Add(entry);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"git diff error: {e.Message}");
            }
            _selectedIndex = _entries.Count > 0 ? 0 : -1;
            BuildTree();
        }

        private void ManualFileSelection()
        {
            var a = EditorUtility.OpenFilePanel("Select First YAML", Application.dataPath, "prefab,asset,yaml");
            var b = EditorUtility.OpenFilePanel("Select Second YAML", Application.dataPath, "prefab,asset,yaml");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;

            var entry = new AssetDiffEntry
            {
                AssetPath = $"Manual Diff: {Path.GetFileName(a)} vs {Path.GetFileName(b)}",
                ChangeType = "Manual"
            };

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

        private string GetFileFromGit(string assetPath, string commit)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"show {commit}:{assetPath}",
                    WorkingDirectory = Application.dataPath.Replace("/Assets", ""),
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var p = Process.Start(psi);
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return output;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"git show error: {e.Message}");
                return string.Empty;
            }
        }
    }
}
#endif