using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace AssetDiff
{
    /// <summary>
    /// Resolves MonoBehaviour script GUIDs to human-readable class names.
    /// Separated from the parser to keep parsing testable without AssetDatabase.
    /// </summary>
    internal static class TypeResolver
    {
        /// <summary>
        /// Post-process parsed documents: resolve MonoBehaviour type names from script GUIDs.
        /// Call this after UnityYamlParser.ExtractDocuments() when running inside the Unity Editor.
        /// </summary>
        public static void ResolveMonoBehaviourNames(List<UnityYamlDocument> docs)
        {
            for (int i = 0; i < docs.Count; i++)
            {
                var doc = docs[i];
                if (doc.ClassId != 114) continue; // Only MonoBehaviours
                if (string.IsNullOrEmpty(doc.ScriptGuid)) continue;
                if (doc.TypeName != "MonoBehaviour") continue; // Already resolved

                var resolved = ResolveScriptName(doc.ScriptGuid);
                if (!string.IsNullOrEmpty(resolved))
                    doc.TypeName = resolved;
            }
        }

        private static string ResolveScriptName(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;

            var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (ms != null)
            {
                var klass = ms.GetClass();
                if (klass != null)
                {
                    return string.IsNullOrEmpty(klass.Namespace)
                        ? klass.Name
                        : klass.Namespace + "." + klass.Name;
                }
            }

            // Fallback: use filename
            return Path.GetFileNameWithoutExtension(path);
        }
    }
}
