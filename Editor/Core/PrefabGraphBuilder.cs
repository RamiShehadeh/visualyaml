using System.Collections.Generic;
using YamlDotNet.RepresentationModel;

namespace VisualYAML
{
    internal static class PrefabGraphBuilder
    {
        public static PrefabGraph Build(List<UnityYamlDocument> docs)
        {
            var graph = new PrefabGraph();

            CollectGameObjectsAndComponents(docs, graph);
            BuildTransformNodes(docs, graph);
            WireParentChild(docs, graph);
            FindRoots(graph);

            return graph;
        }

        private static void CollectGameObjectsAndComponents(List<UnityYamlDocument> docs, PrefabGraph graph)
        {
            for (int i = 0; i < docs.Count; i++)
            {
                var d = docs[i];

                if (UnityClassIds.IsGameObject(d.ClassId))
                {
                    var map = UnityYamlParser.GetContentMap(d);
                    var go = new GameObjectInfo
                    {
                        FileId = d.FileId,
                        Name = (map != null ? UnityYamlParser.ReadScalar(map, "m_Name") : null)
                               ?? "GameObject(" + d.FileId + ")"
                    };

                    // Parse m_Component array
                    if (map != null &&
                        map.Children.TryGetValue(new YamlScalarNode("m_Component"), out var compNode) &&
                        compNode is YamlSequenceNode seq)
                    {
                        for (int j = 0; j < seq.Children.Count; j++)
                        {
                            long cid = ExtractFileIdFromComponentEntry(seq.Children[j]);
                            if (cid != 0) go.ComponentIds.Add(cid);
                        }
                    }

                    graph.GameObjects[d.FileId] = go;
                }
                else if (!UnityClassIds.IsPrefabInstance(d.ClassId))
                {
                    var ci = new ComponentInfo
                    {
                        FileId = d.FileId,
                        ClassId = d.ClassId,
                        TypeName = d.TypeName,
                        OwnerGameObjectFileId = d.OwnerGameObjectFileId
                    };
                    graph.Components[d.FileId] = ci;

                    if (ci.OwnerGameObjectFileId != 0)
                        graph.ComponentToGameObject[d.FileId] = ci.OwnerGameObjectFileId;
                }
            }
        }

        private static void BuildTransformNodes(List<UnityYamlDocument> docs, PrefabGraph graph)
        {
            for (int i = 0; i < docs.Count; i++)
            {
                var d = docs[i];
                if (!UnityClassIds.IsTransformType(d.ClassId)) continue;

                // For stripped documents, create placeholder nodes
                if (d.IsStripped)
                {
                    CreateStrippedNode(d, graph);
                    continue;
                }

                var map = GetTransformMap(d);
                if (map == null) continue;

                // Determine owner GO
                long goId = d.OwnerGameObjectFileId;
                if (goId == 0)
                    goId = UnityYamlParser.ReadFileIdRef(map, "m_GameObject");
                if (goId == 0) continue;

                if (!graph.GameObjects.TryGetValue(goId, out GameObjectInfo goInfo)) continue;

                var node = new PrefabNode
                {
                    Name = goInfo.Name,
                    GameObjectFileId = goId,
                    TransformFileId = d.FileId
                };

                // Collect non-transform components for this GO
                for (int c = 0; c < goInfo.ComponentIds.Count; c++)
                {
                    if (goInfo.ComponentIds[c] != d.FileId)
                        node.ComponentFileIds.Add(goInfo.ComponentIds[c]);
                }

                graph.TransformToNode[d.FileId] = node;
                graph.GameObjectToNode[goId] = node;
            }
        }

        private static void CreateStrippedNode(UnityYamlDocument d, PrefabGraph graph)
        {
            // Stripped transforms are placeholders for nested prefab references.
            // We create a minimal node so hierarchy resolution doesn't break.
            var node = new PrefabNode
            {
                Name = "(Nested Prefab)",
                GameObjectFileId = d.OwnerGameObjectFileId,
                TransformFileId = d.FileId
            };

            // If we have an owner GO in the graph, use its name
            if (d.OwnerGameObjectFileId != 0 &&
                graph.GameObjects.TryGetValue(d.OwnerGameObjectFileId, out var goInfo))
            {
                node.Name = goInfo.Name;
            }

            graph.TransformToNode[d.FileId] = node;
            if (d.OwnerGameObjectFileId != 0)
                graph.GameObjectToNode[d.OwnerGameObjectFileId] = node;
        }

        private static void WireParentChild(List<UnityYamlDocument> docs, PrefabGraph graph)
        {
            for (int i = 0; i < docs.Count; i++)
            {
                var d = docs[i];
                if (!UnityClassIds.IsTransformType(d.ClassId)) continue;
                if (d.IsStripped) continue;

                if (!graph.TransformToNode.TryGetValue(d.FileId, out PrefabNode node)) continue;

                var map = GetTransformMap(d);
                if (map == null) continue;

                long fatherId = UnityYamlParser.ReadFileIdRef(map, "m_Father");

                // Wire children via m_Children array
                if (map.Children.TryGetValue(new YamlScalarNode("m_Children"), out var childrenNode) &&
                    childrenNode is YamlSequenceNode childSeq)
                {
                    for (int j = 0; j < childSeq.Children.Count; j++)
                    {
                        var childItem = childSeq.Children[j] as YamlMappingNode;
                        if (childItem == null) continue;
                        if (childItem.Children.TryGetValue(new YamlScalarNode("fileID"), out var idNode) &&
                            idNode is YamlScalarNode idScalar &&
                            long.TryParse(idScalar.Value, out long childTid))
                        {
                            if (graph.TransformToNode.TryGetValue(childTid, out PrefabNode childNode))
                            {
                                node.Children.Add(childNode);
                                graph.ChildToParentTransform[childTid] = d.FileId;
                            }
                        }
                    }
                }

                // Mark root if no father
                if (fatherId == 0 && !graph.Roots.Contains(node))
                    graph.Roots.Add(node);
            }
        }

        private static void FindRoots(PrefabGraph graph)
        {
            // Any nodes not referenced as children are roots (orphan safety net)
            foreach (var kv in graph.TransformToNode)
            {
                if (!graph.ChildToParentTransform.ContainsKey(kv.Key) && !graph.Roots.Contains(kv.Value))
                    graph.Roots.Add(kv.Value);
            }
        }

        private static YamlMappingNode GetTransformMap(UnityYamlDocument d)
        {
            if (d.Yaml == null) return null;
            var root = d.Yaml.RootNode as YamlMappingNode;
            if (root == null) return null;

            // Use the correct key based on class ID (Transform vs RectTransform)
            var key = UnityClassIds.GetTransformKey(d.ClassId);
            if (root.Children.TryGetValue(new YamlScalarNode(key), out var val) && val is YamlMappingNode map)
                return map;

            // Fallback: try the TypeName
            if (key != d.TypeName &&
                root.Children.TryGetValue(new YamlScalarNode(d.TypeName), out var val2) && val2 is YamlMappingNode map2)
                return map2;

            return null;
        }

        /// <summary>
        /// Extract fileID from a component entry in m_Component array.
        /// Handles both formats:
        ///   - Old: { 4: {fileID: 8} }
        ///   - New: { component: {fileID: 8} }
        /// </summary>
        private static long ExtractFileIdFromComponentEntry(YamlNode node)
        {
            var map = node as YamlMappingNode;
            if (map == null) return 0;

            // Try "component" key first (Unity 2019+)
            if (map.Children.TryGetValue(new YamlScalarNode("component"), out var compRef))
                return ExtractFileIdFromRef(compRef);

            // Fallback: iterate keys (handles "4: {fileID: 8}" format)
            foreach (var kv in map.Children)
            {
                long id = ExtractFileIdFromRef(kv.Value);
                if (id != 0) return id;
            }

            return 0;
        }

        private static long ExtractFileIdFromRef(YamlNode node)
        {
            if (node is YamlMappingNode refMap &&
                refMap.Children.TryGetValue(new YamlScalarNode("fileID"), out var idNode) &&
                idNode is YamlScalarNode idScalar &&
                long.TryParse(idScalar.Value, out long id))
            {
                return id;
            }
            return 0;
        }
    }
}
