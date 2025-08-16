#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using YamlDotNet.RepresentationModel;

namespace YamlPrefabDiff
{
    internal static class DiffEngine
    {
        private static readonly Color AddedC = new(0.4f, 1f, 0.4f);
        private static readonly Color RemovedC = new(1f, 0.4f, 0.4f);
        private static readonly Color ModifiedC = new(0.9f, 0.7f, 0.2f);

        public static List<DiffResult> Diff(string oldYaml, string newYaml, PrefabGraph oldGraph, PrefabGraph newGraph)
        {
            var diffs = new List<DiffResult>();
            var oldDocs = UnityYamlParsing.ExtractDocuments(oldYaml);
            var newDocs = UnityYamlParsing.ExtractDocuments(newYaml);

            // Index by fileId
            var newById = newDocs.ToDictionary(d => d.FileId, d => d);
            var oldById = oldDocs.ToDictionary(d => d.FileId, d => d);

            // Modified or removed
            foreach (var od in oldDocs)
            {
                if (newById.TryGetValue(od.FileId, out var nd))
                {
                    DiffYamlNodes(od, nd, "", diffs, oldGraph, newGraph);
                }
                else
                {
                    // whole document removed
                    diffs.Add(new DiffResult
                    {
                        ChangeType = "removed",
                        DocFileId = od.FileId,
                        ClassId = od.ClassId,
                        ComponentType = od.TypeName,
                        HierarchyPath = ResolveHierarchyPath(od.FileId, oldGraph, componentLabel: od.TypeName),
                        FieldPath = "<document>",
                        OldValue = PrettifyValue(od.RawText),
                        NewValue = null,
                        OwnerGameObject = ResolveOwnerName(od.FileId, oldGraph)
                    });
                }
            }
            // Added
            foreach (var nd in newDocs)
            {
                if (!oldById.ContainsKey(nd.FileId))
                {
                    diffs.Add(new DiffResult
                    {
                        ChangeType = "added",
                        DocFileId = nd.FileId,
                        ClassId = nd.ClassId,
                        ComponentType = nd.TypeName,
                        HierarchyPath = ResolveHierarchyPath(nd.FileId, newGraph, componentLabel: nd.TypeName),
                        FieldPath = "<document>",
                        OldValue = null,
                        NewValue = PrettifyValue(nd.RawText),
                        OwnerGameObject = ResolveOwnerName(nd.FileId, newGraph)
                    });
                }
            }

            // Cleanup: ignore GameObject m_Component removals (noise)
            diffs.RemoveAll(d => string.Equals(d.ComponentType, "GameObject", StringComparison.OrdinalIgnoreCase) && d.FieldPath.Contains("/m_Component"));

            // GUID prettify pass
            foreach (var d in diffs)
            {
                d.OldValue = PrettifyGuids(d.OldValue);
                d.NewValue = PrettifyGuids(d.NewValue);
            }

            return diffs;
        }

        private static void DiffYamlNodes(UnityYamlDocument oldDoc, UnityYamlDocument newDoc, string path, List<DiffResult> diffs, PrefabGraph oldGraph, PrefabGraph newGraph)
        {
            var on = oldDoc.Yaml.RootNode; var nn = newDoc.Yaml.RootNode;
            // Different top-level types → treat as modified wholesale
            if (oldDoc.TypeName != newDoc.TypeName)
            {
                diffs.Add(new DiffResult
                {
                    ChangeType = "modified",
                    DocFileId = newDoc.FileId,
                    ClassId = newDoc.ClassId,
                    ComponentType = newDoc.TypeName,
                    HierarchyPath = ResolveHierarchyPath(newDoc.FileId, newGraph, componentLabel: newDoc.TypeName),
                    FieldPath = path,
                    OldValue = PrettifyValue(oldDoc.RawText),
                    NewValue = PrettifyValue(newDoc.RawText),
                    OwnerGameObject = ResolveOwnerName(newDoc.FileId, newGraph)
                });
                return;
            }

            // Walk mapping under top key
            var oldTop = (YamlMappingNode)on; var newTop = (YamlMappingNode)nn;
            var topKey = new YamlScalarNode(oldDoc.TypeName);
            if (!oldTop.Children.TryGetValue(topKey, out var oldVal) || !newTop.Children.TryGetValue(topKey, out var newVal))
                return;
            Recurse(oldVal, newVal, path, diffs, oldDoc, newDoc, oldGraph, newGraph);
        }

        private static void Recurse(YamlNode o, YamlNode n, string path, List<DiffResult> diffs, UnityYamlDocument oldDoc, UnityYamlDocument newDoc, PrefabGraph oldGraph, PrefabGraph newGraph)
        {
            if (o == null && n != null)
            {
                Add(o, n, path, diffs, newDoc, newGraph, "added"); return;
            }
            if (o != null && n == null)
            {
                Add(o, n, path, diffs, oldDoc, oldGraph, "removed"); return;
            }
            if (o.GetType() != n.GetType())
            {
                Add(o, n, path, diffs, newDoc, newGraph, "modified"); return;
            }

            if (o is YamlScalarNode os && n is YamlScalarNode ns)
            {
                var ov = os.Value; var nv = ns.Value;
                if (ov != nv)
                {
                    Add(o, n, path, diffs, newDoc, newGraph, "modified", ov, nv);
                }
                return;
            }
            if (o is YamlMappingNode om && n is YamlMappingNode nm)
            {
                // Keys in old
                foreach (var kv in om.Children)
                {
                    var key = kv.Key.ToString();
                    nm.Children.TryGetValue(kv.Key, out var nv);
                    Recurse(kv.Value, nv, path + "/" + key, diffs, oldDoc, newDoc, oldGraph, newGraph);
                }
                // Keys only in new
                foreach (var kv in nm.Children)
                {
                    if (!om.Children.ContainsKey(kv.Key))
                    {
                        var key = kv.Key.ToString();
                        Recurse(null, kv.Value, path + "/" + key, diffs, oldDoc, newDoc, oldGraph, newGraph);
                    }
                }
                return;
            }
            if (o is YamlSequenceNode osq && n is YamlSequenceNode nsq)
            {
                int c = Math.Max(osq.Children.Count, nsq.Children.Count);
                for (int i = 0; i < c; i++)
                {
                    var oo = i < osq.Children.Count ? osq.Children[i] : null;
                    var nn = i < nsq.Children.Count ? nsq.Children[i] : null;
                    Recurse(oo, nn, path + $"[{i}]", diffs, oldDoc, newDoc, oldGraph, newGraph);
                }
                return;
            }
        }

        private static void Add(YamlNode o, YamlNode n, string fieldPath, List<DiffResult> diffs, UnityYamlDocument docForMeta, PrefabGraph graph, string change, string ov = null, string nv = null)
        {
            string oldVal = ov ?? o?.ToString();
            string newVal = nv ?? n?.ToString();
            var comp = docForMeta.TypeName;
            var fileId = docForMeta.FileId;

            diffs.Add(new DiffResult
            {
                ChangeType = change,
                DocFileId = fileId,
                ClassId = docForMeta.ClassId,
                ComponentType = comp,
                FieldPath = fieldPath,
                HierarchyPath = ResolveHierarchyPath(fileId, graph, comp),
                OldValue = PrettifyValue(oldVal),
                NewValue = PrettifyValue(newVal),
                OwnerGameObject = ResolveOwnerName(fileId, graph)
            });
        }

        private static string ResolveOwnerName(long? docFileId, PrefabGraph graph)
        {
            if (docFileId == null || graph == null) return null;
            if (graph.Components.TryGetValue(docFileId.Value, out var ci))
            {
                if (graph.GameObjects.TryGetValue(ci.OwnerGameObjectFileId, out var go))
                    return go.Name;
            }
            if (graph.GameObjects.TryGetValue(docFileId.Value, out var go2)) return go2.Name;
            return null;
        }

        private static string ResolveHierarchyPath(long? docFileId, PrefabGraph graph, string componentLabel = null)
        {
            if (docFileId == null || graph == null) return componentLabel ?? "";

            // If this is a component, hop to its GO
            long targetGoId = 0;
            if (graph.Components.TryGetValue(docFileId.Value, out var comp) && comp.OwnerGameObjectFileId != 0)
                targetGoId = comp.OwnerGameObjectFileId;
            else if (graph.GameObjects.ContainsKey(docFileId.Value))
                targetGoId = docFileId.Value;

            if (targetGoId == 0) return componentLabel ?? "";

            // Find the node containing this GO (via its Transform)
            var node = graph.TransformToNode.Values.FirstOrDefault(n => n.GameObjectFileId == targetGoId);
            if (node == null)
            {
                // Maybe the doc is actually a Transform itself
                if (graph.TransformToNode.TryGetValue(docFileId.Value, out var tnode)) node = tnode;
            }
            if (node == null) return componentLabel ?? "";

            // Build path up to root
            List<string> segments = new();
            BuildPath(node, graph, segments);
            var goPath = string.Join("/", segments);
            return string.IsNullOrEmpty(componentLabel) ? goPath : $"{goPath} ({componentLabel})";
        }

        private static void BuildPath(PrefabNode node, PrefabGraph graph, List<string> acc)
        {
            PrefabNode parent = null;
            foreach (var n in graph.TransformToNode.Values)
            {
                if (n.Children.Contains(node)) { parent = n; break; }
            }
            if (parent != null) BuildPath(parent, graph, acc);
            acc.Add(node.Name);
        }

        // Render helpers
        private static readonly Regex GuidRx = new(@"\b[0-9A-Fa-f]{32}\b", RegexOptions.Compiled);
        private static string PrettifyGuids(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return GuidRx.Replace(input, g =>
            {
                var guid = g.Value;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (obj != null) return $"{obj.name} ({guid})";
                }
                return guid;
            });
        }

        private static string PrettifyValue(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            // keep short scalars short
            if (v.IndexOf('\n') < 0 && v.Length <= 120) return v;
            return v.Trim();
        }
    }
}
#endif