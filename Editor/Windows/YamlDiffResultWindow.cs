#if UNITY_EDITOR
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Window to display detailed diff results for one asset.
/// </summary>
public class YamlDiffResultWindow : EditorWindow
{
    private AssetDiffEntry assetDiffEntry;
    private Vector2 scrollPos;

    public static void ShowWindow(AssetDiffEntry entry)
    {
        YamlDiffResultWindow window = GetWindow<YamlDiffResultWindow>("Diff: " + entry.AssetPath);
        window.assetDiffEntry = entry;
    }

    private void OnGUI()
    {
        if (assetDiffEntry == null)
        {
            EditorGUILayout.LabelField("No diff data available.");
            return;
        }
        EditorGUILayout.LabelField("Asset: " + assetDiffEntry.AssetPath);
        EditorGUILayout.LabelField("Change Type: " + assetDiffEntry.ChangeType);
        EditorGUILayout.Space();
        GUIStyle wrapStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        if (assetDiffEntry.DiffResults != null)
        {
            foreach (var diff in assetDiffEntry.DiffResults)
            {
                DrawDiffEntry(diff, wrapStyle);
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawDiffEntry(DiffResult diff, GUIStyle wrapStyle)
    {
        Color originalColor = GUI.color;

        // Choose a color based on the change type
        switch (diff.ChangeType)
        {
            case "added":
                GUI.color = Color.green;
                break;
            case "removed":
                GUI.color = Color.red;
                break;
            case "modified":
                GUI.color = Color.yellow;
                break;
        }

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Path: " + diff.Path, wrapStyle);
        EditorGUILayout.LabelField("Change: " + diff.ChangeType, wrapStyle);
        if (!string.IsNullOrEmpty(diff.ComponentType))
            EditorGUILayout.LabelField("Component: " + diff.ComponentType, wrapStyle);

        // Restore original color for old/new values
        GUI.color = originalColor;

        // Draw clickable label for OldValue
        if (!string.IsNullOrEmpty(diff.OldValue))
        {
            DrawClickableAssetLink("Old", diff.OldValue, wrapStyle);
        }
        // Draw clickable label for NewValue
        if (!string.IsNullOrEmpty(diff.NewValue))
        {
            DrawClickableAssetLink("New", diff.NewValue, wrapStyle);
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
    }

    /// <summary>
    /// If the provided diff value matches the pattern "AssetName (GUID)", display it as a clickable link.
    /// Otherwise, display it as a normal label.
    /// </summary>
    /// <param name="fieldLabel">A label such as "Old" or "New"</param>
    /// <param name="diffValue">The diff text to display</param>
    /// <param name="wrapStyle"> GUISTyle for label</param>
    private void DrawClickableAssetLink(string fieldLabel, string diffValue, GUIStyle wrapStyle)
    {
        // Pattern: capture an asset name followed by a 32-digit hex GUID in parentheses.
        Regex regex = new Regex(@"^(.*?)\s*\(([0-9A-Fa-f]{32})\)$");
        var match = regex.Match(diffValue.Trim());
        if (match.Success)
        {
            string assetName = match.Groups[1].Value;
            string guid = match.Groups[2].Value;
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            UnityEngine.Object asset = null;
            if (!string.IsNullOrEmpty(assetPath))
            {
                asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            }

            string buttonText = $"{fieldLabel}: {assetName}";
            GUIStyle linkStyle = new GUIStyle(EditorStyles.linkLabel)
            {
                alignment = TextAnchor.MiddleLeft
            };

            // Reserve a rect for the button.
            Rect rect = GUILayoutUtility.GetRect(new GUIContent(buttonText), linkStyle);

            if (GUI.Button(rect, buttonText, linkStyle))
            {
                if (asset != null)
                {
                    EditorGUIUtility.PingObject(asset);
                }
            }
            // Change the cursor to a link pointer when hovering over this rect.
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        }
        else
        {
            EditorGUILayout.LabelField($"{fieldLabel}: {diffValue}", wrapStyle);
        }
    }
}
#endif
