// Assets/Packages/YamlPrefabDiff/Editor/UI/DiffTreeView.cs
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace YamlPrefabDiff
{
    internal class DiffTreeItem : TreeViewItem
    {
        public DiffResult FieldChange;         // non-null only for leaf rows (a single field)
        public string ChangeBadge;             // “+ added”, “− removed”, “⟲ modified”, or “<n> changes”
        public bool IsLeaf => FieldChange != null;
    }

    internal class DiffTreeView : TreeView
    {
        private readonly GUIStyle _badgeStyle;
        private readonly GUIStyle _inlineStyle;
        private const float BadgePadding = 10f;
        private const float MinInlineWidth = 520f; // don't render inline diff on tiny rows
        private static readonly Color Added = new Color(0.24f, 0.80f, 0.42f);    // #3CCB6B
        private static readonly Color Modified = new Color(0.90f, 0.76f, 0.31f); // #E6C14F
        private static readonly Color Removed = new Color(0.88f, 0.35f, 0.31f);  // #E05A4F
        private readonly AssetDiffEntry _entry;

        public DiffTreeView(TreeViewState state, AssetDiffEntry entry) : base(state)
        {
            _entry = entry;
            showAlternatingRowBackgrounds = true;
            rowHeight = 20f;

            _badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleRight,
                clipping = TextClipping.Clip,
                richText = false
            };
            _inlineStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                richText = true,
                wordWrap = false
            };

            Reload();
        }

        protected override TreeViewItem BuildRoot()
        {
            var root = new DiffTreeItem { id = 0, depth = -1, displayName = "root" };

            // 1) group by GameObject hierarchy path (strip "(Component)")
            var perGo = new Dictionary<string, List<DiffResult>>();
            foreach (var d in _entry.DiffResults)
            {
                var goPath = d.HierarchyPath ?? string.Empty;
                var paren = goPath.LastIndexOf('(');
                var goOnly = paren > 0 ? goPath.Substring(0, paren).TrimEnd() : goPath;
                if (!perGo.TryGetValue(goOnly, out var list)) perGo[goOnly] = list = new();
                list.Add(d);
            }

            // Sort GameObjects for stability
            var goKeys = perGo.Keys.ToList();
            goKeys.Sort(System.StringComparer.OrdinalIgnoreCase);

            int id = 1;
            foreach (var goKey in goKeys)
            {
                var goItem = new DiffTreeItem { id = id++, depth = 0, displayName = string.IsNullOrEmpty(goKey) ? "(Unresolved GameObject)" : goKey };
                root.AddChild(goItem); // <-- add ONCE

                // 2) group by component type under this GO
                var byComp = perGo[goKey]
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
                        ChangeBadge = summary.badge
                    };
                    goItem.AddChild(compItem);

                    // 3) leaf per field change
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
            // Left color strip based on change type (for leaf rows only)
            var item = (DiffTreeItem)args.item;
            if (item.IsLeaf && item.FieldChange != null)
            {
                var strip = args.rowRect; strip.width = 3f;
                EditorGUI.DrawRect(strip, RowColor(item.FieldChange.ChangeType));
            }

            // Draw default label first (respects indentation)
            base.RowGUI(args);

            // Compute rectangles: [label ...] [inlineDiff ...] [badge  ]
            var row = args.rowRect;
            float indent = GetContentIndent(item);
            float labelEnd = indent + 260f; // roughly where our field path label ends
            float badgeWidth = CalcBadgeWidth(item.ChangeBadge);
            var badgeRect = new Rect(row.xMax - (badgeWidth + BadgePadding), row.y, badgeWidth, row.height);

            // Inline area gets what's between labelEnd and badgeRect.x
            var inlineRect = new Rect(row.x + labelEnd, row.y, Mathf.Max(0, badgeRect.x - (row.x + labelEnd) - 6f), row.height);

            // Only draw inline when there's enough width and it's a leaf
            if (item.IsLeaf && inlineRect.width > 60f && row.width >= MinInlineWidth)
            {
                var inline = InlineDiffText(item.FieldChange);
                EditorGUI.LabelField(inlineRect, inline, _inlineStyle);
            }

            // Badge (always)
            if (!string.IsNullOrEmpty(item.ChangeBadge))
                EditorGUI.LabelField(badgeRect, item.ChangeBadge, _badgeStyle);
        }

        private static float CalcBadgeWidth(string badge)
        {
            if (string.IsNullOrEmpty(badge)) return 0f;
            return GUI.skin.label.CalcSize(new GUIContent(badge)).x + 8f;
        }

        private static Color RowColor(string ct)
        {
            switch (ct)
            {
                case "added": return Added;
                case "removed": return Removed;
                default: return Modified;
            }
        }

        private static string InlineDiffText(DiffResult d)
        {
            // short, single-line preview; GUIDs are already prettified
            string Limit(string s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                var nl = s.IndexOf('\n'); if (nl >= 0) s = s[..nl];
                if (s.Length > 120) s = s[..117] + "…";
                return s;
            }

            return d.ChangeType switch
            {
                "added" => $"<b>new:</b> {Limit(d.NewValue)}",
                "removed" => $"<b>old:</b> {Limit(d.OldValue)}",
                _ => $"{Limit(d.OldValue)} <b>→</b> {Limit(d.NewValue)}"
            };
        }

        // ------------ helpers -------------
        private static (string badge, string type) Summarize(List<DiffResult> diffs)
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
            if (add && !rem && !mod) return ("+ added", "added");
            if (!add && rem && !mod) return ("- removed", "removed");
            if (!add && !rem && mod) return ("~ modified", "modified");
            return ($"{diffs.Count} changes", "mixed");
        }

        private static string BadgeFor(string ct) =>
            ct == "added" ? "+ added" :
            ct == "removed" ? "− removed" : "~ modified";

        private static string PrettyFieldPath(string raw, string componentType)
        {
            if (string.IsNullOrEmpty(raw)) return "(document)";
            // strip leading "/<Type>/" so we don’t repeat the component name
            if (!string.IsNullOrEmpty(componentType))
            {
                var lead = "/" + componentType + "/";
                if (raw.StartsWith(lead)) raw = raw.Substring(lead.Length);
            }
            // drop leading slash
            if (raw.StartsWith("/")) raw = raw.Substring(1);
            return raw;
        }

        private static string Limit(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // keep one-liners tidy
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(0, nl);
            if (s.Length > 120) s = s.Substring(0, 117) + "…";
            return s;
        }
    }
}
#endif
