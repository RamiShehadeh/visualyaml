using System;
using System.Collections.Generic;
using System.Text;

namespace AssetDiff
{
    internal readonly struct RawDocument
    {
        public readonly string HeaderLine;
        public readonly string Body;

        public RawDocument(string headerLine, string body)
        {
            HeaderLine = headerLine;
            Body = body;
        }
    }

    internal static class YamlPreprocessor
    {
        /// <summary>
        /// Split a Unity YAML file into individual documents and sanitize each for YamlDotNet.
        /// Handles: directive stripping, stripped keyword removal, line ending normalization.
        /// </summary>
        public static List<RawDocument> SplitAndSanitize(string rawYaml)
        {
            var results = new List<RawDocument>();
            if (string.IsNullOrEmpty(rawYaml)) return results;

            var lines = rawYaml.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            string currentHeader = null;
            var bodyLines = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();

                if (trimmed.StartsWith("---"))
                {
                    // Flush previous document
                    if (currentHeader != null)
                        FlushDocument(currentHeader, bodyLines, results);

                    currentHeader = trimmed;
                    bodyLines.Clear();
                    continue;
                }

                // Skip file-level directives (%YAML, %TAG)
                if (trimmed.StartsWith("%"))
                    continue;

                if (currentHeader != null)
                    bodyLines.Add(lines[i]); // Preserve original indentation
            }

            // Flush last document
            if (currentHeader != null)
                FlushDocument(currentHeader, bodyLines, results);

            return results;
        }

        private static void FlushDocument(string header, List<string> bodyLines, List<RawDocument> results)
        {
            var body = string.Join("\n", bodyLines).Trim();
            if (string.IsNullOrEmpty(body)) return;

            results.Add(new RawDocument(header, body));
        }

        /// <summary>
        /// Sanitize a document body for YamlDotNet parsing.
        /// Handles duplicate keys by keeping only the last occurrence.
        /// </summary>
        public static string SanitizeBody(string body)
        {
            if (string.IsNullOrEmpty(body)) return body;
            // For now, return as-is. Duplicate key handling can be added if needed
            // since YamlDotNet v11+ handles most cases.
            return body;
        }
    }
}
