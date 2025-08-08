#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YamlDotNet.RepresentationModel;

namespace YamlPrefabDiff
{
    internal static class PrefabGraphBuilder
    {
        public static PrefabGraph Build(List<UnityYamlDocument> docs)
        {
            var graph = new PrefabGraph();

            // First pass: collect GameObjects & Components; map component -> GO and remember Transform IDs
            foreach (var d in docs)
            {
                var root = (YamlMappingNode)d.Yaml.RootNode; // top-level mapping (GameObject:, Transform:, etc.)
                var topKey = d.TypeName;
                if (!root.Children.TryGetValue(new YamlScalarNode(topKey), out var topVal))
                    continue;
                var map = topVal as YamlMappingNode;
                if (map == null) continue;

                if (string.Equals(topKey, "GameObject", StringComparison.Ordinal))
                {
                    var go = new GameObjectInfo { FileId = d.FileId, Name = ReadString(map, "m_Name") ?? $"GameObject({d.FileId})" };

                    // Components list: m_Component: - {fileID: X, ...}
                    if (map.Children.TryGetValue(new YamlScalarNode("m_Component"), out var compsNode) && compsNode is YamlSequenceNode seq)
                    {
                        foreach (var item in seq.Children)
                        {
                            // Entries look like: 4: {fileID: 123}  OR  - component: {fileID: 123} depending on Unity version
                            long compId = TryExtractFileIdFromComponentEntry(item);
                            if (compId != 0) go.ComponentIds.Add(compId);
                        }
                    }
                    graph.GameObjects[d.FileId] = go;
                }
                else
                {
                    // Generic component mapping
                    var ci = new ComponentInfo
                    {
                        FileId = d.FileId,
                        ClassId = d.ClassId,
                        TypeName = d.TypeName,
                        OwnerGameObjectFileId = ReadFileIdRef(map, "m_GameObject") ?? 0
                    };
                    graph.Components[d.FileId] = ci;
                    if (ci.OwnerGameObjectFileId != 0)
                        graph.ComponentToGameObject[d.FileId] = ci.OwnerGameObjectFileId;
                }
            }

            // Second pass: build Transform relationships into PrefabNode tree(s)
            var transformToGo = graph.Components
                .Where(kv => string.Equals(kv.Value.TypeName, "Transform", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value.OwnerGameObjectFileId);

            // Build nodes for each GO that has a Transform component
            foreach (var kv in transformToGo)
            {
                var transformFileId = kv.Key;
                var goFileId = kv.Value;
                if (!graph.GameObjects.TryGetValue(goFileId, out var goInfo)) continue;

                var node = new PrefabNode
                {
                    Name = goInfo.Name,
                    GameObjectFileId = goFileId,
                    TransformFileId = transformFileId,
                };

                // Non-transform components under this GO
                foreach (var compId in goInfo.ComponentIds)
                {
                    if (compId == transformFileId) continue;
                    node.ComponentFileIds.Add(compId);
                }

                graph.TransformToNode[transformFileId] = node;
            }

            // Wire parent/children
            foreach (var d in docs.Where(x => x.TypeName == "Transform"))
            {
                var root = (YamlMappingNode)d.Yaml.RootNode;
                if (!root.Children.TryGetValue(new YamlScalarNode("Transform"), out var tVal)) continue;
                if (tVal is not YamlMappingNode tMap) continue;

                var fatherId = ReadFileIdRef(tMap, "m_Father") ?? 0;
                var children = ReadChildrenTransformIds(tMap);

                if (graph.TransformToNode.TryGetValue(d.FileId, out var node))
                {
                    // Add children
                    foreach (var childTid in children)
                    {
                        if (graph.TransformToNode.TryGetValue(childTid, out var childNode))
                        {
                            node.Children.Add(childNode);
                        }
                    }

                    // If no father, it's a root
                    if (fatherId == 0)
                        graph.Roots.Add(node);
                }
            }

            // Fallback: any orphan nodes not added (rare malformed) — add as roots
            foreach (var n in graph.TransformToNode.Values)
            {
                if (!graph.Roots.Contains(n) && !graph.TransformToNode.Values.Any(p => p.Children.Contains(n)))
                    graph.Roots.Add(n);
            }

            return graph;
        }

        // Utilities
        private static string ReadString(YamlMappingNode map, string key)
        {
            if (map.Children.TryGetValue(new YamlScalarNode(key), out var val) && val is YamlScalarNode s)
                return s.Value;
            return null;
        }

        private static long? ReadFileIdRef(YamlMappingNode map, string key)
        {
            // key: {fileID: 123, guid: ..., type: ...}
            if (!map.Children.TryGetValue(new YamlScalarNode(key), out var val)) return null;
            if (val is YamlMappingNode refMap)
            {
                if (refMap.Children.TryGetValue(new YamlScalarNode("fileID"), out var idNode) && idNode is YamlScalarNode idScalar)
                {
                    if (long.TryParse(idScalar.Value, out var id)) return id;
                }
            }
            return null;
        }

        private static List<long> ReadChildrenTransformIds(YamlMappingNode tMap)
        {
            var result = new List<long>();
            if (!tMap.Children.TryGetValue(new YamlScalarNode("m_Children"), out var val)) return result;
            if (val is YamlSequenceNode seq)
            {
                foreach (var item in seq.Children)
                {
                    if (item is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode("fileID"), out var idNode) && idNode is YamlScalarNode idScalar)
                    {
                        if (long.TryParse(idScalar.Value, out var id)) result.Add(id);
                    }
                }
            }
            return result;
        }

        private static long TryExtractFileIdFromComponentEntry(YamlNode node)
        {
            // Formats seen:
            //  - "4: {fileID: 8}"
            //  - "- component: {fileID: 8}"
            if (node is YamlMappingNode map)
            {
                // case: "4: {fileID: 8}"
                foreach (var kv in map.Children)
                {
                    if (kv.Value is YamlMappingNode inner && inner.Children.TryGetValue(new YamlScalarNode("fileID"), out var idNode) && idNode is YamlScalarNode s && long.TryParse(s.Value, out var id))
                        return id;
                }
                // case: "component: {fileID: 8}"
                if (map.Children.TryGetValue(new YamlScalarNode("component"), out var compRef) && compRef is YamlMappingNode refMap)
                {
                    if (refMap.Children.TryGetValue(new YamlScalarNode("fileID"), out var idNode) && idNode is YamlScalarNode s && long.TryParse(s.Value, out var id))
                        return id;
                }
            }
            return 0;
        }
    }
}
#endif