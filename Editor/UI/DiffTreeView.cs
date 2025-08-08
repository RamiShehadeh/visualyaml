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
        private readonly AssetDiffEntry _entry;
        private readonly GUIStyle _badgeStyle;

        public DiffTreeView(TreeViewState state, AssetDiffEntry entry) : base(state)
        {
            _entry = entry;
            showAlternatingRowBackgrounds = true;
            rowHeight = 20f;
            _badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleRight };
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
            base.RowGUI(args); // draws label & indentation

            var item = (DiffTreeItem)args.item;
            var rect = args.rowRect;

            // Right badge
            if (!string.IsNullOrEmpty(item.ChangeBadge))
            {
                var r = rect; r.xMin = r.xMax - 150f;
                EditorGUI.LabelField(r, item.ChangeBadge, _badgeStyle);
            }

            // If leaf, draw inline “old → new” to the right of the label
            if (item.IsLeaf && item.FieldChange != null)
            {
                var ch = item.FieldChange;
                var textRect = rect; textRect.xMin = Mathf.Max(textRect.xMin + GetContentIndent(item) + 260f, 260f);

                var s = InlineDiffText(ch);
                var style = new GUIStyle(EditorStyles.miniLabel) { richText = true, wordWrap = false };
                EditorGUI.LabelField(textRect, s, style);
            }
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
            if (!add && rem && !mod) return ("− removed", "removed");
            if (!add && !rem && mod) return ("⟲ modified", "modified");
            return ($"{diffs.Count} changes", "mixed");
        }

        private static string BadgeFor(string ct) =>
            ct == "added" ? "+ added" :
            ct == "removed" ? "− removed" : "⟲ modified";

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

        private static string InlineDiffText(DiffResult d)
        {
            switch (d.ChangeType)
            {
                case "added":
                    return $"<b>new:</b> {Limit(d.NewValue)}";
                case "removed":
                    return $"<b>old:</b> {Limit(d.OldValue)}";
                default:
                    return $"{Limit(d.OldValue)} <b>→</b> {Limit(d.NewValue)}";
            }
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
