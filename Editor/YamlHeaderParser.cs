using System;
using System.Text.RegularExpressions;

public static class YamlHeaderParser
{
    // Two regex patterns: one expecting "!u!" and one without.
    private static Regex headerRegexWithU = new Regex(@"^---\s+!u!(\d+)\s+&(\d+)", RegexOptions.Compiled);
    private static Regex headerRegexWithoutU = new Regex(@"^---\s+(\d+)\s+&(\d+)", RegexOptions.Compiled);

    public static string ExtractClassId(string headerLine)
    {
        if (headerLine.Contains("!u!"))
        {
            var match = headerRegexWithU.Match(headerLine);
            if (match.Success && match.Groups.Count >= 2)
                return match.Groups[1].Value;
        }
        else
        {
            var match = headerRegexWithoutU.Match(headerLine);
            if (match.Success && match.Groups.Count >= 2)
                return match.Groups[1].Value;
        }
        return "";
    }

    public static string ExtractFileId(string headerLine)
    {
        if (headerLine.Contains("!u!"))
        {
            var match = headerRegexWithU.Match(headerLine);
            if (match.Success && match.Groups.Count >= 3)
                return match.Groups[2].Value;
        }
        else
        {
            var match = headerRegexWithoutU.Match(headerLine);
            if (match.Success && match.Groups.Count >= 3)
                return match.Groups[2].Value;
        }
        return "";
    }

    public static string ExtractComponentType(string documentText)
    {
        // Split the document into lines.
        var lines = documentText.Split(new[] { "\n", "\r\n" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Skip header lines (starting with % or ---).
            if (trimmed.StartsWith("%") || trimmed.StartsWith("---"))
                continue;
            // Look for the first line ending with ':'.
            if (trimmed.EndsWith(":"))
            {
                return trimmed.Substring(0, trimmed.Length - 1).Trim();
            }
        }
        return "";
    }
}
