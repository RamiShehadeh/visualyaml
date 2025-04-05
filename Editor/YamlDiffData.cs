using System.Collections.Generic;
using UnityEngine;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Static storage for diff data so the custom Inspector can look it up.
/// </summary>
public static class YamlDiffData
{
    // Maps an asset’s path (or a unique manual diff key) to its diff entry.
    public static Dictionary<string, AssetDiffEntry> DiffEntries = new();

    public static AssetDiffEntry GetDiffEntry(string assetPath)
    {
        if (DiffEntries.ContainsKey(assetPath))
            return DiffEntries[assetPath];
        return null;
    }
}

/// <summary>
/// Data structure representing one asset’s diff details.
/// </summary>
public class AssetDiffEntry
{
    public string AssetPath;            // e.g. "Assets/MyPrefab.prefab"
    public string ChangeType;           // "Added", "Modified", "Deleted", or "Manual"
    public List<DiffResult> DiffResults; // List of field differences (if applicable)
}

/// <summary>
/// Data structure representing one difference in the YAML tree.
/// </summary>
public class DiffResult
{
    public string Path;       // e.g. "/GameObject/m_Name" or "/Transform/m_LocalPosition"
    public string ChangeType; // "added", "removed", or "modified"
    public string OldValue;
    public string NewValue;
    public string ComponentType;
}

public class YamlDocumentInfo
{
    public string HeaderLine;       // e.g. "--- !u!65 &4098701836555577983"
    public string RawText;          // The entire document text.
    public string ComponentType;    // e.g. "BoxCollider"
    public string ClassId;          // e.g. "65"
    public string FileId;           // e.g. "4098701836555577983"
    public YamlDocument Document;   // Parsed YAML document.
}