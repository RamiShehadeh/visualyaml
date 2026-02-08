using System.Text.RegularExpressions;

namespace AssetDiff
{
    internal readonly struct DocumentHeader
    {
        public readonly int ClassId;
        public readonly long FileId;
        public readonly bool IsStripped;

        public DocumentHeader(int classId, long fileId, bool isStripped)
        {
            ClassId = classId;
            FileId = fileId;
            IsStripped = isStripped;
        }
    }

    internal static class YamlHeaderParser
    {
        // --- !u!114 &-1234567890
        // --- !u!4 &5678 stripped
        private static readonly Regex HeaderRegex = new Regex(
            @"^---\s+!u!(\d+)\s+&(-?\d+)(\s+stripped)?",
            RegexOptions.Compiled);

        // Fallback for malformed headers without !u! prefix
        private static readonly Regex HeaderNoTagRegex = new Regex(
            @"^---\s+(\d+)\s+&(-?\d+)(\s+stripped)?",
            RegexOptions.Compiled);

        public static bool TryParse(string headerLine, out DocumentHeader header)
        {
            header = default;
            if (string.IsNullOrWhiteSpace(headerLine)) return false;

            var m = headerLine.Contains("!u!") ? HeaderRegex.Match(headerLine) : HeaderNoTagRegex.Match(headerLine);
            if (!m.Success) return false;

            int classId = int.Parse(m.Groups[1].Value);
            long fileId = long.Parse(m.Groups[2].Value);
            bool isStripped = m.Groups[3].Success;

            header = new DocumentHeader(classId, fileId, isStripped);
            return true;
        }

        public static string ExtractTopKey(string documentText)
        {
            if (string.IsNullOrEmpty(documentText)) return string.Empty;
            var lines = documentText.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '%' || line.StartsWith("---")) continue;
                int colon = line.IndexOf(':');
                if (colon > 0) return line.Substring(0, colon).Trim();
            }
            return string.Empty;
        }
    }
}
