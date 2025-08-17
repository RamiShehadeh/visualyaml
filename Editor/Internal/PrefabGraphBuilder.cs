#if UNITY_EDITOR
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

            // collect GameObjects and Components, map component → GO
            for (int i = 0; i < docs.Count; i++)
            {
                var d = docs[i];
                var root = d.Yaml.RootNode as YamlMappingNode;
                if (root == null) continue;
                if (!root.Children.TryGetValue(new YamlScalarNode(d.TypeName), out var topVal)) continue;
                var map = topVal as YamlMappingNode;
                if (map == null) continue;

                if (d.TypeName == "GameObject")
                {
                    var go = new GameObjectInfo();
                    go.FileId = d.FileId;
                    go.Name = ReadString(map, "m_Name");
                    if (string.IsNullOrEmpty(go.Name)) go.Name = "GameObject(" + d.FileId + ")";

                    // Components list: m_Component: array of entries that include fileID
                    YamlNode compNode;
                    if (map.Children.TryGetValue(new YamlScalarNode("m_Component"), out compNode))
                    {
                        var seq = compNode as YamlSequenceNode;
                        if (seq != null)
                        {
                            foreach (var item in seq.Children)
                            {
                                long cid = TryExtractFileIdFromComponentEntry(item);
                                if (cid != 0) go.ComponentIds.Add(cid);
                            }
                        }
                    }
                    graph.GameObjects[d.FileId] = go;
                }
                else
                {
                    // Generic component
                    var ci = new ComponentInfo();
                    ci.FileId = d.FileId;
                    ci.ClassId = d.ClassId;
                    ci.TypeName = d.TypeName;
                    ci.OwnerGameObjectFileId = d.OwnerGameObjectFileId;
                    graph.Components[d.FileId] = ci;

                    if (ci.OwnerGameObjectFileId != 0 && !graph.ComponentToGameObject.ContainsKey(d.FileId))
                        graph.ComponentToGameObject[d.FileId] = ci.OwnerGameObjectFileId;
                }
            }

            // make PrefabNodes from Transforms and wire children
            var transformDocs = docs.Where(x => x.TypeName == "Transform").ToList();
            for (int i = 0; i < transformDocs.Count; i++)
            {
                var d = transformDocs[i];
                var root = d.Yaml.RootNode as YamlMappingNode;
                if (root == null) continue;
                YamlNode tVal;
                if (!root.Children.TryGetValue(new YamlScalarNode("Transform"), out tVal)) continue;
                var tMap = tVal as YamlMappingNode;
                if (tMap == null) continue;

                // Owner GO
                long goId = d.OwnerGameObjectFileId;
                if (goId == 0)
                {
                    long tmp;
                    if (TryReadFileIdRef(tMap, "m_GameObject", out tmp)) goId = tmp;
                }
                if (goId == 0) continue;

                GameObjectInfo goInfo;
                if (!graph.GameObjects.TryGetValue(goId, out goInfo)) continue;

                var node = new PrefabNode();
                node.Name = goInfo.Name;
                node.GameObjectFileId = goId;
                node.TransformFileId = d.FileId;

                // Non-transform components of this GO
                for (int c = 0; c < goInfo.ComponentIds.Count; c++)
                {
                    var compId = goInfo.ComponentIds[c];
                    if (compId == d.FileId) continue;
                    node.ComponentFileIds.Add(compId);
                }

                graph.TransformToNode[d.FileId] = node;
            }

            // Wire up parent/children, mark roots
            for (int i = 0; i < transformDocs.Count; i++)
            {
                var d = transformDocs[i];
                var root = d.Yaml.RootNode as YamlMappingNode;
                if (root == null) continue;
                YamlNode tVal;
                if (!root.Children.TryGetValue(new YamlScalarNode("Transform"), out tVal)) continue;
                var tMap = tVal as YamlMappingNode;
                if (tMap == null) continue;

                long fatherId;
                if (!TryReadFileIdRef(tMap, "m_Father", out fatherId)) fatherId = 0;

                var children = ReadChildrenTransformIds(tMap);

                PrefabNode node;
                if (!graph.TransformToNode.TryGetValue(d.FileId, out node)) continue;

                // Add children
                for (int k = 0; k < children.Count; k++)
                {
                    var childTid = children[k];
                    PrefabNode childNode;
                    if (graph.TransformToNode.TryGetValue(childTid, out childNode))
                    {
                        node.Children.Add(childNode);
                    }
                }

                // Root?
                if (fatherId == 0)
                {
                    if (!graph.Roots.Contains(node)) graph.Roots.Add(node);
                }
            }

            // Any orphans become roots
            foreach (var kv in graph.TransformToNode)
            {
                var n = kv.Value;
                bool hasParent = false;
                foreach (var p in graph.TransformToNode.Values)
                {
                    if (p.Children.Contains(n)) { hasParent = true; break; }
                }
                if (!hasParent && !graph.Roots.Contains(n)) graph.Roots.Add(n);
            }

            return graph;
        }

        // Helpers
        private static string ReadString(YamlMappingNode map, string key)
        {
            YamlNode val;
            if (map.Children.TryGetValue(new YamlScalarNode(key), out val))
            {
                var s = val as YamlScalarNode;
                if (s != null) return s.Value;
            }
            return null;
        }

        private static bool TryReadFileIdRef(YamlMappingNode map, string key, out long id)
        {
            id = 0;
            YamlNode val;
            if (!map.Children.TryGetValue(new YamlScalarNode(key), out val)) return false;
            var refMap = val as YamlMappingNode;
            if (refMap == null) return false;
            YamlNode idNode;
            if (refMap.Children.TryGetValue(new YamlScalarNode("fileID"), out idNode))
            {
                var idScalar = idNode as YamlScalarNode;
                if (idScalar != null)
                {
                    long tmp;
                    if (long.TryParse(idScalar.Value, out tmp)) { id = tmp; return true; }
                }
            }
            return false;
        }

        private static List<long> ReadChildrenTransformIds(YamlMappingNode tMap)
        {
            var result = new List<long>();
            YamlNode val;
            if (!tMap.Children.TryGetValue(new YamlScalarNode("m_Children"), out val)) return result;
            var seq = val as YamlSequenceNode;
            if (seq == null) return result;

            for (int i = 0; i < seq.Children.Count; i++)
            {
                var item = seq.Children[i] as YamlMappingNode;
                if (item == null) continue;
                YamlNode idNode;
                if (item.Children.TryGetValue(new YamlScalarNode("fileID"), out idNode))
                {
                    var idScalar = idNode as YamlScalarNode;
                    if (idScalar != null)
                    {
                        long tmp;
                        if (long.TryParse(idScalar.Value, out tmp)) result.Add(tmp);
                    }
                }
            }
            return result;
        }

        private static long TryExtractFileIdFromComponentEntry(YamlNode node)
        {
            // Formats:
            //  - "4: {fileID: 8}"
            //  - "component: {fileID: 8}"
            var map = node as YamlMappingNode;
            if (map != null)
            {
                // case: "4: {fileID: 8}"
                foreach (var kv in map.Children)
                {
                    var inner = kv.Value as YamlMappingNode;
                    if (inner != null)
                    {
                        YamlNode idNode;
                        if (inner.Children.TryGetValue(new YamlScalarNode("fileID"), out idNode))
                        {
                            var s = idNode as YamlScalarNode;
                            if (s != null)
                            {
                                long id;
                                if (long.TryParse(s.Value, out id)) return id;
                            }
                        }
                    }
                }
                // case: "component: {fileID: 8}"
                YamlNode compRef;
                if (map.Children.TryGetValue(new YamlScalarNode("component"), out compRef))
                {
                    var refMap = compRef as YamlMappingNode;
                    if (refMap != null)
                    {
                        YamlNode idNode;
                        if (refMap.Children.TryGetValue(new YamlScalarNode("fileID"), out idNode))
                        {
                            var s = idNode as YamlScalarNode;
                            if (s != null)
                            {
                                long id;
                                if (long.TryParse(s.Value, out id)) return id;
                            }
                        }
                    }
                }
            }
            return 0;
        }
    }
}
#endif
