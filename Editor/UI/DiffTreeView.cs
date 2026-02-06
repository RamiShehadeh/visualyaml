using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace VisualYAML
{
    internal class DiffTreeItem : TreeViewItem
    {
        public DiffResult FieldChange;
        public string ChangeBadge;
        public bool IsLeaf => FieldChange != null;
    }

    internal class DiffTreeView : TreeView
    {
        private readonly AssetDiffEntry _entry;
        private readonly string _searchFilter;

        private GUIStyle _badgeStyle;
        private GUIStyle _inlineStyle;

        private const float BadgePadding = 10f;
        private const float MinInlineWidth = 520f;

        private static readonly Color AddedColor = new Color(0.24f, 0.80f, 0.42f);
        private static readonly Color ModifiedColor = new Color(0.90f, 0.76f, 0.31f);
        private static readonly Color RemovedColor = new Color(0.88f, 0.35f, 0.31f);

        public DiffTreeView(TreeViewState state, AssetDiffEntry entry, string searchFilter = null) : base(state)
        {
            _entry = entry;
            _searchFilter = searchFilter;
            showAlternatingRowBackgrounds = true;
            rowHeight = 20f;
            Reload();
        }

        private void EnsureStyles()
        {
            if (_badgeStyle == null)
            {
                _badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleRight,
                    clipping = TextClipping.Clip,
                    richText = false
                };
            }
            if (_inlineStyle == null)
            {
                _inlineStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    clipping = TextClipping.Clip,
                    richText = true,
                    wordWrap = false
                };
            }
        }

        protected override TreeViewItem BuildRoot()
        {
            var root = new DiffTreeItem { id = 0, depth = -1, displayName = "root" };

            var results = _entry.DiffResults;
            if (results == null || results.Count == 0)
            {
                root.AddChild(new DiffTreeItem { id = 1, depth = 0, displayName = "No differences." });
                SetupDepthsFromParentsAndChildren(root);
                return root;
            }

            // Apply search filter
            if (!string.IsNullOrEmpty(_searchFilter))
            {
                var lower = _searchFilter.ToLowerInvariant();
                results = results.Where(d =>
                    (d.HierarchyPath != null && d.HierarchyPath.ToLowerInvariant().Contains(lower)) ||
                    (d.ComponentType != null && d.ComponentType.ToLowerInvariant().Contains(lower)) ||
                    (d.FieldPath != null && d.FieldPath.ToLowerInvariant().Contains(lower)) ||
                    (d.OldValue != null && d.OldValue.ToLowerInvariant().Contains(lower)) ||
                    (d.NewValue != null && d.NewValue.ToLowerInvariant().Contains(lower))
                ).ToList();

                if (results.Count == 0)
                {
                    root.AddChild(new DiffTreeItem { id = 1, depth = 0, displayName = "No matches for \"" + _searchFilter + "\"" });
                    SetupDepthsFromParentsAndChildren(root);
                    return root;
                }
            }

            // Group by GameObject hierarchy path
            var perGo = new Dictionary<string, List<DiffResult>>();
            foreach (var d in results)
            {
                var goPath = d.HierarchyPath ?? string.Empty;
                int paren = goPath.LastIndexOf('(');
                var goOnly = paren > 0 ? goPath.Substring(0, paren).TrimEnd() : goPath;
                if (!perGo.TryGetValue(goOnly, out var list))
                {
                    list = new List<DiffResult>();
                    perGo[goOnly] = list;
                }
                list.Add(d);
            }

            var goKeys = perGo.Keys.ToList();
            goKeys.Sort(System.StringComparer.OrdinalIgnoreCase);

            int id = 1;
            foreach (var goKey in goKeys)
            {
                var goChanges = perGo[goKey];
                var goItem = new DiffTreeItem
                {
                    id = id++,
                    depth = 0,
                    displayName = string.IsNullOrEmpty(goKey) ? "(Unresolved)" : goKey,
                    ChangeBadge = goChanges.Count + " changes"
                };
                root.AddChild(goItem);

                var byComp = goChanges
                    .GroupBy(d => string.IsNullOrEmpty(d.ComponentType) ? "(Unknown)" : d.ComponentType)
                    .OrderBy(g => g.Key);

                foreach (var compGroup in byComp)
                {
                    var compChanges = compGroup.ToList();
                    var summary = Summarize(compChanges);

                    var compItem = new DiffTreeItem
                    {
                        id = id++,
                        depth = 1,
                        displayName = compGroup.Key,
                        ChangeBadge = summary
                    };
                    goItem.AddChild(compItem);

                    foreach (var ch in compChanges.OrderBy(c => c.FieldPath))
                    {
                        compItem.AddChild(new DiffTreeItem
                        {
                            id = id++,
                            depth = 2,
                            displayName = PrettyFieldPath(ch.FieldPath, ch.ComponentType),
                            FieldChange = ch,
                            ChangeBadge = BadgeFor(ch.ChangeType)
                        });
                    }
                }
            }

            if (root.children == null || root.children.Count == 0)
                root.AddChild(new DiffTreeItem { id = id++, depth = 0, displayName = "No differences." });

            SetupDepthsFromParentsAndChildren(root);
            return root;
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            EnsureStyles();
            var item = (DiffTreeItem)args.item;

            // Left color strip for leaf rows
            if (item.IsLeaf && item.FieldChange != null)
            {
                var strip = args.rowRect;
                strip.width = 3f;
                EditorGUI.DrawRect(strip, RowColor(item.FieldChange.ChangeType));
            }

            base.RowGUI(args);

            var row = args.rowRect;
            float indent = GetContentIndent(item);
            float labelEnd = indent + 260f;
            float badgeWidth = CalcBadgeWidth(item.ChangeBadge);
            var badgeRect = new Rect(row.xMax - (badgeWidth + BadgePadding), row.y, badgeWidth, row.height);
            var inlineRect = new Rect(row.x + labelEnd, row.y, Mathf.Max(0, badgeRect.x - (row.x + labelEnd) - 6f), row.height);

            // Inline diff preview
            if (item.IsLeaf && inlineRect.width > 60f && row.width >= MinInlineWidth)
            {
                EditorGUI.LabelField(inlineRect, InlineDiffText(item.FieldChange), _inlineStyle);
            }

            // Badge
            if (!string.IsNullOrEmpty(item.ChangeBadge))
                EditorGUI.LabelField(badgeRect, item.ChangeBadge, _badgeStyle);
        }

        protected override void ContextClickedItem(int id)
        {
            var item = FindItem(id, rootItem) as DiffTreeItem;
            if (item == null || !item.IsLeaf) return;

            var menu = new GenericMenu();
            var fc = item.FieldChange;

            menu.AddItem(new GUIContent("Copy Field Path"), false, () =>
                EditorGUIUtility.systemCopyBuffer = fc.FieldPath ?? "");

            if (!string.IsNullOrEmpty(fc.OldValue))
                menu.AddItem(new GUIContent("Copy Old Value"), false, () =>
                    EditorGUIUtility.systemCopyBuffer = fc.OldValue);

            if (!string.IsNullOrEmpty(fc.NewValue))
                menu.AddItem(new GUIContent("Copy New Value"), false, () =>
                    EditorGUIUtility.systemCopyBuffer = fc.NewValue);

            menu.ShowAsContext();
        }

        // --- Helpers ---

        private static float CalcBadgeWidth(string badge)
        {
            if (string.IsNullOrEmpty(badge)) return 0f;
            return GUI.skin.label.CalcSize(new GUIContent(badge)).x + 8f;
        }

        private static Color RowColor(string ct)
        {
            switch (ct)
            {
                case "added": return AddedColor;
                case "removed": return RemovedColor;
                default: return ModifiedColor;
            }
        }

        private static string InlineDiffText(DiffResult d)
        {
            string Limit(string s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(0, nl);
                if (s.Length > 120) s = s.Substring(0, 117) + "...";
                return s;
            }

            switch (d.ChangeType)
            {
                case "added": return "<b>new:</b> " + Limit(d.NewValue);
                case "removed": return "<b>old:</b> " + Limit(d.OldValue);
                default: return Limit(d.OldValue) + " <b>\u2192</b> " + Limit(d.NewValue);
            }
        }

        private static string Summarize(List<DiffResult> diffs)
        {
            bool add = false, rem = false, mod = false;
            foreach (var d in diffs)
            {
                switch (d.ChangeType)
                {
                    case "added": add = true; break;
                    case "removed": rem = true; break;
                    case "modified": mod = true; break;
                }
            }
            if (add && !rem && !mod) return "+ added";
            if (!add && rem && !mod) return "- removed";
            if (!add && !rem && mod) return "~ modified";
            return diffs.Count + " changes";
        }

        private static string BadgeFor(string ct)
        {
            switch (ct)
            {
                case "added": return "+ added";
                case "removed": return "- removed";
                default: return "~ modified";
            }
        }

        private static string PrettyFieldPath(string raw, string componentType)
        {
            if (string.IsNullOrEmpty(raw) || raw == "<document>") return "(document)";
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
