#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using YamlDotNet.RepresentationModel;
using System;
using Debug = UnityEngine.Debug;
using System.Text.RegularExpressions;

/// <summary>
/// YAML diff algorithm. This parses each YAML file using YamlDotNet and recursively compares nodes.
/// </summary>
public static class YamlDiffer
{

    public static List<DiffResult> DiffYaml(string yamlOld, string yamlNew)
    {
        List<DiffResult> diffs = new List<DiffResult>();

        List<YamlDocumentInfo> oldDocs = ExtractYamlDocuments(yamlOld);
        List<YamlDocumentInfo> newDocs = ExtractYamlDocuments(yamlNew);

        // Match documents by fileId.
        foreach (var oldDoc in oldDocs)
        {
            // Try to find a matching document in the new version.
            var newDoc = newDocs.Find(d => d.FileId == oldDoc.FileId);
            if (newDoc != null)
            {
                // Diff the two documents.
                DiffYamlNodes(oldDoc.Document.RootNode, newDoc.Document.RootNode, "", diffs);
                // Attach the component type info to all diff results from this document.
                foreach (var diff in diffs)
                {
                    if (string.IsNullOrEmpty(diff.ComponentType))
                        diff.ComponentType = oldDoc.ComponentType;
                }
                // Remove matched doc from newDocs.
                newDocs.Remove(newDoc);
            }
            else
            {
                // The document was removed.
                diffs.Add(new DiffResult
                {
                    Path = "",
                    ChangeType = "removed",
                    OldValue = oldDoc.RawText,
                    ComponentType = oldDoc.ComponentType
                });
            }
        }

        // Any remaining new documents are additions.
        foreach (var newDoc in newDocs)
        {
            diffs.Add(new DiffResult
            {
                Path = "",
                ChangeType = "added",
                NewValue = newDoc.RawText,
                ComponentType = newDoc.ComponentType
            });
        }

        // Post-process diff results and replace any GUIDs with asset names.
        foreach (var diff in diffs)
        {
            diff.OldValue = ReplaceGuidsWithAssetNames(diff.OldValue);
            diff.NewValue = ReplaceGuidsWithAssetNames(diff.NewValue);
        }

        // Post-process Remove diff entries coming from GameObject documents that only represent the m_Component
        // since these will be displayed as later diff entries
        diffs.RemoveAll(diff =>
            string.Equals(diff.ComponentType, "GameObject", StringComparison.OrdinalIgnoreCase) &&
            diff.Path.Contains("/m_Component", StringComparison.OrdinalIgnoreCase));

        return diffs;
    }

    private static void DiffYamlNodes(YamlNode oldNode, YamlNode newNode, string path, List<DiffResult> diffs)
    {
        // One of the nodes is missing.
        if (oldNode == null && newNode != null)
        {
            diffs.Add(new DiffResult { Path = path, ChangeType = "added", NewValue = newNode.ToString() });
            return;
        }
        if (oldNode != null && newNode == null)
        {
            diffs.Add(new DiffResult { Path = path, ChangeType = "removed", OldValue = oldNode.ToString() });
            return;
        }
        // If node types differ, mark as modified.
        if (oldNode.GetType() != newNode.GetType())
        {
            diffs.Add(new DiffResult { Path = path, ChangeType = "modified", OldValue = oldNode.ToString(), NewValue = newNode.ToString() });
            return;
        }
        // Handle scalar nodes.
        if (oldNode is YamlDotNet.RepresentationModel.YamlScalarNode scalarOld && newNode is YamlDotNet.RepresentationModel.YamlScalarNode scalarNew)
        {
            // Normalize numeric values if needed.
            if (scalarOld.Value != scalarNew.Value)
            {
                diffs.Add(new DiffResult { Path = path, ChangeType = "modified", OldValue = scalarOld.Value, NewValue = scalarNew.Value });
            }
            return;
        }
        // Handle mapping nodes.
        if (oldNode is YamlDotNet.RepresentationModel.YamlMappingNode mapOld && newNode is YamlDotNet.RepresentationModel.YamlMappingNode mapNew)
        {
            foreach (var entry in mapOld.Children)
            {
                string key = entry.Key.ToString();
                mapNew.Children.TryGetValue(entry.Key, out YamlNode newValue);
                DiffYamlNodes(entry.Value, newValue, path + "/" + key, diffs);
            }
            foreach (var entry in mapNew.Children)
            {
                if (!mapOld.Children.ContainsKey(entry.Key))
                {
                    string key = entry.Key.ToString();
                    DiffYamlNodes(null, entry.Value, path + "/" + key, diffs);
                }
            }
            return;
        }
        // Handle sequence nodes.
        if (oldNode is YamlDotNet.RepresentationModel.YamlSequenceNode seqOld && newNode is YamlDotNet.RepresentationModel.YamlSequenceNode seqNew)
        {
            int count = Math.Max(seqOld.Children.Count, seqNew.Children.Count);
            for (int i = 0; i < count; i++)
            {
                YamlNode childOld = i < seqOld.Children.Count ? seqOld.Children[i] : null;
                YamlNode childNew = i < seqNew.Children.Count ? seqNew.Children[i] : null;
                DiffYamlNodes(childOld, childNew, path + $"[{i}]", diffs);
            }
            return;
        }
    }

    // Splits a raw YAML text into individual documents with header info.
    private static List<YamlDocumentInfo> ExtractYamlDocuments(string yamlText)
    {
        List<YamlDocumentInfo> docs = new List<YamlDocumentInfo>();
        // Split by the document delimiter.
        string[] lines = yamlText.Split(new[] { "\n", "\r\n" }, StringSplitOptions.None);
        List<string> currentDocLines = new List<string>();
        foreach (string line in lines)
        {
            if (line.Trim().StartsWith("---"))
            {
                // If we have accumulated lines, process the previous document.
                if (currentDocLines.Count > 0)
                {
                    string docText = string.Join("\n", currentDocLines);
                    var info = ParseYamlDocumentInfo(docText);
                    if (info != null)
                        docs.Add(info);
                    currentDocLines.Clear();
                }
            }
            currentDocLines.Add(line);
        }
        // Process the last document.
        if (currentDocLines.Count > 0)
        {
            string docText = string.Join("\n", currentDocLines);
            var info = ParseYamlDocumentInfo(docText);
            if (info != null)
                docs.Add(info);
        }
        return docs;
    }

    private static YamlDocumentInfo ParseYamlDocumentInfo(string docText)
    {
        if (string.IsNullOrWhiteSpace(docText))
            return null;

        // Split into lines.
        var lines = docText.Split(new[] { "\n", "\r\n" }, StringSplitOptions.None);

        // Remove directive lines (lines starting with '%').
        List<string> filteredLines = new List<string>();
        foreach (var line in lines)
        {
            if (!line.TrimStart().StartsWith("%"))
                filteredLines.Add(line);
        }

        // Extract header line: the first line that starts with '---'.
        string headerLine = "";
        int headerIndex = -1;
        for (int i = 0; i < filteredLines.Count; i++)
        {
            if (filteredLines[i].TrimStart().StartsWith("---"))
            {
                headerLine = filteredLines[i].Trim();
                headerIndex = i;
                break;
            }
        }

        // Remove the header line from the text for parsing.
        if (headerIndex != -1)
        {
            filteredLines.RemoveAt(headerIndex);
        }
        // Reassemble the remaining lines into the text for parsing.
        string textForParsing = string.Join("\n", filteredLines).Trim();

        if (string.IsNullOrEmpty(textForParsing))
            return null;

        // Extract header info (classId, fileId) from the header line.
        string classId = YamlHeaderParser.ExtractClassId(headerLine);
        string fileId = YamlHeaderParser.ExtractFileId(headerLine);
        // Extract the component type from the remaining text.
        string componentType = YamlHeaderParser.ExtractComponentType(textForParsing);

        // load the YAML using the "clean" text.
        YamlDotNet.RepresentationModel.YamlStream stream = new YamlDotNet.RepresentationModel.YamlStream();
        using (StringReader sr = new StringReader(textForParsing))
        {
            try
            {
                stream.Load(sr);
            }
            catch (Exception e)
            {
                Debug.LogError("Error loading YAML document: " + e.Message);
                return null;
            }
        }
        if (stream.Documents.Count == 0)
            return null;

        var info =  new YamlDocumentInfo
        {
            HeaderLine = headerLine,
            RawText = docText,  // can keep original text
            ClassId = classId,
            FileId = fileId,
            ComponentType = componentType,
            Document = stream.Documents[0]
        };

        // Handle monobehaviour scripts
        if (string.Equals(info.ComponentType, "MonoBehaviour", StringComparison.OrdinalIgnoreCase))
        {
            // First, try to extract m_Name using regex.
            // This pattern looks for a line like: m_Name: SomeScriptName
            Regex nameRegex = new Regex(@"m_Name:\s(\S+)", RegexOptions.IgnoreCase);
            Match nameMatch = nameRegex.Match(info.RawText);
            if (nameMatch.Success && !string.IsNullOrEmpty(nameMatch.Groups[1].Value) && nameMatch.Groups[1].Value != "0")
            {
                // Use the m_Name value if it's not empty or "0"
                info.ComponentType = nameMatch.Groups[1].Value;
            }
            else
            {
                // Otherwise, look for the GUID under m_Script.
                // This pattern matches something like: m_Script: {fileID: XXXX, guid: 72a8275e761... , type: 3}
                Regex guidRegex = new Regex(@"m_Script:\s*\{[^}]*guid:\s*([0-9A-Fa-f]{32})", RegexOptions.IgnoreCase);
                Match guidMatch = guidRegex.Match(info.RawText);
                if (guidMatch.Success)
                {
                    string guid = guidMatch.Groups[1].Value;
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        UnityEngine.Object scriptAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                        if (scriptAsset != null)
                        {
                            info.ComponentType = scriptAsset.name;
                        }
                    }
                }
            }
        }

        return info;
    }

    /// <summary>
    /// Search for GUIDs in a string and replace them with asset names
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static string ReplaceGuidsWithAssetNames(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Regex to match a 32 hex digit string as a whole word
        // (assuming this is always the case for Unity GUIDs for now)
        Regex regex = new Regex(@"\b[0-9A-Fa-f]{32}\b");

        return regex.Replace(input, match =>
        {
            string guid = match.Value;
            // Convert GUID to asset path:
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(assetPath))
            {
                var assetObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                if (assetObj != null)
                {
                    // Replace the raw GUID with the asset name plus GUID in parentheses
                    return $"{assetObj.name} ({guid})";
                }
            }
            // If the GUID isnt recognized as a real asset, just keep the original text
            return guid;
        });
    }
}
#endif
