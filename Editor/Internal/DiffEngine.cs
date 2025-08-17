#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using YamlDotNet.RepresentationModel;

namespace YamlPrefabDiff
{
    internal static class DiffEngine
    {
        private static readonly Regex GuidRx = new Regex(@"\b[0-9A-Fa-f]{32}\b", RegexOptions.Compiled);

        public static List<DiffResult> Diff(string oldYaml, string newYaml, PrefabGraph oldGraph, PrefabGraph newGraph)
        {
            var diffs = new List<DiffResult>();
            var oldDocs = UnityYamlParsing.ExtractDocuments(oldYaml);
            var newDocs = UnityYamlParsing.ExtractDocuments(newYaml);

            var newById = new Dictionary<long, UnityYamlDocument>();
            for (int i = 0; i < newDocs.Count; i++) newById[newDocs[i].FileId] = newDocs[i];

            var oldById = new Dictionary<long, UnityYamlDocument>();
            for (int i = 0; i < oldDocs.Count; i++) oldById[oldDocs[i].FileId] = oldDocs[i];

            // Build secondary keys for components to survive fileID churn
            var oldByKey = new Dictionary<string, UnityYamlDocument>();
            for (int i = 0; i < oldDocs.Count; i++)
            {
                var d = oldDocs[i];
                var k = CompKey(d);
                if (!string.IsNullOrEmpty(k) && !oldByKey.ContainsKey(k)) oldByKey[k] = d;
            }

            var pairedNewIds = new HashSet<long>();

            // exact id matches
            for (int i = 0; i < oldDocs.Count; i++)
            {
                var od = oldDocs[i];
                UnityYamlDocument nd;
                if (newById.TryGetValue(od.FileId, out nd))
                {
                    DiffYamlNodes(od, nd, "", diffs, oldGraph, newGraph);
                    pairedNewIds.Add(nd.FileId);
                }
            }

            // re-id matches
            for (int i = 0; i < newDocs.Count; i++)
            {
                var nd = newDocs[i];
                if (pairedNewIds.Contains(nd.FileId)) continue;
                var key = CompKey(nd);
                if (string.IsNullOrEmpty(key)) continue;
                UnityYamlDocument od;
                if (oldByKey.TryGetValue(key, out od))
                {
                    DiffYamlNodes(od, nd, "", diffs, oldGraph, newGraph);
                    pairedNewIds.Add(nd.FileId);
                    oldById.Remove(od.FileId);
                }
            }

            // Remaining olds -> removed
            for (int i = 0; i < oldDocs.Count; i++)
            {
                var od = oldDocs[i];
                if (newById.ContainsKey(od.FileId)) continue;
                var k = CompKey(od);
                if (k != null && oldByKey.ContainsKey(k) && !object.ReferenceEquals(oldByKey[k], od)) continue;

                diffs.Add(new DiffResult
                {
                    ChangeType = "removed",
                    DocFileId = od.FileId,
                    ClassId = od.ClassId,
                    ComponentType = od.TypeName,
                    HierarchyPath = ResolveHierarchyPath(od.FileId, oldGraph, od.TypeName),
                    FieldPath = "<document>",
                    OldValue = PrettifyValue(od.RawText),
                    NewValue = null,
                    OwnerGameObject = ResolveOwnerName(od.FileId, oldGraph)
                });
            }

            // Remaining news -> added
            for (int i = 0; i < newDocs.Count; i++)
            {
                var nd = newDocs[i];
                if (pairedNewIds.Contains(nd.FileId)) continue;

                diffs.Add(new DiffResult
                {
                    ChangeType = "added",
                    DocFileId = nd.FileId,
                    ClassId = nd.ClassId,
                    ComponentType = nd.TypeName,
                    HierarchyPath = ResolveHierarchyPath(nd.FileId, newGraph, nd.TypeName),
                    FieldPath = "<document>",
                    OldValue = null,
                    NewValue = PrettifyValue(nd.RawText),
                    OwnerGameObject = ResolveOwnerName(nd.FileId, newGraph)
                });
            }

            // Remove noisy GameObject m_Component changes
            diffs.RemoveAll(d => string.Equals(d.ComponentType, "GameObject", StringComparison.OrdinalIgnoreCase)
                              && d.FieldPath.IndexOf("/m_Component", StringComparison.OrdinalIgnoreCase) >= 0);

            // Prettify GUIDs
            for (int i = 0; i < diffs.Count; i++)
            {
                diffs[i].OldValue = PrettifyGuids(diffs[i].OldValue);
                diffs[i].NewValue = PrettifyGuids(diffs[i].NewValue);
            }

            return diffs;
        }

        private static string CompKey(UnityYamlDocument d)
        {
            if (d == null) return null;
            if (d.TypeName == "GameObject" || d.TypeName == "Transform") return null;
            var owner = d.OwnerGameObjectFileId;
            if (!string.IsNullOrEmpty(d.ScriptGuid))
                return "MB:" + owner + ":" + d.ScriptGuid;            // MonoBehaviour key
            return "CP:" + owner + ":" + d.ClassId + ":" + d.TypeName; // other component key
        }

        private static void DiffYamlNodes(UnityYamlDocument oldDoc, UnityYamlDocument newDoc, string path,
                                          List<DiffResult> diffs,
                                          PrefabGraph oldGraph, PrefabGraph newGraph)
        {
            if (oldDoc.TypeName != newDoc.TypeName)
            {
                diffs.Add(new DiffResult
                {
                    ChangeType = "modified",
                    DocFileId = newDoc.FileId,
                    ClassId = newDoc.ClassId,
                    ComponentType = newDoc.TypeName,
                    HierarchyPath = ResolveHierarchyPath(newDoc.FileId, newGraph, newDoc.TypeName),
                    FieldPath = path,
                    OldValue = PrettifyValue(oldDoc.RawText),
                    NewValue = PrettifyValue(newDoc.RawText),
                    OwnerGameObject = ResolveOwnerName(newDoc.FileId, newGraph)
                });
                return;
            }

            var oldTop = (YamlMappingNode)oldDoc.Yaml.RootNode;
            var newTop = (YamlMappingNode)newDoc.Yaml.RootNode;
            var topKey = new YamlScalarNode(oldDoc.TypeName);

            YamlNode oldVal, newVal;
            if (!oldTop.Children.TryGetValue(topKey, out oldVal) || !newTop.Children.TryGetValue(topKey, out newVal))
                return;

            Recurse(oldVal, newVal, path, diffs, oldDoc, newDoc, oldGraph, newGraph);
        }

        private static void Recurse(YamlNode o, YamlNode n, string path,
                                    List<DiffResult> diffs,
                                    UnityYamlDocument oldDoc, UnityYamlDocument newDoc,
                                    PrefabGraph oldGraph, PrefabGraph newGraph)
        {
            if (o == null && n != null)
            {
                Add(o, n, path, diffs, newDoc, newGraph, "added");
                return;
            }
            if (o != null && n == null)
            {
                Add(o, n, path, diffs, oldDoc, oldGraph, "removed");
                return;
            }
            if (o.GetType() != n.GetType())
            {
                Add(o, n, path, diffs, newDoc, newGraph, "modified");
                return;
            }

            var os = o as YamlScalarNode; var ns = n as YamlScalarNode;
            if (os != null && ns != null)
            {
                var ov = os.Value; var nv = ns.Value;
                if (ov != nv) Add(o, n, path, diffs, newDoc, newGraph, "modified", ov, nv);
                return;
            }

            var om = o as YamlMappingNode; var nm = n as YamlMappingNode;
            if (om != null && nm != null)
            {
                // Keys in old
                foreach (var kv in om.Children)
                {
                    var keyStr = kv.Key.ToString();
                    YamlNode nv;
                    nm.Children.TryGetValue(kv.Key, out nv);
                    Recurse(kv.Value, nv, path + "/" + keyStr, diffs, oldDoc, newDoc, oldGraph, newGraph);
                }
                // Keys only in new
                foreach (var kv in nm.Children)
                {
                    if (!om.Children.ContainsKey(kv.Key))
                    {
                        var keyStr = kv.Key.ToString();
                        Recurse(null, kv.Value, path + "/" + keyStr, diffs, oldDoc, newDoc, oldGraph, newGraph);
                    }
                }
                return;
            }

            var osq = o as YamlSequenceNode; var nsq = n as YamlSequenceNode;
            if (osq != null && nsq != null)
            {
                int c = Math.Max(osq.Children.Count, nsq.Children.Count);
                for (int i = 0; i < c; i++)
                {
                    var oo = i < osq.Children.Count ? osq.Children[i] : null;
                    var nn = i < nsq.Children.Count ? nsq.Children[i] : null;
                    Recurse(oo, nn, path + "[" + i + "]", diffs, oldDoc, newDoc, oldGraph, newGraph);
                }
                return;
            }
        }

        private static void Add(YamlNode o, YamlNode n, string fieldPath,
                                List<DiffResult> diffs, UnityYamlDocument docForMeta, PrefabGraph graph,
                                string change, string ov = null, string nv = null)
        {
            string oldVal = ov ?? (o != null ? o.ToString() : null);
            string newVal = nv ?? (n != null ? n.ToString() : null);

            diffs.Add(new DiffResult
            {
                ChangeType = change,
                DocFileId = docForMeta.FileId,
                ClassId = docForMeta.ClassId,
                ComponentType = docForMeta.TypeName,
                FieldPath = fieldPath,
                HierarchyPath = ResolveHierarchyPath(docForMeta.FileId, graph, docForMeta.TypeName),
                OldValue = PrettifyValue(oldVal),
                NewValue = PrettifyValue(newVal),
                OwnerGameObject = ResolveOwnerName(docForMeta.FileId, graph)
            });
        }

        private static string ResolveOwnerName(long? docFileId, PrefabGraph graph)
        {
            if (docFileId == null || graph == null) return null;

            ComponentInfo ci;
            if (graph.Components.TryGetValue(docFileId.Value, out ci))
            {
                GameObjectInfo go;
                if (graph.GameObjects.TryGetValue(ci.OwnerGameObjectFileId, out go))
                    return go.Name;
            }
            GameObjectInfo go2;
            if (graph.GameObjects.TryGetValue(docFileId.Value, out go2)) return go2.Name;
            return null;
        }

        private static string ResolveHierarchyPath(long? docFileId, PrefabGraph graph, string componentLabel)
        {
            if (docFileId == null || graph == null) return componentLabel ?? "";

            long targetGoId = 0;
            ComponentInfo ci;
            if (graph.Components.TryGetValue(docFileId.Value, out ci) && ci.OwnerGameObjectFileId != 0)
                targetGoId = ci.OwnerGameObjectFileId;
            else if (graph.GameObjects.ContainsKey(docFileId.Value))
                targetGoId = docFileId.Value;

            if (targetGoId == 0) return componentLabel ?? "";

            // Find node by GO
            PrefabNode node = null;
            foreach (var n in graph.TransformToNode.Values)
            {
                if (n.GameObjectFileId == targetGoId) { node = n; break; }
            }
            if (node == null)
            {
                // maybe the doc is a Transform
                if (!graph.TransformToNode.TryGetValue(docFileId.Value, out node)) return componentLabel ?? "";
            }

            var segments = new List<string>();
            BuildPathToRoot(node, graph, segments);
            var goPath = string.Join("/", segments.ToArray());

            return string.IsNullOrEmpty(componentLabel) ? goPath : (goPath + " (" + componentLabel + ")");
        }

        private static void BuildPathToRoot(PrefabNode node, PrefabGraph graph, List<string> acc)
        {
            PrefabNode parent = null;
            foreach (var n in graph.TransformToNode.Values)
            {
                if (n.Children.Contains(node)) { parent = n; break; }
            }
            if (parent != null) BuildPathToRoot(parent, graph, acc);
            acc.Add(node.Name);
        }

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
                    if (obj != null) return obj.name + " (" + guid + ")";
                }
                return guid;
            });
        }

        private static string PrettifyValue(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            if (v.IndexOf('\n') < 0 && v.Length <= 120) return v;
            return v.Trim();
        }
    }
}
#endif
