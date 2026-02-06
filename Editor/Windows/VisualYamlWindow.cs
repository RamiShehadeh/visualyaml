using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace VisualYAML
{
    public class VisualYamlWindow : EditorWindow
    {
        private List<AssetDiffEntry> _entries = new List<AssetDiffEntry>();
        private int _selectedIndex = -1;
        private DiffTreeView _treeView;
        private TreeViewState _treeState;
        private Vector2 _leftScroll;
        private string _searchFilter = "";
        private string _pendingSearch = "";
        private CompareMode _compareMode = CompareMode.LastCommit;

        // Resizable split panel
        private float _splitX = 280f;
        private bool _draggingSplit;
        private const float SplitMinLeft = 180f;
        private const float SplitMinRight = 400f;
        private const float SplitHandleWidth = 5f;

        [MenuItem("Tools/Visual YAML/Diff Tool")]
        public static void ShowWindow()
        {
            var win = GetWindow<VisualYamlWindow>("Visual YAML");
            win.minSize = new Vector2(800, 500);
        }

        private void OnEnable()
        {
            _treeState = _treeState ?? new TreeViewState();
            _splitX = EditorPrefs.GetFloat("VisualYAML_SplitX", 280f);
        }

        private void OnDisable()
        {
            EditorPrefs.SetFloat("VisualYAML_SplitX", _splitX);
        }

        private void OnGUI()
        {
            DrawToolbar();
            GUILayout.Space(2);
            DrawSplitPanel();
        }

        // --- Toolbar ---

        private void DrawToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Compare mode dropdown
            var modeLabel = "Source: " + CompareModeLabel(_compareMode);
            if (EditorGUILayout.DropdownButton(new GUIContent(modeLabel), FocusType.Passive, EditorStyles.toolbarDropDown, GUILayout.Width(180)))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Last Commit (HEAD~1 vs HEAD)"), _compareMode == CompareMode.LastCommit, () => _compareMode = CompareMode.LastCommit);
                menu.AddItem(new GUIContent("Working Tree (HEAD vs unstaged)"), _compareMode == CompareMode.WorkingTree, () => _compareMode = CompareMode.WorkingTree);
                menu.AddItem(new GUIContent("Staged (HEAD vs index)"), _compareMode == CompareMode.Staged, () => _compareMode = CompareMode.Staged);
                menu.ShowAsContext();
            }

            if (GUILayout.Button("Fetch Changes", EditorStyles.toolbarButton))
                FetchChangedAssets();

            if (GUILayout.Button("Manual Diff", EditorStyles.toolbarButton))
                ManualFileSelection();

            if (GUILayout.Button("Compare to Commit", EditorStyles.toolbarButton))
                ShowCommitPicker();

            GUILayout.FlexibleSpace();

            // Search field
            var newSearch = EditorGUILayout.TextField(_pendingSearch, EditorStyles.toolbarSearchField, GUILayout.Width(200));
            if (newSearch != _pendingSearch)
            {
                _pendingSearch = newSearch;
                if (_pendingSearch != _searchFilter)
                {
                    _searchFilter = _pendingSearch;
                    RebuildTree();
                }
            }

            GUILayout.EndHorizontal();
        }

        private static string CompareModeLabel(CompareMode mode)
        {
            switch (mode)
            {
                case CompareMode.WorkingTree: return "Working Tree";
                case CompareMode.Staged: return "Staged";
                default: return "Last Commit";
            }
        }

        // --- Split Panel ---

        private void DrawSplitPanel()
        {
            var fullRect = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (fullRect.width < 10) return;

            // Clamp split
            _splitX = Mathf.Clamp(_splitX, SplitMinLeft, fullRect.width - SplitMinRight);

            var leftRect = new Rect(fullRect.x, fullRect.y, _splitX, fullRect.height);
            var handleRect = new Rect(fullRect.x + _splitX, fullRect.y, SplitHandleWidth, fullRect.height);
            var rightRect = new Rect(fullRect.x + _splitX + SplitHandleWidth, fullRect.y, fullRect.width - _splitX - SplitHandleWidth, fullRect.height);

            // Handle dragging
            EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.ResizeHorizontal);
            if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
            {
                _draggingSplit = true;
                Event.current.Use();
            }
            if (_draggingSplit)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    _splitX = Event.current.mousePosition.x - fullRect.x;
                    _splitX = Mathf.Clamp(_splitX, SplitMinLeft, fullRect.width - SplitMinRight);
                    Event.current.Use();
                    Repaint();
                }
                if (Event.current.type == EventType.MouseUp)
                {
                    _draggingSplit = false;
                    Event.current.Use();
                }
            }

            // Draw handle
            EditorGUI.DrawRect(handleRect, new Color(0.15f, 0.15f, 0.15f, 0.3f));

            DrawAssetList(leftRect);
            DrawDiffPanel(rightRect);
        }

        // --- Left Panel: Asset List ---

        private void DrawAssetList(Rect rect)
        {
            GUILayout.BeginArea(rect, EditorStyles.helpBox);
            _leftScroll = GUILayout.BeginScrollView(_leftScroll);

            if (_entries.Count == 0)
            {
                EditorGUILayout.HelpBox("Click \"Fetch Changes\" to detect changed YAML assets from Git.", MessageType.Info);
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var isSelected = _selectedIndex == i;

                GUILayout.BeginHorizontal();

                // File icon
                var icon = GetFileIcon(e.AssetPath);
                if (icon != null)
                    GUILayout.Label(new GUIContent(icon), GUILayout.Width(18), GUILayout.Height(18));

                // Selection toggle
                bool pressed = GUILayout.Toggle(isSelected, Path.GetFileName(e.AssetPath), "Button");

                // Change count badge
                int changeCount = e.DiffResults != null ? e.DiffResults.Count : 0;
                var badge = changeCount > 0 ? changeCount.ToString() : "";
                GUILayout.Label(badge, EditorStyles.miniBoldLabel, GUILayout.Width(30));

                // Status label
                GUILayout.Label(e.ChangeType, EditorStyles.miniLabel, GUILayout.Width(80));

                GUILayout.EndHorizontal();

                if (pressed && !isSelected)
                {
                    _selectedIndex = i;
                    RebuildTree();
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // --- Right Panel: Diff View ---

        private void DrawDiffPanel(Rect rect)
        {
            GUILayout.BeginArea(rect);

            if (_selectedIndex < 0 || _selectedIndex >= _entries.Count)
            {
                EditorGUILayout.HelpBox("Select an asset on the left to view its diff.", MessageType.Info);
                GUILayout.EndArea();
                return;
            }

            var e = _entries[_selectedIndex];
            GUILayout.Label(e.AssetPath, EditorStyles.boldLabel);

            GUILayout.BeginHorizontal();
            GUILayout.Label(e.ChangeType, EditorStyles.miniBoldLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Expand All", EditorStyles.miniButtonLeft, GUILayout.Width(70)))
                _treeView?.ExpandAll();
            if (GUILayout.Button("Collapse All", EditorStyles.miniButtonRight, GUILayout.Width(80)))
                _treeView?.CollapseAll();

            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            var treeRect = GUILayoutUtility.GetRect(rect.width, rect.height - 50);
            if (_treeView != null)
                _treeView.OnGUI(treeRect);

            GUILayout.EndArea();
        }

        private void RebuildTree()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _entries.Count) return;
            _treeView = new DiffTreeView(_treeState, _entries[_selectedIndex], _searchFilter);
        }

        // --- Git Operations ---

        private void FetchChangedAssets()
        {
            _entries.Clear();
            _selectedIndex = -1;
            _treeView = null;

            var projectRoot = Application.dataPath.Replace("/Assets", "");
            var repoRoot = GitRunner.FindRepoRoot(projectRoot);
            if (string.IsNullOrEmpty(repoRoot))
            {
                EditorUtility.DisplayDialog("Visual YAML",
                    "This project is not inside a Git repository, or git is not on PATH.", "OK");
                return;
            }

            var changed = GitDiffProvider.GetChangedFiles(repoRoot, _compareMode);
            if (changed.Count == 0)
            {
                EditorUtility.DisplayDialog("Visual YAML", "No changed files detected.", "OK");
                return;
            }

            int processed = 0;
            for (int i = 0; i < changed.Count; i++)
            {
                var cf = changed[i];
                if (!GitDiffProvider.IsSupportedFile(cf.Path)) continue;

                var entry = new AssetDiffEntry
                {
                    AssetPath = cf.Path,
                    ChangeType = GitDiffProvider.StatusToLabel(cf.Status)
                };

                string current = GitDiffProvider.GetCurrentFileContent(projectRoot, cf.Path);
                string previous = null;

                if (entry.ChangeType == "Modified" || entry.ChangeType == "Renamed")
                {
                    var prevPath = cf.OldPath ?? cf.Path;
                    if (_compareMode == CompareMode.LastCommit && GitDiffProvider.HasCommits(repoRoot, 2))
                        previous = GitDiffProvider.GetFileAtCommit(repoRoot, "HEAD~1", prevPath);
                    else if (_compareMode == CompareMode.WorkingTree || _compareMode == CompareMode.Staged)
                        previous = GitDiffProvider.GetFileAtCommit(repoRoot, "HEAD", prevPath);
                }

                ComputeDiff(entry, previous ?? "", current ?? "");
                _entries.Add(entry);
                processed++;
            }

            _selectedIndex = _entries.Count > 0 ? 0 : -1;
            RebuildTree();

            if (processed == 0)
            {
                EditorUtility.DisplayDialog("Visual YAML",
                    "Changed files found, but none were supported YAML types (.prefab, .unity, .asset, .mat, etc.).", "OK");
            }
        }

        private void ManualFileSelection()
        {
            var a = EditorUtility.OpenFilePanel("Select First YAML (old)", Application.dataPath, "prefab,unity,asset,mat,yaml");
            if (string.IsNullOrEmpty(a)) return;
            var b = EditorUtility.OpenFilePanel("Select Second YAML (new)", Application.dataPath, "prefab,unity,asset,mat,yaml");
            if (string.IsNullOrEmpty(b)) return;

            var entry = new AssetDiffEntry
            {
                AssetPath = "Manual: " + Path.GetFileName(a) + " vs " + Path.GetFileName(b),
                ChangeType = "Manual"
            };

            ComputeDiff(entry, File.ReadAllText(a), File.ReadAllText(b));
            _entries.Add(entry);
            _selectedIndex = _entries.Count - 1;
            RebuildTree();
        }

        private void ShowCommitPicker()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _entries.Count)
            {
                EditorUtility.DisplayDialog("Visual YAML", "Select an asset on the left first.", "OK");
                return;
            }

            var asset = _entries[_selectedIndex].AssetPath;
            if (asset.StartsWith("Manual:"))
            {
                EditorUtility.DisplayDialog("Visual YAML", "Cannot compare manual diffs to commits.", "OK");
                return;
            }

            var projectRoot = Application.dataPath.Replace("/Assets", "");
            var repoRoot = GitRunner.FindRepoRoot(projectRoot) ?? projectRoot;
            var commits = GitDiffProvider.GetCommitHistory(repoRoot, asset, 50);

            if (commits.Count == 0)
            {
                EditorUtility.DisplayDialog("Visual YAML", "No commit history found for this file.", "OK");
                return;
            }

            var menu = new GenericMenu();
            for (int i = 0; i < commits.Count; i++)
            {
                var c = commits[i];
                var label = c.ShortHash + "  " + c.Title + "  (" + c.Date + ")";
                menu.AddItem(new GUIContent(label), false, () => CompareToCommit(asset, c.Hash, projectRoot, repoRoot));
            }
            menu.ShowAsContext();
        }

        private void CompareToCommit(string assetPath, string commitHash, string projectRoot, string repoRoot)
        {
            try
            {
                var previous = GitDiffProvider.GetFileAtCommit(repoRoot, commitHash, assetPath) ?? "";
                var current = GitDiffProvider.GetCurrentFileContent(projectRoot, assetPath) ?? "";

                var entry = new AssetDiffEntry
                {
                    AssetPath = assetPath,
                    ChangeType = "vs " + commitHash.Substring(0, Math.Min(7, commitHash.Length))
                };

                ComputeDiff(entry, previous, current);

                if (_selectedIndex >= 0 && _selectedIndex < _entries.Count)
                    _entries[_selectedIndex] = entry;
                else
                    _entries.Add(entry);

                RebuildTree();
            }
            catch (Exception e)
            {
                Debug.LogError("[VisualYAML] Compare to commit failed: " + e.Message);
            }
        }

        // --- Diff Computation ---

        private static void ComputeDiff(AssetDiffEntry entry, string oldYaml, string newYaml)
        {
            var oldDocs = UnityYamlParser.ExtractDocuments(oldYaml);
            var newDocs = UnityYamlParser.ExtractDocuments(newYaml);

            TypeResolver.ResolveMonoBehaviourNames(oldDocs);
            TypeResolver.ResolveMonoBehaviourNames(newDocs);

            entry.OldGraph = PrefabGraphBuilder.Build(oldDocs);
            entry.NewGraph = PrefabGraphBuilder.Build(newDocs);
            entry.DiffResults = DiffEngine.Diff(oldYaml, newYaml, entry.OldGraph, entry.NewGraph);
        }

        // --- Utility ---

        private static Texture GetFileIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.EndsWith(".prefab")) return EditorGUIUtility.IconContent("Prefab Icon").image;
            if (path.EndsWith(".unity")) return EditorGUIUtility.IconContent("SceneAsset Icon").image;
            if (path.EndsWith(".mat")) return EditorGUIUtility.IconContent("Material Icon").image;
            if (path.EndsWith(".asset")) return EditorGUIUtility.IconContent("ScriptableObject Icon").image;
            return null;
        }
    }
}
