#if UNITY_EDITOR
using System;
using System.Text.RegularExpressions;

namespace YamlPrefabDiff
{
    internal static class YamlHeaderParser
    {
        private static readonly Regex HeaderU = new("^---\\s+!u!(\\d+)\\s+&(\\-?\\d+)", RegexOptions.Compiled);
        private static readonly Regex HeaderNoU = new("^---\\s+(\\d+)\\s+&(\\-?\\d+)", RegexOptions.Compiled);

        public static bool TryParseHeader(string headerLine, out int classId, out long fileId)
        {
            classId = 0; fileId = 0;
            if (string.IsNullOrWhiteSpace(headerLine)) return false;

            var m = headerLine.Contains("!u!") ? HeaderU.Match(headerLine) : HeaderNoU.Match(headerLine);
            if (!m.Success) return false;
            classId = int.Parse(m.Groups[1].Value);
            fileId = long.Parse(m.Groups[2].Value);
            return true;
        }

        public static string ExtractTopKey(string documentText)
        {
            if (string.IsNullOrEmpty(documentText)) return string.Empty;
            var lines = documentText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '%' || line.StartsWith("---")) continue;
                if (line.EndsWith(":")) return line.Substring(0, line.Length - 1).Trim();
            }
            return string.Empty;
        }
    }
}
#endif