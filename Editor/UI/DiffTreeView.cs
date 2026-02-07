using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace VisualYAML
{
    internal enum DiffTreeNodeKind { GameObject, Component, Field }

    internal class DiffTreeItem : TreeViewItem
    {
        public DiffTreeNodeKind Kind;
        public DiffResult FieldChange;       // non-null only for Field nodes
        public string ChangeType;            // added | removed | modified | mixed | null
        public int ChangeCount;              // total field-level changes under this node
        public bool IsWholeObjectChange;     // true when the entire GO/component was added or removed
        public List<DiffResult> AllChanges;  // all changes under this group (for component/GO summary)
    }

    internal class DiffTreeView : TreeView
    {
        private readonly AssetDiffEntry _entry;
        private readonly string _searchFilter;

        // Styles (lazy initialized)
        private GUIStyle _badgeStyleAdded;
        private GUIStyle _badgeStyleRemoved;
        private GUIStyle _badgeStyleModified;
        private GUIStyle _badgeStyleCount;
        private GUIStyle _valueStyleOld;
        private GUIStyle _valueStyleNew;
        private GUIStyle _arrowStyle;
        private GUIStyle _componentLabelStyle;
        private bool _stylesReady;

        // Colors
        private static readonly Color AddedBg = new Color(0.18f, 0.56f, 0.34f, 0.25f);
        private static readonly Color RemovedBg = new Color(0.62f, 0.22f, 0.20f, 0.25f);
        private static readonly Color ModifiedBg = new Color(0.60f, 0.52f, 0.20f, 0.18f);
        private static readonly Color AddedText = new Color(0.40f, 0.87f, 0.55f);
        private static readonly Color RemovedText = new Color(0.95f, 0.50f, 0.45f);
        private static readonly Color ModifiedText = new Color(0.95f, 0.82f, 0.40f);
        private static readonly Color StripAdded = new Color(0.30f, 0.82f, 0.48f);
        private static readonly Color StripRemoved = new Color(0.88f, 0.35f, 0.31f);
        private static readonly Color StripModified = new Color(0.90f, 0.76f, 0.31f);
        private static readonly Color DimText = new Color(0.6f, 0.6f, 0.6f);

        public DiffTreeView(TreeViewState state, AssetDiffEntry entry, string searchFilter = null) : base(state)
        {
            _entry = entry;
            _searchFilter = searchFilter;
            showAlternatingRowBackgrounds = true;
            rowHeight = 22f;
            Reload();
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _badgeStyleAdded = MakeBadgeStyle(AddedText);
            _badgeStyleRemoved = MakeBadgeStyle(RemovedText);
            _badgeStyleModified = MakeBadgeStyle(ModifiedText);
            _badgeStyleCount = MakeBadgeStyle(DimText);

            _valueStyleOld = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.85f, 0.55f, 0.50f) },
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                richText = false,
                fontSize = 11
            };
            _valueStyleNew = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.50f, 0.85f, 0.60f) },
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                richText = false,
                fontSize = 11
            };
            _arrowStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = DimText },
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11
            };
            _componentLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.75f, 0.75f, 0.75f) },
                fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleLeft,
                fontSize = 11
            };
        }

        private static GUIStyle MakeBadgeStyle(Color textColor)
        {
            return new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = textColor },
                alignment = TextAnchor.MiddleRight,
                clipping = TextClipping.Clip,
                fontStyle = FontStyle.Bold,
                fontSize = 10,
                padding = new RectOffset(4, 6, 0, 0)
            };
        }

        // ─────────────────────────── Tree Construction ───────────────────────────

        protected override TreeViewItem BuildRoot()
        {
            var root = new DiffTreeItem { id = 0, depth = -1, displayName = "root" };

            var results = _entry.DiffResults;
            if (results == null || results.Count == 0)
            {
                root.AddChild(new DiffTreeItem { id = 1, depth = 0, displayName = "No differences found.", Kind = DiffTreeNodeKind.Field });
                SetupDepthsFromParentsAndChildren(root);
                return root;
            }

            // Apply search filter
            if (!string.IsNullOrEmpty(_searchFilter))
            {
                var lower = _searchFilter.ToLowerInvariant();
                results = results.Where(d =>
                    Contains(d.HierarchyPath, lower) ||
                    Contains(d.ComponentType, lower) ||
                    Contains(d.FieldPath, lower) ||
                    Contains(d.OldValue, lower) ||
                    Contains(d.NewValue, lower)
                ).ToList();

                if (results.Count == 0)
                {
                    root.AddChild(new DiffTreeItem { id = 1, depth = 0, displayName = "No matches for \"" + _searchFilter + "\"", Kind = DiffTreeNodeKind.Field });
                    SetupDepthsFromParentsAndChildren(root);
                    return root;
                }
            }

            // Group by GameObject path
            var perGo = new Dictionary<string, List<DiffResult>>();
            foreach (var d in results)
            {
                var goPath = ExtractGameObjectPath(d);
                if (!perGo.TryGetValue(goPath, out var list))
                {
                    list = new List<DiffResult>();
                    perGo[goPath] = list;
                }
                list.Add(d);
            }

            var goKeys = perGo.Keys.ToList();
            goKeys.Sort(System.StringComparer.OrdinalIgnoreCase);

            int nextId = 1;

            foreach (var goKey in goKeys)
            {
                var goChanges = perGo[goKey];
                bool goIsWholeAdd = IsWholeObjectAddOrRemove(goChanges, "added");
                bool goIsWholeRemove = IsWholeObjectAddOrRemove(goChanges, "removed");
                string goChangeType = goIsWholeAdd ? "added" : goIsWholeRemove ? "removed" : DominantChange(goChanges);

                var goItem = new DiffTreeItem
                {
                    id = nextId++,
                    depth = 0,
                    displayName = string.IsNullOrEmpty(goKey) ? "(Unresolved)" : goKey,
                    Kind = DiffTreeNodeKind.GameObject,
                    ChangeType = goChangeType,
                    ChangeCount = CountFieldChanges(goChanges),
                    IsWholeObjectChange = goIsWholeAdd || goIsWholeRemove,
                    AllChanges = goChanges
                };
                root.AddChild(goItem);

                // If the entire GO was added/removed, show a single summary line instead of expanding every component
                if (goIsWholeAdd || goIsWholeRemove)
                {
                    var componentTypes = goChanges
                        .Where(d => d.IsDocumentLevel && !UnityClassIds.IsGameObject(d.ClassId ?? 0))
                        .Select(d => d.ComponentType)
                        .Where(t => !string.IsNullOrEmpty(t))
                        .Distinct()
                        .OrderBy(t => t)
                        .ToList();

                    if (componentTypes.Count > 0)
                    {
                        var summary = string.Join(", ", componentTypes);
                        goItem.AddChild(new DiffTreeItem
                        {
                            id = nextId++,
                            depth = 1,
                            displayName = summary,
                            Kind = DiffTreeNodeKind.Component,
                            ChangeType = goChangeType,
                            IsWholeObjectChange = true
                        });
                    }
                    continue;
                }

                // Normal case: group by component
                var byComp = goChanges
                    .GroupBy(d => string.IsNullOrEmpty(d.ComponentType) ? "(Unknown)" : d.ComponentType)
                    .OrderBy(g => g.Key);

                foreach (var compGroup in byComp)
                {
                    var compChanges = compGroup.ToList();
                    bool compIsWholeAdd = compChanges.All(d => d.IsDocumentLevel && d.ChangeType == "added");
                    bool compIsWholeRemove = compChanges.All(d => d.IsDocumentLevel && d.ChangeType == "removed");
                    string compChangeType = compIsWholeAdd ? "added" : compIsWholeRemove ? "removed" : DominantChange(compChanges);

                    var compItem = new DiffTreeItem
                    {
                        id = nextId++,
                        depth = 1,
                        displayName = compGroup.Key,
                        Kind = DiffTreeNodeKind.Component,
                        ChangeType = compChangeType,
                        ChangeCount = CountFieldChanges(compChanges),
                        IsWholeObjectChange = compIsWholeAdd || compIsWholeRemove,
                        AllChanges = compChanges
                    };
                    goItem.AddChild(compItem);

                    // Skip individual fields for whole-component add/remove
                    if (compIsWholeAdd || compIsWholeRemove) continue;

                    // Add field-level changes (skip document-level placeholders)
                    foreach (var ch in compChanges
                        .Where(c => !c.IsDocumentLevel)
                        .OrderBy(c => c.FieldPath))
                    {
                        compItem.AddChild(new DiffTreeItem
                        {
                            id = nextId++,
                            depth = 2,
                            displayName = PrettyFieldPath(ch.FieldPath, ch.ComponentType),
                            Kind = DiffTreeNodeKind.Field,
                            FieldChange = ch,
                            ChangeType = ch.ChangeType,
                            ChangeCount = 1
                        });
                    }
                }
            }

            if (root.children == null || root.children.Count == 0)
                root.AddChild(new DiffTreeItem { id = nextId++, depth = 0, displayName = "No differences found.", Kind = DiffTreeNodeKind.Field });

            SetupDepthsFromParentsAndChildren(root);
            return root;
        }

        // ─────────────────────────── Row Rendering ───────────────────────────

        protected override void RowGUI(RowGUIArgs args)
        {
            EnsureStyles();
            var item = (DiffTreeItem)args.item;
            var row = args.rowRect;

            // Background tint for the whole row based on change type
            if (!string.IsNullOrEmpty(item.ChangeType))
            {
                var bgColor = GetRowBackground(item.ChangeType);
                if (bgColor.a > 0)
                    EditorGUI.DrawRect(row, bgColor);
            }

            // Left color strip (3px)
            if (!string.IsNullOrEmpty(item.ChangeType))
            {
                var strip = new Rect(row.x, row.y, 3f, row.height);
                EditorGUI.DrawRect(strip, GetStripColor(item.ChangeType));
            }

            // Draw the default label (handles indentation and fold arrows)
            base.RowGUI(args);

            float indent = GetContentIndent(item);

            switch (item.Kind)
            {
                case DiffTreeNodeKind.GameObject:
                    DrawGameObjectRow(item, row, indent);
                    break;

                case DiffTreeNodeKind.Component:
                    DrawComponentRow(item, row, indent);
                    break;

                case DiffTreeNodeKind.Field:
                    DrawFieldRow(item, row, indent);
                    break;
            }
        }

        private void DrawGameObjectRow(DiffTreeItem item, Rect row, float indent)
        {
            // Right side: badge
            if (item.IsWholeObjectChange)
            {
                DrawBadge(row, item.ChangeType == "added" ? "Added" : "Removed", GetBadgeStyle(item.ChangeType));
            }
            else if (item.ChangeCount > 0)
            {
                var text = item.ChangeCount == 1 ? "1 change" : item.ChangeCount + " changes";
                DrawBadge(row, text, _badgeStyleCount);
            }
        }

        private void DrawComponentRow(DiffTreeItem item, Rect row, float indent)
        {
            if (item.IsWholeObjectChange)
            {
                // For the summary line under a whole-object GO, show "Components" dimmed label
                DrawBadge(row, item.ChangeType == "added" ? "Added" : "Removed", GetBadgeStyle(item.ChangeType));
            }
            else
            {
                // Show change summary
                string badge = SummarizeComponentBadge(item);
                DrawBadge(row, badge, GetBadgeStyle(item.ChangeType));
            }
        }

        private void DrawFieldRow(DiffTreeItem item, Rect row, float indent)
        {
            if (item.FieldChange == null) return;

            var d = item.FieldChange;
            float labelWidth = indent + 220f;
            float badgeWidth = 80f;
            float valueAreaStart = Mathf.Min(labelWidth, row.width * 0.35f);
            float valueAreaEnd = row.xMax - badgeWidth - 8f;
            float valueWidth = valueAreaEnd - (row.x + valueAreaStart);

            if (valueWidth > 80f)
            {
                var valueRect = new Rect(row.x + valueAreaStart, row.y, valueWidth, row.height);
                DrawValuePreview(valueRect, d);
            }

            // Badge on right
            string badge;
            switch (d.ChangeType)
            {
                case "added": badge = "Added"; break;
                case "removed": badge = "Removed"; break;
                default: badge = "Modified"; break;
            }
            DrawBadge(row, badge, GetBadgeStyle(d.ChangeType));
        }

        private void DrawValuePreview(Rect rect, DiffResult d)
        {
            string oldVal = LimitValue(d.OldValue, 80);
            string newVal = LimitValue(d.NewValue, 80);

            switch (d.ChangeType)
            {
                case "added":
                    EditorGUI.LabelField(rect, newVal, _valueStyleNew);
                    break;

                case "removed":
                    EditorGUI.LabelField(rect, oldVal, _valueStyleOld);
                    break;

                default: // modified
                    if (!string.IsNullOrEmpty(oldVal) && !string.IsNullOrEmpty(newVal))
                    {
                        // Show: oldValue → newValue
                        float arrowWidth = 22f;
                        float halfWidth = (rect.width - arrowWidth) * 0.5f;

                        if (halfWidth > 30f)
                        {
                            var oldRect = new Rect(rect.x, rect.y, halfWidth, rect.height);
                            var arrowRect = new Rect(rect.x + halfWidth, rect.y, arrowWidth, rect.height);
                            var newRect = new Rect(rect.x + halfWidth + arrowWidth, rect.y, halfWidth, rect.height);

                            EditorGUI.LabelField(oldRect, oldVal, _valueStyleOld);
                            EditorGUI.LabelField(arrowRect, "\u2192", _arrowStyle);
                            EditorGUI.LabelField(newRect, newVal, _valueStyleNew);
                        }
                        else
                        {
                            EditorGUI.LabelField(rect, oldVal + " \u2192 " + newVal, _valueStyleNew);
                        }
                    }
                    else if (!string.IsNullOrEmpty(newVal))
                    {
                        EditorGUI.LabelField(rect, newVal, _valueStyleNew);
                    }
                    break;
            }
        }

        private void DrawBadge(Rect row, string text, GUIStyle style)
        {
            if (string.IsNullOrEmpty(text)) return;
            float w = style.CalcSize(new GUIContent(text)).x + 8f;
            var rect = new Rect(row.xMax - w - 6f, row.y, w, row.height);
            EditorGUI.LabelField(rect, text, style);
        }

        // ─────────────────────────── Context Menu ───────────────────────────

        protected override void ContextClickedItem(int id)
        {
            var item = FindItem(id, rootItem) as DiffTreeItem;
            if (item == null) return;

            var menu = new GenericMenu();

            if (item.FieldChange != null)
            {
                var fc = item.FieldChange;
                menu.AddItem(new GUIContent("Copy Field Path"), false, () =>
                    EditorGUIUtility.systemCopyBuffer = fc.FieldPath ?? "");

                if (!string.IsNullOrEmpty(fc.OldValue))
                    menu.AddItem(new GUIContent("Copy Old Value"), false, () =>
                        EditorGUIUtility.systemCopyBuffer = fc.OldValue);

                if (!string.IsNullOrEmpty(fc.NewValue))
                    menu.AddItem(new GUIContent("Copy New Value"), false, () =>
                        EditorGUIUtility.systemCopyBuffer = fc.NewValue);
            }
            else
            {
                menu.AddItem(new GUIContent("Copy Path"), false, () =>
                    EditorGUIUtility.systemCopyBuffer = item.displayName ?? "");
            }

            menu.ShowAsContext();
        }

        // ─────────────────────────── Helpers ───────────────────────────

        private static bool Contains(string s, string lower)
        {
            return s != null && s.ToLowerInvariant().Contains(lower);
        }

        private static string ExtractGameObjectPath(DiffResult d)
        {
            var goPath = d.HierarchyPath ?? string.Empty;
            int paren = goPath.LastIndexOf('(');
            return paren > 0 ? goPath.Substring(0, paren).TrimEnd() : goPath;
        }

        /// <summary>
        /// Returns true if ALL diffs under this GO path are document-level adds or removes
        /// of the same type — meaning the entire GameObject was added or removed.
        /// </summary>
        private static bool IsWholeObjectAddOrRemove(List<DiffResult> changes, string changeType)
        {
            if (changes.Count == 0) return false;
            return changes.All(d => d.IsDocumentLevel && d.ChangeType == changeType);
        }

        private static string DominantChange(List<DiffResult> changes)
        {
            bool a = false, r = false, m = false;
            foreach (var c in changes)
            {
                switch (c.ChangeType)
                {
                    case "added": a = true; break;
                    case "removed": r = true; break;
                    default: m = true; break;
                }
            }
            if (a && !r && !m) return "added";
            if (r && !a && !m) return "removed";
            if (m && !a && !r) return "modified";
            return "modified"; // mixed
        }

        private static int CountFieldChanges(List<DiffResult> changes)
        {
            int count = 0;
            foreach (var c in changes)
            {
                if (!c.IsDocumentLevel) count++;
                else count++; // document-level still counts as 1 change
            }
            return count;
        }

        private static string SummarizeComponentBadge(DiffTreeItem item)
        {
            if (item.AllChanges == null || item.AllChanges.Count == 0) return "";
            int fieldCount = item.AllChanges.Count(c => !c.IsDocumentLevel);
            if (fieldCount == 0) return item.ChangeType == "added" ? "Added" : item.ChangeType == "removed" ? "Removed" : "Changed";
            if (fieldCount == 1) return "1 change";
            return fieldCount + " changes";
        }

        private GUIStyle GetBadgeStyle(string changeType)
        {
            switch (changeType)
            {
                case "added": return _badgeStyleAdded;
                case "removed": return _badgeStyleRemoved;
                default: return _badgeStyleModified;
            }
        }

        private static Color GetRowBackground(string changeType)
        {
            switch (changeType)
            {
                case "added": return AddedBg;
                case "removed": return RemovedBg;
                case "modified": return ModifiedBg;
                default: return new Color(0, 0, 0, 0);
            }
        }

        private static Color GetStripColor(string changeType)
        {
            switch (changeType)
            {
                case "added": return StripAdded;
                case "removed": return StripRemoved;
                default: return StripModified;
            }
        }

        private static string LimitValue(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(0, nl);
            if (s.Length > maxLen) s = s.Substring(0, maxLen - 3) + "...";
            return s;
        }

        private static string PrettyFieldPath(string raw, string componentType)
        {
            if (string.IsNullOrEmpty(raw) || raw == "<document>") return "(whole component)";
            if (!string.IsNullOrEmpty(componentType))
            {
                var lead = "/" + componentType + "/";
                if (raw.StartsWith(lead)) raw = raw.Substring(lead.Length);
            }
            if (raw.StartsWith("/")) raw = raw.Substring(1);
            return raw;
        }
    }
}
