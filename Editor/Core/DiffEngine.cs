using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using YamlDotNet.RepresentationModel;

namespace AssetDiff
{
    internal static class DiffEngine
    {
        private static readonly Regex GuidRx = new Regex(@"\b[0-9A-Fa-f]{32}\b", RegexOptions.Compiled);

        // Fields that produce noise and should be filtered from diff results
        private static readonly HashSet<string> NoisyFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/m_ObjectHideFlags",
            "/m_CorrespondingSourceObject",
            "/m_PrefabInstance",
            "/m_PrefabAsset",
            "/serializedVersion",
            "/m_Father",
            "/m_RootOrder",
            "/m_LocalEulerAnglesHint",
        };

        // Field path prefixes that are always noise
        private static readonly string[] NoisyPrefixes = new[]
        {
            // PrefabInstance internal override bookkeeping — very noisy, index-matched arrays
            "/m_Modification/m_TransformParent",
            "/m_Modification/m_Modifications",
            "/m_Modification/m_RemovedComponents",
            "/m_Modification/m_RemovedGameObjects",
            "/m_Modification/m_AddedComponents",
            "/m_Modification/m_AddedGameObjects",
            // Source prefab reference tracking
            "/m_SourcePrefab",
            "/m_ParentPrefab",
        };

        public static List<DiffResult> Diff(
            string oldYaml, string newYaml,
            PrefabGraph oldGraph, PrefabGraph newGraph)
        {
            var diffs = new List<DiffResult>();
            var oldDocs = UnityYamlParser.ExtractDocuments(oldYaml);
            var newDocs = UnityYamlParser.ExtractDocuments(newYaml);

            TypeResolver.ResolveMonoBehaviourNames(oldDocs);
            TypeResolver.ResolveMonoBehaviourNames(newDocs);

            var newById = new Dictionary<long, UnityYamlDocument>();
            for (int i = 0; i < newDocs.Count; i++) newById[newDocs[i].FileId] = newDocs[i];

            // Build secondary keys for re-identification
            var oldByKey = new Dictionary<string, UnityYamlDocument>();
            for (int i = 0; i < oldDocs.Count; i++)
            {
                var k = CompKey(oldDocs[i]);
                if (k != null && !oldByKey.ContainsKey(k)) oldByKey[k] = oldDocs[i];
            }

            var pairedOldIds = new HashSet<long>();
            var pairedNewIds = new HashSet<long>();

            // Stage 1: Exact fileID matches
            for (int i = 0; i < oldDocs.Count; i++)
            {
                var od = oldDocs[i];
                if (newById.TryGetValue(od.FileId, out UnityYamlDocument nd))
                {
                    DiffDocuments(od, nd, diffs, oldGraph, newGraph);
                    pairedOldIds.Add(od.FileId);
                    pairedNewIds.Add(nd.FileId);
                }
            }

            // Stage 2: Re-identification for unpaired documents
            for (int i = 0; i < newDocs.Count; i++)
            {
                var nd = newDocs[i];
                if (pairedNewIds.Contains(nd.FileId)) continue;
                var key = CompKey(nd);
                if (key == null) continue;
                if (oldByKey.TryGetValue(key, out UnityYamlDocument od) && !pairedOldIds.Contains(od.FileId))
                {
                    DiffDocuments(od, nd, diffs, oldGraph, newGraph);
                    pairedOldIds.Add(od.FileId);
                    pairedNewIds.Add(nd.FileId);
                }
            }

            // Stage 3: Remaining old docs -> removed
            for (int i = 0; i < oldDocs.Count; i++)
            {
                var od = oldDocs[i];
                if (pairedOldIds.Contains(od.FileId)) continue;

                diffs.Add(new DiffResult
                {
                    ChangeType = "removed",
                    DocFileId = od.FileId,
                    ClassId = od.ClassId,
                    ComponentType = od.TypeName,
                    HierarchyPath = ResolveHierarchyPath(od.FileId, oldGraph, od.TypeName),
                    FieldPath = "<document>",
                    OldValue = TrimValue(od.RawText),
                    OwnerGameObject = ResolveOwnerName(od.FileId, oldGraph),
                    IsDocumentLevel = true
                });
            }

            // Stage 4: Remaining new docs -> added
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
                    NewValue = TrimValue(nd.RawText),
                    OwnerGameObject = ResolveOwnerName(nd.FileId, newGraph),
                    IsDocumentLevel = true
                });
            }

            // Post-process: remove noise, prettify
            FilterNoise(diffs);
            PrettifyAllGuids(diffs);

            return diffs;
        }

        // --- Stable key for re-identification across fileID churn ---

        private static string CompKey(UnityYamlDocument d)
        {
            if (d == null || d.IsStripped) return null;
            int cls = d.ClassId;

            // GameObjects: key by name
            if (UnityClassIds.IsGameObject(cls))
                return null; // GOs are matched by fileID or through their Transform

            // Transforms/RectTransforms: key by owner GO
            if (UnityClassIds.IsTransformType(cls))
                return d.OwnerGameObjectFileId != 0 ? "T:" + d.OwnerGameObjectFileId : null;

            // MonoBehaviours: key by owner GO + script GUID
            if (cls == 114 && !string.IsNullOrEmpty(d.ScriptGuid))
                return "MB:" + d.OwnerGameObjectFileId + ":" + d.ScriptGuid;

            // Other components: key by owner GO + class ID
            return "CP:" + d.OwnerGameObjectFileId + ":" + cls + ":" + d.TypeName;
        }

        // --- Document-level diffing ---

        private static void DiffDocuments(
            UnityYamlDocument oldDoc, UnityYamlDocument newDoc,
            List<DiffResult> diffs, PrefabGraph oldGraph, PrefabGraph newGraph)
        {
            // Skip stripped documents (no meaningful body to diff)
            if (oldDoc.IsStripped || newDoc.IsStripped) return;
            if (oldDoc.Yaml == null || newDoc.Yaml == null) return;

            var oldRoot = oldDoc.Yaml.RootNode as YamlMappingNode;
            var newRoot = newDoc.Yaml.RootNode as YamlMappingNode;
            if (oldRoot == null || newRoot == null) return;

            // Get the inner content (e.g., the mapping under "Transform:" or "MonoBehaviour:")
            YamlNode oldContent = null, newContent = null;

            // Try old doc's type name
            oldRoot.Children.TryGetValue(new YamlScalarNode(oldDoc.TypeName), out oldContent);
            newRoot.Children.TryGetValue(new YamlScalarNode(newDoc.TypeName), out newContent);

            // If type names differ (e.g., resolved differently), try fallback
            if (oldContent == null && newContent != null)
            {
                oldRoot.Children.TryGetValue(new YamlScalarNode(newDoc.TypeName), out oldContent);
            }
            else if (oldContent != null && newContent == null)
            {
                newRoot.Children.TryGetValue(new YamlScalarNode(oldDoc.TypeName), out newContent);
            }

            if (oldContent == null || newContent == null)
            {
                // Fall back to class ID-based key
                var classKey = UnityClassIds.GetTypeName(newDoc.ClassId);
                if (oldContent == null) oldRoot.Children.TryGetValue(new YamlScalarNode(classKey), out oldContent);
                if (newContent == null) newRoot.Children.TryGetValue(new YamlScalarNode(classKey), out newContent);
            }

            if (oldContent == null || newContent == null) return;

            Recurse(oldContent, newContent, "", diffs, newDoc, newGraph);
        }

        // --- Recursive YAML node comparison ---

        private static void Recurse(
            YamlNode o, YamlNode n, string path,
            List<DiffResult> diffs, UnityYamlDocument docForMeta, PrefabGraph graph)
        {
            if (o == null && n != null)
            {
                AddDiff(path, diffs, docForMeta, graph, "added", null, NodeToString(n));
                return;
            }
            if (o != null && n == null)
            {
                AddDiff(path, diffs, docForMeta, graph, "removed", NodeToString(o), null);
                return;
            }
            if (o == null && n == null) return;

            // Type mismatch
            if (o.GetType() != n.GetType())
            {
                AddDiff(path, diffs, docForMeta, graph, "modified", NodeToString(o), NodeToString(n));
                return;
            }

            // Scalars
            if (o is YamlScalarNode os && n is YamlScalarNode ns)
            {
                if (os.Value != ns.Value)
                    AddDiff(path, diffs, docForMeta, graph, "modified", os.Value, ns.Value);
                return;
            }

            // Mappings
            if (o is YamlMappingNode om && n is YamlMappingNode nm)
            {
                // Keys in old
                foreach (var kv in om.Children)
                {
                    var keyStr = kv.Key.ToString();
                    nm.Children.TryGetValue(kv.Key, out YamlNode nv);
                    Recurse(kv.Value, nv, path + "/" + keyStr, diffs, docForMeta, graph);
                }
                // Keys only in new
                foreach (var kv in nm.Children)
                {
                    if (!om.Children.ContainsKey(kv.Key))
                        Recurse(null, kv.Value, path + "/" + kv.Key, diffs, docForMeta, graph);
                }
                return;
            }

            // Sequences
            if (o is YamlSequenceNode osq && n is YamlSequenceNode nsq)
            {
                DiffSequence(osq, nsq, path, diffs, docForMeta, graph);
                return;
            }
        }

        private static void DiffSequence(
            YamlSequenceNode old, YamlSequenceNode @new, string path,
            List<DiffResult> diffs, UnityYamlDocument docForMeta, PrefabGraph graph)
        {
            // Try key-based matching for arrays of references (m_Children, m_Component, etc.)
            if (TryKeyBasedSequenceDiff(old, @new, path, diffs, docForMeta, graph))
                return;

            // Try name-based matching for settings arrays (QualitySettings, etc.)
            if (TryNameBasedSequenceDiff(old, @new, path, diffs, docForMeta, graph))
                return;

            // Fallback: index-based comparison
            int count = Math.Max(old.Children.Count, @new.Children.Count);
            for (int i = 0; i < count; i++)
            {
                var oo = i < old.Children.Count ? old.Children[i] : null;
                var nn = i < @new.Children.Count ? @new.Children[i] : null;
                Recurse(oo, nn, path + "[" + i + "]", diffs, docForMeta, graph);
            }
        }

        /// <summary>
        /// For arrays of {fileID: N} references, match by fileID value instead of index.
        /// This avoids false "modified" diffs when elements are reordered or inserted.
        /// </summary>
        private static bool TryKeyBasedSequenceDiff(
            YamlSequenceNode old, YamlSequenceNode @new, string path,
            List<DiffResult> diffs, UnityYamlDocument docForMeta, PrefabGraph graph)
        {
            // Check if this looks like an array of fileID refs
            if (old.Children.Count == 0 && @new.Children.Count == 0) return true;

            // Heuristic: check first element of each side
            string ExtractKey(YamlNode node)
            {
                if (node is YamlMappingNode map)
                {
                    // {fileID: N} reference
                    if (map.Children.TryGetValue(new YamlScalarNode("fileID"), out var idNode) &&
                        idNode is YamlScalarNode idScalar)
                        return "fid:" + idScalar.Value;

                    // Nested map with component or classId key (m_Component entries)
                    foreach (var kv in map.Children)
                    {
                        if (kv.Value is YamlMappingNode inner &&
                            inner.Children.TryGetValue(new YamlScalarNode("fileID"), out var innerIdNode) &&
                            innerIdNode is YamlScalarNode innerIdScalar)
                            return "fid:" + innerIdScalar.Value;
                    }
                }
                return null;
            }

            // Check if elements have extractable keys
            bool hasKeys = false;
            if (old.Children.Count > 0 && ExtractKey(old.Children[0]) != null) hasKeys = true;
            else if (@new.Children.Count > 0 && ExtractKey(@new.Children[0]) != null) hasKeys = true;

            if (!hasKeys) return false;

            // Build old keyed map
            var oldByKey = new Dictionary<string, YamlNode>();
            var oldUnkeyed = new List<(int idx, YamlNode node)>();
            for (int i = 0; i < old.Children.Count; i++)
            {
                var key = ExtractKey(old.Children[i]);
                if (key != null && !oldByKey.ContainsKey(key))
                    oldByKey[key] = old.Children[i];
                else
                    oldUnkeyed.Add((i, old.Children[i]));
            }

            var matchedOld = new HashSet<string>();
            // Match new elements to old by key
            for (int i = 0; i < @new.Children.Count; i++)
            {
                var key = ExtractKey(@new.Children[i]);
                if (key != null && oldByKey.TryGetValue(key, out var oldNode))
                {
                    Recurse(oldNode, @new.Children[i], path + "[" + i + "]", diffs, docForMeta, graph);
                    matchedOld.Add(key);
                }
                else
                {
                    // Added element
                    Recurse(null, @new.Children[i], path + "[" + i + "]", diffs, docForMeta, graph);
                }
            }

            // Removed elements (in old but not matched)
            foreach (var kv in oldByKey)
            {
                if (!matchedOld.Contains(kv.Key))
                    Recurse(kv.Value, null, path + "[?]", diffs, docForMeta, graph);
            }

            return true;
        }

        /// <summary>
        /// For arrays of mappings with a "name" field (like QualitySettings, InputManager axes),
        /// match by name instead of index to avoid false positives when entries are reordered.
        /// </summary>
        private static bool TryNameBasedSequenceDiff(
            YamlSequenceNode old, YamlSequenceNode @new, string path,
            List<DiffResult> diffs, UnityYamlDocument docForMeta, PrefabGraph graph)
        {
            if (old.Children.Count == 0 && @new.Children.Count == 0) return true;

            string ExtractName(YamlNode node)
            {
                if (node is YamlMappingNode map &&
                    map.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode) &&
                    nameNode is YamlScalarNode nameScalar &&
                    !string.IsNullOrEmpty(nameScalar.Value))
                    return nameScalar.Value;
                return null;
            }

            // Check if elements have name keys
            bool hasNames = false;
            if (old.Children.Count > 0 && ExtractName(old.Children[0]) != null) hasNames = true;
            else if (@new.Children.Count > 0 && ExtractName(@new.Children[0]) != null) hasNames = true;
            if (!hasNames) return false;

            // Build old map by name (with index suffix for duplicates)
            var oldByName = new Dictionary<string, (int idx, YamlNode node)>();
            var nameCounts = new Dictionary<string, int>();
            for (int i = 0; i < old.Children.Count; i++)
            {
                var name = ExtractName(old.Children[i]);
                if (name == null) return false; // Mixed — bail to index-based
                nameCounts.TryGetValue(name, out int cnt);
                var key = cnt > 0 ? name + "#" + cnt : name;
                nameCounts[name] = cnt + 1;
                oldByName[key] = (i, old.Children[i]);
            }

            var matchedOld = new HashSet<string>();
            var newNameCounts = new Dictionary<string, int>();

            for (int i = 0; i < @new.Children.Count; i++)
            {
                var name = ExtractName(@new.Children[i]);
                if (name == null) return false;

                newNameCounts.TryGetValue(name, out int cnt);
                var key = cnt > 0 ? name + "#" + cnt : name;
                newNameCounts[name] = cnt + 1;

                if (oldByName.TryGetValue(key, out var oldEntry))
                {
                    Recurse(oldEntry.node, @new.Children[i], path + "/" + name, diffs, docForMeta, graph);
                    matchedOld.Add(key);
                }
                else
                {
                    Recurse(null, @new.Children[i], path + "/" + name, diffs, docForMeta, graph);
                }
            }

            foreach (var kv in oldByName)
            {
                if (!matchedOld.Contains(kv.Key))
                    Recurse(kv.Value.node, null, path + "/" + kv.Key, diffs, docForMeta, graph);
            }

            return true;
        }

        private static void AddDiff(
            string fieldPath, List<DiffResult> diffs,
            UnityYamlDocument docForMeta, PrefabGraph graph,
            string change, string oldVal, string newVal)
        {
            diffs.Add(new DiffResult
            {
                ChangeType = change,
                DocFileId = docForMeta.FileId,
                ClassId = docForMeta.ClassId,
                ComponentType = docForMeta.TypeName,
                FieldPath = fieldPath,
                HierarchyPath = ResolveHierarchyPath(docForMeta.FileId, graph, docForMeta.TypeName),
                OldValue = TrimValue(oldVal),
                NewValue = TrimValue(newVal),
                OwnerGameObject = ResolveOwnerName(docForMeta.FileId, graph)
            });
        }

        // --- Noise filtering ---

        private static void FilterNoise(List<DiffResult> diffs)
        {
            diffs.RemoveAll(d =>
            {
                if (d.FieldPath == null) return false;

                // Remove noisy GameObject m_Component changes
                if (string.Equals(d.ComponentType, "GameObject", StringComparison.OrdinalIgnoreCase) &&
                    d.FieldPath.Contains("/m_Component"))
                    return true;

                // Remove known noisy fields (exact prefix match)
                foreach (var noisy in NoisyFields)
                {
                    if (d.FieldPath.StartsWith(noisy, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // Remove noisy prefix patterns
                foreach (var prefix in NoisyPrefixes)
                {
                    if (d.FieldPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // Filter fileID-only changes within references (structural, not user-visible)
                // e.g., /m_Materials[0]/fileID changing from 0 to 12345 (same asset, different ID)
                if (d.FieldPath.EndsWith("/fileID") && d.ChangeType == "modified")
                {
                    // Keep if the value actually resolves to different things
                    // But filter if it's just internal ID churn (both are numbers)
                    if (IsPlainNumber(d.OldValue) && IsPlainNumber(d.NewValue))
                        return true;
                }

                return false;
            });
        }

        private static bool IsPlainNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '-' && (c < '0' || c > '9')) return false;
            }
            return true;
        }

        // --- Hierarchy resolution (O(depth) with indexed parent lookup) ---

        private static string ResolveOwnerName(long? docFileId, PrefabGraph graph)
        {
            if (docFileId == null || graph == null) return null;

            if (graph.Components.TryGetValue(docFileId.Value, out ComponentInfo ci) &&
                graph.GameObjects.TryGetValue(ci.OwnerGameObjectFileId, out GameObjectInfo go))
                return go.Name;

            if (graph.GameObjects.TryGetValue(docFileId.Value, out GameObjectInfo go2))
                return go2.Name;

            return null;
        }

        private static string ResolveHierarchyPath(long? docFileId, PrefabGraph graph, string componentLabel)
        {
            if (docFileId == null || graph == null) return componentLabel ?? "";

            // Find the GO this doc belongs to
            long targetGoId = 0;
            if (graph.Components.TryGetValue(docFileId.Value, out ComponentInfo ci) && ci.OwnerGameObjectFileId != 0)
                targetGoId = ci.OwnerGameObjectFileId;
            else if (graph.GameObjects.ContainsKey(docFileId.Value))
                targetGoId = docFileId.Value;

            // Find the PrefabNode for this GO
            PrefabNode node = null;
            if (targetGoId != 0)
                graph.GameObjectToNode.TryGetValue(targetGoId, out node);

            // Fallback: maybe the doc IS a transform
            if (node == null)
                graph.TransformToNode.TryGetValue(docFileId.Value, out node);

            if (node == null) return componentLabel ?? "";

            // Build path using indexed parent lookup (O(depth) instead of O(N))
            var segments = new List<string>();
            BuildPathToRoot(node, graph, segments);
            var goPath = string.Join("/", segments);

            return string.IsNullOrEmpty(componentLabel) ? goPath : goPath + " (" + componentLabel + ")";
        }

        private static void BuildPathToRoot(PrefabNode node, PrefabGraph graph, List<string> acc)
        {
            // Walk up using ChildToParentTransform index
            if (graph.ChildToParentTransform.TryGetValue(node.TransformFileId, out long parentTid) &&
                graph.TransformToNode.TryGetValue(parentTid, out PrefabNode parent))
            {
                BuildPathToRoot(parent, graph, acc);
            }
            acc.Add(node.Name);
        }

        // --- Value formatting ---

        private static string TrimValue(string v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            if (v.Length <= 200 && v.IndexOf('\n') < 0) return v;
            // For multi-line or very long values, truncate
            int nl = v.IndexOf('\n');
            if (nl >= 0 && nl < 200) return v.Substring(0, nl) + "...";
            if (v.Length > 200) return v.Substring(0, 197) + "...";
            return v.Trim();
        }

        private static void PrettifyAllGuids(List<DiffResult> diffs)
        {
            for (int i = 0; i < diffs.Count; i++)
            {
                diffs[i].OldValue = PrettifyGuids(diffs[i].OldValue);
                diffs[i].NewValue = PrettifyGuids(diffs[i].NewValue);
            }
        }

        private static string PrettifyGuids(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return GuidRx.Replace(input, m =>
            {
                var guid = m.Value;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (obj != null) return obj.name + " (" + guid + ")";
                }
                return guid;
            });
        }

        private static string NodeToString(YamlNode node)
        {
            if (node == null) return null;
            if (node is YamlScalarNode s) return s.Value;
            return node.ToString();
        }
    }
}
