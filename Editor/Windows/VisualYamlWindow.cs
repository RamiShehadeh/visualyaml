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

        // Asset list layout
        private const float AssetRowHeight = 22f;
        private const float AssetIconSize = 18f;
        private const float AssetBadgeWidth = 30f;
        private const float AssetStatusWidth = 80f;

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
            // Toolbar uses GUILayout — self-contained, no mixing
            DrawToolbar();

            // Everything below is pure Rect-based (no GUILayout) to avoid
            // Layout/Repaint control-count mismatches from BeginArea/GetRect
            float toolbarH = EditorStyles.toolbar.fixedHeight;
            if (toolbarH < 1) toolbarH = 20f;
            float contentY = toolbarH + 2f;
            var contentRect = new Rect(0, contentY, position.width, position.height - contentY);

            if (contentRect.height < 10 || contentRect.width < 10) return;

            // Compute split rects
            float maxSplit = Mathf.Max(SplitMinLeft, contentRect.width - SplitMinRight);
            _splitX = Mathf.Clamp(_splitX, SplitMinLeft, maxSplit);

            var leftRect = new Rect(contentRect.x, contentRect.y, _splitX, contentRect.height);
            var handleRect = new Rect(contentRect.x + _splitX, contentRect.y, SplitHandleWidth, contentRect.height);
            var rightRect = new Rect(contentRect.x + _splitX + SplitHandleWidth, contentRect.y,
                Mathf.Max(0, contentRect.width - _splitX - SplitHandleWidth), contentRect.height);

            // Split handle interaction
            EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.ResizeHorizontal);
            HandleSplitDrag(contentRect, maxSplit);
            EditorGUI.DrawRect(handleRect, new Color(0.15f, 0.15f, 0.15f, 0.3f));

            // Draw panels — pure Rect-based
            DrawAssetList(leftRect);
            DrawDiffPanel(rightRect);
        }

        // --- Toolbar (GUILayout, self-contained) ---

        private void DrawToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

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

        // --- Split handle dragging ---

        private void HandleSplitDrag(Rect contentRect, float maxSplit)
        {
            if (Event.current.type == EventType.MouseDown)
            {
                var handleRect = new Rect(contentRect.x + _splitX, contentRect.y, SplitHandleWidth, contentRect.height);
                if (handleRect.Contains(Event.current.mousePosition))
                {
                    _draggingSplit = true;
                    Event.current.Use();
                }
            }
            if (_draggingSplit)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    _splitX = Event.current.mousePosition.x - contentRect.x;
                    _splitX = Mathf.Clamp(_splitX, SplitMinLeft, maxSplit);
                    Event.current.Use();
                    Repaint();
                }
                if (Event.current.type == EventType.MouseUp)
                {
                    _draggingSplit = false;
                    Event.current.Use();
                }
            }
        }

        // --- Left Panel: Asset List (pure Rect-based) ---

        private void DrawAssetList(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

            var innerRect = new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4);

            if (_entries.Count == 0)
            {
                EditorGUI.HelpBox(innerRect, "Click \"Fetch Changes\" to detect changed YAML assets from Git.", MessageType.Info);
                return;
            }

            float contentHeight = _entries.Count * AssetRowHeight;
            var viewRect = new Rect(0, 0, innerRect.width - 16, contentHeight);

            _leftScroll = GUI.BeginScrollView(innerRect, _leftScroll, viewRect);

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var isSelected = _selectedIndex == i;
                var rowRect = new Rect(0, i * AssetRowHeight, viewRect.width, AssetRowHeight);

                // Selection highlight
                if (isSelected)
                    EditorGUI.DrawRect(rowRect, new Color(0.24f, 0.48f, 0.9f, 0.3f));

                // Hover highlight
                if (!isSelected && rowRect.Contains(Event.current.mousePosition))
                    EditorGUI.DrawRect(rowRect, new Color(0.5f, 0.5f, 0.5f, 0.1f));

                float x = rowRect.x + 4;

                // File icon
                var icon = GetFileIcon(e.AssetPath);
                if (icon != null)
                    GUI.DrawTexture(new Rect(x, rowRect.y + 2, AssetIconSize, AssetIconSize), icon, ScaleMode.ScaleToFit);
                x += AssetIconSize + 4;

                // File name
                float nameWidth = Mathf.Max(60, rowRect.width - AssetIconSize - AssetBadgeWidth - AssetStatusWidth - 16);
                EditorGUI.LabelField(new Rect(x, rowRect.y, nameWidth, rowRect.height), Path.GetFileName(e.AssetPath));
                x += nameWidth;

                // Change count badge
                int changeCount = e.DiffResults != null ? e.DiffResults.Count : 0;
                if (changeCount > 0)
                    EditorGUI.LabelField(new Rect(x, rowRect.y, AssetBadgeWidth, rowRect.height),
                        changeCount.ToString(), EditorStyles.miniBoldLabel);
                x += AssetBadgeWidth;

                // Status label
                EditorGUI.LabelField(new Rect(x, rowRect.y, AssetStatusWidth, rowRect.height),
                    e.ChangeType, EditorStyles.miniLabel);

                // Click to select
                if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
                {
                    _selectedIndex = i;
                    RebuildTree();
                    Event.current.Use();
                    Repaint();
                }
            }

            GUI.EndScrollView();
        }

        // --- Right Panel: Diff View (pure Rect-based) ---

        private void DrawDiffPanel(Rect rect)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _entries.Count)
            {
                EditorGUI.HelpBox(rect, "Select an asset on the left to view its diff.", MessageType.Info);
                return;
            }

            var e = _entries[_selectedIndex];
            float y = rect.y;

            // Asset path label
            EditorGUI.LabelField(new Rect(rect.x, y, rect.width, 20), e.AssetPath, EditorStyles.boldLabel);
            y += 20;

            // Status + expand/collapse buttons
            EditorGUI.LabelField(new Rect(rect.x, y, 200, 18), e.ChangeType, EditorStyles.miniBoldLabel);

            float btnX = rect.xMax - 158;
            if (GUI.Button(new Rect(btnX, y, 74, 18), "Expand All", EditorStyles.miniButtonLeft))
                _treeView?.ExpandAll();
            if (GUI.Button(new Rect(btnX + 74, y, 84, 18), "Collapse All", EditorStyles.miniButtonRight))
                _treeView?.CollapseAll();
            y += 22;

            // Tree view fills remaining space
            var treeRect = new Rect(rect.x, y, rect.width, Mathf.Max(0, rect.yMax - y));
            if (_treeView != null && treeRect.height > 1)
                _treeView.OnGUI(treeRect);
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
