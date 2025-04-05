using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

[CustomEditor(typeof(GameObject))]
public class DiffInspectorHighlighter : Editor
{
    private Dictionary<string, bool> foldoutStates = new Dictionary<string, bool>();
    private readonly Color darkBackground = new Color(0.15f, 0.15f, 0.15f);
    private bool showDiffInfo = true;

    private void OnEnable()
    {
        // Load the saved state of the main foldout
        showDiffInfo = EditorPrefs.GetBool("DiffInfoFoldout", true);
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        GameObject go = (GameObject)target;
        string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);

        if (string.IsNullOrEmpty(assetPath)) return;

        var diffEntry = YamlDiffData.GetDiffEntry(assetPath);
        if (diffEntry?.DiffResults == null || diffEntry.DiffResults.Count == 0) return;

        EditorGUILayout.Space();

        // Main collapsible foldout for the entire Diff Info section
        showDiffInfo = EditorGUILayout.Foldout(
            showDiffInfo,
            "Diff Info",
            true,
            new GUIStyle(EditorStyles.foldoutHeader) { fontStyle = FontStyle.Bold, fontSize = 16}
        );

        // Save state whenever it changes
        if (GUI.changed)
            EditorPrefs.SetBool("DiffInfoFoldout", showDiffInfo);

        if (!showDiffInfo) return;

        var diffsByComponent = diffEntry.DiffResults
            .GroupBy(d => d.ComponentType ?? "Unknown")
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var comp in go.GetComponents<Component>())
        {
            string compType = comp.GetType().Name;
            bool hasDiff = diffsByComponent.ContainsKey(compType);

            if (!foldoutStates.ContainsKey(compType))
                foldoutStates[compType] = true;

            DrawFoldoutHeader(compType, hasDiff, diffsByComponent.GetValueOrDefault(compType));

            if (foldoutStates[compType])
            {
                EditorGUI.indentLevel++;
                if (hasDiff) DrawDiffEntries(diffsByComponent[compType]);
                else EditorGUILayout.LabelField("No changes detected.");
                EditorGUI.indentLevel--;
            }
        }
    }

    private void DrawFoldoutHeader(string compType, bool hasDiff, List<DiffResult> diffs)
    {
        EditorGUILayout.BeginHorizontal();

        // Color indicator
        if (hasDiff)
        {
            Color headerColor = GetAggregateColor(diffs);
            Rect colorRect = GUILayoutUtility.GetRect(10f, 16f, GUILayout.Width(10f));
            EditorGUI.DrawRect(colorRect, headerColor);
        }

        // Foldout
        foldoutStates[compType] = EditorGUILayout.Foldout(
            foldoutStates[compType],
            compType,
            true,
            new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold, fontSize = 14 }
        );

        EditorGUILayout.EndHorizontal();
    }

    private void DrawDiffEntries(List<DiffResult> diffs)
    {
        EditorGUILayout.BeginVertical();
        Rect bgRect = EditorGUILayout.BeginVertical();
        EditorGUI.DrawRect(bgRect, darkBackground);

        foreach (var diff in diffs)
        {
            GUIStyle labelStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = GetTextColor(diff.ChangeType) },
                richText = true
            };

            EditorGUILayout.LabelField($"• {diff.Path}: <b>{diff.ChangeType}</b>", labelStyle);
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.EndVertical();
    }

    private Color GetAggregateColor(List<DiffResult> diffs)
    {
        if (diffs.Any(d => d.ChangeType == "removed")) return new Color(0.8f, 0.2f, 0.2f);
        if (diffs.Any(d => d.ChangeType == "modified")) return new Color(0.9f, 0.7f, 0.2f);
        return diffs.Any(d => d.ChangeType == "added") ? new Color(0.2f, 0.7f, 0.2f) : Color.gray;
    }

    private Color GetTextColor(string changeType)
    {
        switch (changeType.ToLower())
        {
            case "added": return new Color(0.4f, 1f, 0.4f);
            case "modified": return new Color(0.8f, 0.5f, 0.1f); //return new Color(1f, 0.9f, 0.4f);
            case "removed": return new Color(1f, 0.4f, 0.4f);
            default: return Color.white;
        }
    }
}