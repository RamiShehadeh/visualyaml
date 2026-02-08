using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace AssetDiff
{
    public class AssetDiffWindow : EditorWindow
    {
        private List<AssetDiffEntry> _entries = new List<AssetDiffEntry>();
        private int _selectedIndex = -1;
        private DiffTreeView _treeView;
        private TreeViewState _treeState;
        private Vector2 _leftScroll;
        private string _searchFilter = "";
        private string _pendingSearch = "";
        private CompareMode _compareMode = CompareMode.LastCommit;

        // Branch comparison state
        private string _baseBranch;          // selected base branch for Branch mode
        private string _currentBranch;       // cached current branch name
        private string _cachedRepoRoot;      // cached repo root path

        // Resizable split panel
        private float _splitX = 280f;
        private bool _draggingSplit;
        private const float SplitMinLeft = 180f;
        private const float SplitMinRight = 400f;
        private const float SplitHandleWidth = 5f;

        // Asset list layout
        private const float AssetRowHeight = 26f;
        private const float AssetIconSize = 18f;
        private const float AssetBadgeWidth = 34f;
        private const float AssetStatusWidth = 70f;

        // Colors matching the tree view
        private static readonly Color SelectionColor = new Color(0.24f, 0.48f, 0.9f, 0.35f);
        private static readonly Color HoverColor = new Color(0.5f, 0.5f, 0.5f, 0.12f);
        private static readonly Color StatusAddedColor = new Color(0.40f, 0.87f, 0.55f);
        private static readonly Color StatusModifiedColor = new Color(0.95f, 0.82f, 0.40f);
        private static readonly Color StatusDeletedColor = new Color(0.95f, 0.50f, 0.45f);
        private static readonly Color StatusRenamedColor = new Color(0.55f, 0.75f, 0.95f);
        private static readonly Color BranchColor = new Color(0.55f, 0.75f, 0.95f);
        private static readonly Color DimColor = new Color(0.55f, 0.55f, 0.55f);

        private GUIStyle _statusStyle;
        private GUIStyle _badgeLabelStyle;
        private GUIStyle _headerPathStyle;
        private GUIStyle _headerStatusStyle;
        private GUIStyle _branchInfoStyle;

        [MenuItem("Tools/AssetDiff/Diff Tool")]
        public static void ShowWindow()
        {
            var win = GetWindow<AssetDiffWindow>("AssetDiff");
            win.minSize = new Vector2(800, 500);
        }

        private void OnEnable()
        {
            _treeState = _treeState ?? new TreeViewState();
            _splitX = EditorPrefs.GetFloat("AssetDiff_SplitX", 280f);
        }

        private void OnDisable()
        {
            EditorPrefs.SetFloat("AssetDiff_SplitX", _splitX);
        }

        private string GetRepoRoot()
        {
            if (_cachedRepoRoot == null)
            {
                var projectRoot = Application.dataPath.Replace("/Assets", "");
                _cachedRepoRoot = GitRunner.FindRepoRoot(projectRoot) ?? "";
            }
            return _cachedRepoRoot;
        }

        private void OnGUI()
        {
            // Toolbar uses GUILayout — self-contained, no mixing
            DrawToolbar();

            // Branch info bar (only in Branch mode)
            float extraHeaderH = 0;
            if (_compareMode == CompareMode.Branch && !string.IsNullOrEmpty(_baseBranch))
                extraHeaderH = 22f;

            float toolbarH = EditorStyles.toolbar.fixedHeight;
            if (toolbarH < 1) toolbarH = 20f;
            float contentY = toolbarH + 2f + extraHeaderH;

            // Draw branch info bar between toolbar and content
            if (extraHeaderH > 0)
            {
                EnsureWindowStyles();
                var barRect = new Rect(0, toolbarH + 1, position.width, extraHeaderH);
                EditorGUI.DrawRect(barRect, new Color(0.18f, 0.18f, 0.22f));

                var branchText = (_currentBranch ?? "HEAD") + "  vs  " + _baseBranch;
                EditorGUI.LabelField(new Rect(8, barRect.y + 1, barRect.width - 16, barRect.height - 2),
                    branchText, _branchInfoStyle);
            }

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
                menu.AddItem(new GUIContent("Last Commit (HEAD~1 vs HEAD)"), _compareMode == CompareMode.LastCommit,
                    () => { _compareMode = CompareMode.LastCommit; });
                menu.AddItem(new GUIContent("Working Tree (HEAD vs unstaged)"), _compareMode == CompareMode.WorkingTree,
                    () => { _compareMode = CompareMode.WorkingTree; });
                menu.AddItem(new GUIContent("Staged (HEAD vs index)"), _compareMode == CompareMode.Staged,
                    () => { _compareMode = CompareMode.Staged; });
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Compare to Branch (PR diff)"), _compareMode == CompareMode.Branch,
                    () => ShowBranchPicker());
                menu.ShowAsContext();
            }

            if (GUILayout.Button("Fetch Changes", EditorStyles.toolbarButton))
                FetchChangedAssets();

            // Prominent "Compare to Branch" button
            if (GUILayout.Button("Compare to Branch", EditorStyles.toolbarButton))
                ShowBranchPicker();

            if (GUILayout.Button("Manual Diff", EditorStyles.toolbarButton))
                ManualFileSelection();

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

        private string CompareModeLabel(CompareMode mode)
        {
            switch (mode)
            {
                case CompareMode.WorkingTree: return "Working Tree";
                case CompareMode.Staged: return "Staged";
                case CompareMode.Branch:
                    return !string.IsNullOrEmpty(_baseBranch) ? "vs " + _baseBranch : "Branch";
                default: return "Last Commit";
            }
        }

        // --- Branch picker ---

        private void ShowBranchPicker()
        {
            var repoRoot = GetRepoRoot();
            if (string.IsNullOrEmpty(repoRoot))
            {
                EditorUtility.DisplayDialog("AssetDiff",
                    "This project is not inside a Git repository, or git is not on PATH.", "OK");
                return;
            }

            _currentBranch = GitDiffProvider.GetCurrentBranch(repoRoot);
            var branches = GitDiffProvider.GetBranches(repoRoot);

            if (branches.Count == 0)
            {
                EditorUtility.DisplayDialog("AssetDiff",
                    "No other branches found. Create a branch and make changes to compare.", "OK");
                return;
            }

            var menu = new GenericMenu();

            // Group: common base branches at top
            bool addedSeparator = false;
            for (int i = 0; i < branches.Count; i++)
            {
                var branch = branches[i];
                var shortName = branch;
                int slash = branch.LastIndexOf('/');
                if (slash >= 0) shortName = branch.Substring(slash + 1);

                // Add separator after the prioritized branches
                if (!addedSeparator && i > 0)
                {
                    var lowerPrev = branches[i - 1].ToLowerInvariant();
                    var lowerCurr = branch.ToLowerInvariant();
                    bool prevIsCommon = lowerPrev.EndsWith("/main") || lowerPrev.EndsWith("/master") ||
                                        lowerPrev.EndsWith("/develop") || lowerPrev == "main" ||
                                        lowerPrev == "master" || lowerPrev == "develop";
                    bool currIsCommon = lowerCurr.EndsWith("/main") || lowerCurr.EndsWith("/master") ||
                                        lowerCurr.EndsWith("/develop") || lowerCurr == "main" ||
                                        lowerCurr == "master" || lowerCurr == "develop";
                    if (prevIsCommon && !currIsCommon)
                    {
                        menu.AddSeparator("");
                        addedSeparator = true;
                    }
                }

                var selected = branch == _baseBranch;
                menu.AddItem(new GUIContent(branch), selected, () => SelectBaseBranch(branch));
            }

            menu.ShowAsContext();
        }

        private void SelectBaseBranch(string branch)
        {
            _baseBranch = branch;
            _compareMode = CompareMode.Branch;
            FetchChangedAssets();
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

        // --- Styles ---

        private void EnsureWindowStyles()
        {
            if (_statusStyle != null) return;

            _statusStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 10,
                alignment = TextAnchor.MiddleRight,
                clipping = TextClipping.Clip
            };
            _badgeLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = DimColor },
                fontSize = 10
            };
            _headerPathStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                clipping = TextClipping.Clip
            };
            _headerStatusStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft
            };
            _branchInfoStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = BranchColor },
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
        }

        private static Color GetStatusColor(string changeType)
        {
            if (string.IsNullOrEmpty(changeType)) return DimColor;
            switch (changeType)
            {
                case "Added": return StatusAddedColor;
                case "Modified": return StatusModifiedColor;
                case "Deleted": return StatusDeletedColor;
                case "Renamed": return StatusRenamedColor;
                default: return DimColor;
            }
        }

        // --- Left Panel: Asset List (pure Rect-based) ---

        private void DrawAssetList(Rect rect)
        {
            EnsureWindowStyles();
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

            var innerRect = new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4);

            if (_entries.Count == 0)
            {
                var msgRect = new Rect(innerRect.x + 8, innerRect.y + innerRect.height * 0.3f, innerRect.width - 16, 60);
                EditorGUI.HelpBox(msgRect, "Click \"Fetch Changes\" or \"Compare to Branch\" to detect changed YAML assets.", MessageType.Info);
                return;
            }

            float contentHeight = _entries.Count * AssetRowHeight;
            var viewRect = new Rect(0, 0, innerRect.width - 14, contentHeight);

            _leftScroll = GUI.BeginScrollView(innerRect, _leftScroll, viewRect);

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var isSelected = _selectedIndex == i;
                var rowRect = new Rect(0, i * AssetRowHeight, viewRect.width, AssetRowHeight);

                if (isSelected)
                    EditorGUI.DrawRect(rowRect, SelectionColor);
                else if (rowRect.Contains(Event.current.mousePosition))
                    EditorGUI.DrawRect(rowRect, HoverColor);

                var stripColor = GetStatusColor(e.ChangeType);
                EditorGUI.DrawRect(new Rect(rowRect.x, rowRect.y, 3f, rowRect.height), stripColor);

                float x = rowRect.x + 8;

                var icon = GetFileIcon(e.AssetPath);
                if (icon != null)
                {
                    float iconY = rowRect.y + (rowRect.height - AssetIconSize) * 0.5f;
                    GUI.DrawTexture(new Rect(x, iconY, AssetIconSize, AssetIconSize), icon, ScaleMode.ScaleToFit);
                }
                x += AssetIconSize + 6;

                float statusAndBadge = AssetStatusWidth + AssetBadgeWidth + 8;
                float nameWidth = Mathf.Max(40, rowRect.width - x - statusAndBadge);
                EditorGUI.LabelField(new Rect(x, rowRect.y, nameWidth, rowRect.height), Path.GetFileName(e.AssetPath));

                int changeCount = e.DiffResults != null ? e.DiffResults.Count : 0;
                float rightX = rowRect.xMax;

                rightX -= AssetStatusWidth + 2;
                _statusStyle.normal.textColor = stripColor;
                EditorGUI.LabelField(new Rect(rightX, rowRect.y, AssetStatusWidth, rowRect.height),
                    e.ChangeType, _statusStyle);

                if (changeCount > 0)
                {
                    rightX -= AssetBadgeWidth;
                    EditorGUI.LabelField(new Rect(rightX, rowRect.y, AssetBadgeWidth, rowRect.height),
                        changeCount.ToString(), _badgeLabelStyle);
                }

                EditorGUI.DrawRect(new Rect(rowRect.x, rowRect.yMax - 1, rowRect.width, 1),
                    new Color(0.2f, 0.2f, 0.2f, 0.3f));

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
            EnsureWindowStyles();

            if (_selectedIndex < 0 || _selectedIndex >= _entries.Count)
            {
                var msgRect = new Rect(rect.x + 20, rect.y + rect.height * 0.35f, rect.width - 40, 60);
                EditorGUI.HelpBox(msgRect, "Select an asset on the left to view its diff.", MessageType.Info);
                return;
            }

            var e = _entries[_selectedIndex];
            float y = rect.y + 4;

            float pathWidth = rect.width - 200;
            EditorGUI.LabelField(new Rect(rect.x + 4, y, pathWidth, 20), e.AssetPath, _headerPathStyle);

            _headerStatusStyle.normal.textColor = GetStatusColor(e.ChangeType);
            EditorGUI.LabelField(new Rect(rect.x + 4 + pathWidth, y, 100, 20), e.ChangeType, _headerStatusStyle);
            y += 24;

            int totalChanges = e.DiffResults != null ? e.DiffResults.Count : 0;
            string summary = totalChanges == 1 ? "1 change" : totalChanges + " changes";
            EditorGUI.LabelField(new Rect(rect.x + 4, y, 200, 18), summary,
                new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = DimColor } });

            float btnX = rect.xMax - 166;
            if (GUI.Button(new Rect(btnX, y, 78, 18), "Expand All", EditorStyles.miniButtonLeft))
                _treeView?.ExpandAll();
            if (GUI.Button(new Rect(btnX + 78, y, 88, 18), "Collapse All", EditorStyles.miniButtonRight))
                _treeView?.CollapseAll();
            y += 22;

            EditorGUI.DrawRect(new Rect(rect.x, y, rect.width, 1), new Color(0.3f, 0.3f, 0.3f, 0.5f));
            y += 2;

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
            var repoRoot = GetRepoRoot();
            if (string.IsNullOrEmpty(repoRoot))
            {
                EditorUtility.DisplayDialog("AssetDiff",
                    "This project is not inside a Git repository, or git is not on PATH.", "OK");
                return;
            }

            _currentBranch = GitDiffProvider.GetCurrentBranch(repoRoot);

            List<ChangedFile> changed;

            if (_compareMode == CompareMode.Branch)
            {
                if (string.IsNullOrEmpty(_baseBranch))
                {
                    ShowBranchPicker();
                    return;
                }
                changed = GitDiffProvider.GetChangedFilesVsBranch(repoRoot, _baseBranch);
            }
            else
            {
                changed = GitDiffProvider.GetChangedFiles(repoRoot, _compareMode);
            }

            if (changed.Count == 0)
            {
                var msg = _compareMode == CompareMode.Branch
                    ? "No changed files between " + (_currentBranch ?? "HEAD") + " and " + _baseBranch + "."
                    : "No changed files detected.";
                EditorUtility.DisplayDialog("AssetDiff", msg, "OK");
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

                string current = null;
                string previous = null;

                if (_compareMode == CompareMode.Branch)
                {
                    // Branch mode: compare merge-base content vs HEAD content
                    current = GitDiffProvider.GetFileAtCommit(repoRoot, "HEAD", cf.Path);
                    if (entry.ChangeType == "Modified" || entry.ChangeType == "Renamed")
                    {
                        var prevPath = cf.OldPath ?? cf.Path;
                        previous = GitDiffProvider.GetFileAtMergeBase(repoRoot, _baseBranch, prevPath);
                    }
                }
                else
                {
                    current = GitDiffProvider.GetCurrentFileContent(projectRoot, cf.Path);
                    if (entry.ChangeType == "Modified" || entry.ChangeType == "Renamed")
                    {
                        var prevPath = cf.OldPath ?? cf.Path;
                        if (_compareMode == CompareMode.LastCommit && GitDiffProvider.HasCommits(repoRoot, 2))
                            previous = GitDiffProvider.GetFileAtCommit(repoRoot, "HEAD~1", prevPath);
                        else if (_compareMode == CompareMode.WorkingTree || _compareMode == CompareMode.Staged)
                            previous = GitDiffProvider.GetFileAtCommit(repoRoot, "HEAD", prevPath);
                    }
                }

                ComputeDiff(entry, previous ?? "", current ?? "");
                _entries.Add(entry);
                processed++;
            }

            _selectedIndex = _entries.Count > 0 ? 0 : -1;
            RebuildTree();

            if (processed == 0)
            {
                EditorUtility.DisplayDialog("AssetDiff",
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
