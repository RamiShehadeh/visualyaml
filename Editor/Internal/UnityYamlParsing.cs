#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using YamlDotNet.RepresentationModel;
using UnityEditor;

namespace YamlPrefabDiff
{
    internal class UnityYamlDocument
    {
        public int ClassId;        // !u!xx
        public long FileId;        // &nnn
        public string TypeName;    // e.g. GameObject, Transform, MonoBehaviour, etc.
        public string RawText;     // entire document text (without leading directives)
        public YamlDocument Yaml;  // parsed doc (YamlDotNet)
        public long OwnerGameObjectFileId;  
        public string ScriptGuid; 
    }

    internal static class UnityYamlParsing
    {
        private static readonly Regex DirectiveLine = new("^%", RegexOptions.Compiled);

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

            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("---"))
                {
                    Flush();
                }
                current.Add(line);
            }
            Flush();
            return docs;
        }

        private static UnityYamlDocument ParseDocument(string docText)
        {
            if (string.IsNullOrWhiteSpace(docText)) return null;

            // Strip directives (%TAG etc.) for parser stability
            var rawLines = docText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var filtered = new List<string>();
            string header = string.Empty;
            int headerIndex = -1;

            for (int i = 0; i < rawLines.Length; i++)
            {
                var l = rawLines[i];
                var t = l.TrimStart();
                if (t.StartsWith("%")) continue; // drop directives
                if (headerIndex < 0 && t.StartsWith("---")) { header = t; headerIndex = i; continue; }
                filtered.Add(l);
            }

            if (!YamlHeaderParser.TryParseHeader(header, out var classId, out var fileId))
                return null; // not a Unity YAML doc

            var textForParsing = string.Join("\n", filtered).Trim();
            if (string.IsNullOrEmpty(textForParsing)) return null;

            var typeName = YamlHeaderParser.ExtractTopKey(textForParsing);

            var ys = new YamlStream();
            try
            {
                using var sr = new StringReader(textForParsing);
                ys.Load(sr);
            }
            catch (Exception e)
            {
                Debug.LogError($"YAML parse error: {e.Message}");
                return null;
            }
            if (ys.Documents.Count == 0) return null;

            // get the top mapping
            var topMap = ys.Documents[0].RootNode as YamlMappingNode;
            YamlMappingNode map = null;
            if (topMap != null && topMap.Children.TryGetValue(new YamlScalarNode(typeName), out var tv) && tv is YamlMappingNode m0)
                map = m0;

            // capture owner GO fileID if present (for components)
            long ownerGo = 0;
            if (map != null && map.Children.TryGetValue(new YamlScalarNode("m_GameObject"), out var goRef) && goRef is YamlMappingNode goMap)
            {
                if (goMap.Children.TryGetValue(new YamlScalarNode("fileID"), out var idNode) && idNode is YamlScalarNode s && long.TryParse(s.Value, out var id))
                    ownerGo = id;
            }

            // resolve MonoBehaviour -> C# class (prefer MonoScript.GetClass)
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
                        typeName = string.IsNullOrEmpty(klass.Namespace) ? klass.Name : $"{klass.Namespace}.{klass.Name}";
                    else
                        typeName = Path.GetFileNameWithoutExtension(path); // fallback to file name
                }
                else
                {
                    // last-resort fallback to previous heuristic
                    typeName = TryResolveMonoScriptName(textForParsing) ?? "MonoBehaviour";
                }
            }

            return new UnityYamlDocument
            {
                ClassId = classId,
                FileId = fileId,
                TypeName = typeName,
                RawText = docText,
                Yaml = ys.Documents[0],
                OwnerGameObjectFileId = ownerGo,
                ScriptGuid = scriptGuid
            };
        }

        private static string TryResolveMonoScriptName(string text)
        {
            // TODO: Fix
            // Prefer m_Name if non-empty; else resolve m_Script guid
            var nameRx = new Regex(@"\bm_Name:\s*(\S+)");
            var m = nameRx.Match(text);
            if (m.Success && !string.IsNullOrEmpty(m.Groups[1].Value) && m.Groups[1].Value != "0")
                return m.Groups[1].Value;

            var guidRx = new Regex(@"m_Script:\s*\{[^}]*guid:\s*([0-9A-Fa-f]{32})");
            var g = guidRx.Match(text);
            if (g.Success)
            {
                var guid = g.Groups[1].Value;
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    var obj = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (obj != null) return obj.name;
                }
            }
            return null;
        }
    }
}
#endif