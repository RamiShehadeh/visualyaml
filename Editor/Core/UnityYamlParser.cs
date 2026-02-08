using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using YamlDotNet.RepresentationModel;

namespace AssetDiff
{
    internal static class UnityYamlParser
    {
        public static List<UnityYamlDocument> ExtractDocuments(string fullYaml)
        {
            var docs = new List<UnityYamlDocument>();
            if (string.IsNullOrEmpty(fullYaml)) return docs;

            var rawDocs = YamlPreprocessor.SplitAndSanitize(fullYaml);

            for (int i = 0; i < rawDocs.Count; i++)
            {
                var raw = rawDocs[i];
                var parsed = ParseDocument(raw.HeaderLine, raw.Body);
                if (parsed != null)
                    docs.Add(parsed);
            }

            return docs;
        }

        private static UnityYamlDocument ParseDocument(string headerLine, string body)
        {
            if (!YamlHeaderParser.TryParse(headerLine, out DocumentHeader header))
                return null;

            var doc = new UnityYamlDocument
            {
                ClassId = header.ClassId,
                FileId = header.FileId,
                IsStripped = header.IsStripped,
                RawText = headerLine + "\n" + body,
                TypeName = UnityClassIds.GetTypeName(header.ClassId),
            };

            // Stripped documents have no real YAML body to parse — they're placeholders
            if (header.IsStripped)
            {
                // Try to extract owner GO and corresponding source from the minimal body
                ExtractStrippedInfo(body, doc);
                return doc;
            }

            // Parse YAML body
            var sanitized = YamlPreprocessor.SanitizeBody(body);
            var ys = new YamlStream();
            try
            {
                using (var sr = new StringReader(sanitized))
                    ys.Load(sr);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AssetDiff] YAML parse error in !u!{header.ClassId} &{header.FileId}: {e.Message}");
                return doc; // Return doc without Yaml — still useful for graph building
            }

            if (ys.Documents.Count == 0) return doc;
            doc.Yaml = ys.Documents[0];

            // Use actual top key from body if available (more reliable than class ID map)
            var topKey = YamlHeaderParser.ExtractTopKey(sanitized);
            if (!string.IsNullOrEmpty(topKey))
                doc.TypeName = topKey;

            // Extract component owner (m_GameObject.fileID)
            var contentMap = GetContentMap(doc);
            if (contentMap != null)
            {
                doc.OwnerGameObjectFileId = ReadFileIdRef(contentMap, "m_GameObject");

                // MonoBehaviour: extract script GUID for type resolution
                if (header.ClassId == 114)
                    ExtractMonoBehaviourInfo(contentMap, doc);
            }

            return doc;
        }

        private static void ExtractStrippedInfo(string body, UnityYamlDocument doc)
        {
            // Stripped docs look like:
            //   Transform:
            //     m_CorrespondingSourceObject: {fileID: 5678, guid: abc123, type: 3}
            //     m_PrefabInstance: {fileID: 9012}
            // We parse this manually since the body may be incomplete.
            // For graph building we need m_PrefabInstance fileID.

            var lines = body.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                if (line.StartsWith("m_GameObject:"))
                {
                    long id = ExtractInlineFileId(line);
                    if (id != 0) doc.OwnerGameObjectFileId = id;
                }
            }

            // Also try a minimal YAML parse — sometimes stripped docs are valid enough
            try
            {
                var ys = new YamlStream();
                using (var sr = new StringReader(body))
                    ys.Load(sr);
                if (ys.Documents.Count > 0)
                    doc.Yaml = ys.Documents[0];
            }
            catch
            {
                // Expected for most stripped documents
            }
        }

        private static void ExtractMonoBehaviourInfo(YamlMappingNode map, UnityYamlDocument doc)
        {
            if (map.Children.TryGetValue(new YamlScalarNode("m_Script"), out var sRef) &&
                sRef is YamlMappingNode sMap)
            {
                if (sMap.Children.TryGetValue(new YamlScalarNode("guid"), out var gNode) &&
                    gNode is YamlScalarNode gScalar)
                {
                    doc.ScriptGuid = gScalar.Value;
                }
            }

            // Type name resolution is done later by TypeResolver (keeps parser AssetDatabase-free for testing)
        }

        /// <summary>
        /// Get the inner content mapping node for a document.
        /// E.g., for a Transform doc with root key "Transform:", returns the mapping under that key.
        /// </summary>
        internal static YamlMappingNode GetContentMap(UnityYamlDocument doc)
        {
            if (doc.Yaml == null) return null;
            var root = doc.Yaml.RootNode as YamlMappingNode;
            if (root == null) return null;

            if (root.Children.TryGetValue(new YamlScalarNode(doc.TypeName), out var val) &&
                val is YamlMappingNode map)
                return map;

            // Fallback: try the top key from class ID mapping
            var classTypeName = UnityClassIds.GetTypeName(doc.ClassId);
            if (classTypeName != doc.TypeName &&
                root.Children.TryGetValue(new YamlScalarNode(classTypeName), out var val2) &&
                val2 is YamlMappingNode map2)
                return map2;

            return null;
        }

        internal static long ReadFileIdRef(YamlMappingNode map, string key)
        {
            if (map == null) return 0;
            if (!map.Children.TryGetValue(new YamlScalarNode(key), out var val)) return 0;
            if (val is YamlMappingNode refMap &&
                refMap.Children.TryGetValue(new YamlScalarNode("fileID"), out var idNode) &&
                idNode is YamlScalarNode idScalar &&
                long.TryParse(idScalar.Value, out long id))
            {
                return id;
            }
            return 0;
        }

        internal static string ReadScalar(YamlMappingNode map, string key)
        {
            if (map == null) return null;
            if (map.Children.TryGetValue(new YamlScalarNode(key), out var val) &&
                val is YamlScalarNode scalar)
                return scalar.Value;
            return null;
        }

        private static long ExtractInlineFileId(string line)
        {
            // Quick regex-free extraction of fileID from: "m_GameObject: {fileID: 12345}"
            int fileIdIdx = line.IndexOf("fileID:", StringComparison.Ordinal);
            if (fileIdIdx < 0) return 0;
            int start = fileIdIdx + 7; // length of "fileID:"
            while (start < line.Length && (line[start] == ' ' || line[start] == '\t')) start++;
            int end = start;
            if (end < line.Length && line[end] == '-') end++;
            while (end < line.Length && char.IsDigit(line[end])) end++;
            if (end > start && long.TryParse(line.Substring(start, end - start), out long id))
                return id;
            return 0;
        }
    }
}
