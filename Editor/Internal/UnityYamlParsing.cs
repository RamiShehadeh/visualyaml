#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using YamlDotNet.RepresentationModel;

namespace YamlPrefabDiff
{
    internal class UnityYamlDocument
    {
        public int ClassId;               // !u!xx
        public long FileId;               // &nnn
        public string TypeName;           // GameObject, Transform, MonoBehaviour (resolved), etc.
        public string RawText;            // full text (minus directives)
        public YamlDocument Yaml;         // parsed doc
        public long OwnerGameObjectFileId;// for components (m_GameObject.fileID)
        public string ScriptGuid;         // MonoBehaviour -> m_Script.guid
        public bool IsStripped;           // header may include "stripped"
    }

    internal static class UnityYamlParsing
    {
        private static readonly Regex DirectiveLine = new Regex("^%", RegexOptions.Compiled);

        public static List<UnityYamlDocument> ExtractDocuments(string fullYaml)
        {
            var docs = new List<UnityYamlDocument>();
            if (string.IsNullOrEmpty(fullYaml)) return docs;

            var lines = fullYaml.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var current = new List<string>();

            void Flush()
            {
                if (current.Count == 0) return;
                var docText = string.Join("\n", current);
                var parsed = ParseDocument(docText);
                if (parsed != null) docs.Add(parsed);
                current.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("---")) Flush();
                current.Add(lines[i]);
            }
            Flush();

            return docs;
        }

        private static UnityYamlDocument ParseDocument(string docText)
        {
            if (string.IsNullOrWhiteSpace(docText)) return null;

            // strip directives, capture header
            var rawLines = docText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var filtered = new List<string>();
            string header = string.Empty;
            bool headerSeen = false;

            for (int i = 0; i < rawLines.Length; i++)
            {
                var l = rawLines[i];
                var t = l.TrimStart();
                if (t.StartsWith("%")) continue;
                if (!headerSeen && t.StartsWith("---")) { header = t; headerSeen = true; continue; }
                filtered.Add(l);
            }

            int classId; long fileId;
            if (!YamlHeaderParser.TryParseHeader(header, out classId, out fileId)) return null;

            var textForParsing = string.Join("\n", filtered).Trim();
            if (string.IsNullOrEmpty(textForParsing)) return null;

            var typeName = YamlHeaderParser.ExtractTopKey(textForParsing);
            var ys = new YamlStream();
            try
            {
                using (var sr = new StringReader(textForParsing))
                    ys.Load(sr);
            }
            catch (Exception e)
            {
                Debug.LogError("YAML parse error: " + e.Message);
                return null;
            }
            if (ys.Documents.Count == 0) return null;

            var root = ys.Documents[0].RootNode as YamlMappingNode;
            YamlMappingNode map = null;
            if (root != null && root.Children.TryGetValue(new YamlScalarNode(typeName), out var tv) && tv is YamlMappingNode m)
                map = m;

            // Owner GO
            long ownerGo = 0;
            if (map != null && map.Children.TryGetValue(new YamlScalarNode("m_GameObject"), out var goRef) && goRef is YamlMappingNode goMap)
            {
                if (goMap.Children.TryGetValue(new YamlScalarNode("fileID"), out var idNode) && idNode is YamlScalarNode s)
                {
                    long idTmp;
                    if (long.TryParse(s.Value, out idTmp)) ownerGo = idTmp;
                }
            }

            // MonoBehaviour → resolve script/class name, capture guid
            string scriptGuid = null;
            if (string.Equals(typeName, "MonoBehaviour", StringComparison.OrdinalIgnoreCase) && map != null)
            {
                if (map.Children.TryGetValue(new YamlScalarNode("m_Script"), out var sRef) && sRef is YamlMappingNode sMap)
                {
                    if (sMap.Children.TryGetValue(new YamlScalarNode("guid"), out var gNode) && gNode is YamlScalarNode gScalar)
                        scriptGuid = gScalar.Value;
                }

                if (!string.IsNullOrEmpty(scriptGuid))
                {
                    var path = AssetDatabase.GUIDToAssetPath(scriptGuid);
                    var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                    var klass = ms != null ? ms.GetClass() : null;
                    if (klass != null)
                        typeName = string.IsNullOrEmpty(klass.Namespace) ? klass.Name : (klass.Namespace + "." + klass.Name);
                    else
                        typeName = Path.GetFileNameWithoutExtension(path); // fallback
                }
                else
                {
                    typeName = TryResolveMonoScriptName(textForParsing) ?? "MonoBehaviour";
                }
            }

            var isStripped = header.Contains(" stripped");

            return new UnityYamlDocument
            {
                ClassId = classId,
                FileId = fileId,
                TypeName = typeName,
                RawText = docText,
                Yaml = ys.Documents[0],
                OwnerGameObjectFileId = ownerGo,
                ScriptGuid = scriptGuid,
                IsStripped = isStripped
            };
        }

        private static string TryResolveMonoScriptName(string text)
        {
            // fallback using m_Name (if present) or m_Script guid -> asset name
            var nameRx = new Regex(@"\bm_Name:\s*(\S+)");
            var m = nameRx.Match(text);
            if (m.Success && !string.IsNullOrEmpty(m.Groups[1].Value) && m.Groups[1].Value != "0")
                return m.Groups[1].Value;

            var guidRx = new Regex(@"m_Script:\s*\{[^}]*guid:\s*([0-9A-Fa-f]{32})");
            var g = guidRx.Match(text);
            if (g.Success)
            {
                var guid = g.Groups[1].Value;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    return Path.GetFileNameWithoutExtension(path);
            }
            return null;
        }
    }
}
#endif
