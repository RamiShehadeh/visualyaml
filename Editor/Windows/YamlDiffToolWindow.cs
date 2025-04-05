#if UNITY_EDITOR
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Main Editor Window that lists changed assets and offers file selection.
/// </summary>
public class YamlDiffToolWindow : EditorWindow
{
    private Vector2 scrollPos;
    private List<AssetDiffEntry> assetDiffEntries = new();

    [MenuItem("Window/YAML Diff Tool")]
    public static void ShowWindow()
    {
        GetWindow<YamlDiffToolWindow>("YAML Diff Tool");
    }

    private void OnGUI()
    {
        GUILayout.Label("YAML Diff Tool", EditorStyles.boldLabel);

        // Button to auto-fetch changed assets using Git diff.
        if (GUILayout.Button("Fetch Changed Assets (Git Diff)"))
        {
            FetchChangedAssets();
        }

        // Fallback: manually select two files to compare.
        if (GUILayout.Button("Manually Select Two Files for Diff"))
        {
            ManualFileSelection();
        }

        GUILayout.Space(10);

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        foreach (var entry in assetDiffEntries)
        {
            EditorGUILayout.BeginHorizontal();
            // Clicking the asset name will ping it and open a detailed diff window.
            if (GUILayout.Button(entry.AssetPath, GUILayout.Width(300)))
            {
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(entry.AssetPath);
                if (asset != null)
                {
                    EditorGUIUtility.PingObject(asset);
                    Selection.activeObject = asset;
                }
                YamlDiffResultWindow.ShowWindow(entry);
            }
            EditorGUILayout.LabelField(entry.ChangeType, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();
    }

    /// <summary>
    /// Uses Git command-line to list changed files (only YAML files, e.g. prefabs and assets).
    /// </summary>
    private void FetchChangedAssets()
    {
        assetDiffEntries.Clear();
        YamlDiffData.DiffEntries.Clear();
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = "diff --name-status HEAD~1 HEAD",
                WorkingDirectory = Application.dataPath.Replace("/Assets", ""),
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process proc = Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            // Parse each line (e.g. "M	Assets/MyPrefab.prefab")
            string[] lines = output.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 2)
                {
                    string changeCode = parts[0];
                    string filePath = parts[1].Trim();
                    if (filePath.EndsWith(".prefab") || filePath.EndsWith(".asset"))
                    {
                        AssetDiffEntry entry = new()
                        {
                            AssetPath = filePath,
                            ChangeType = (changeCode == "A") ? "Added" : (changeCode == "D") ? "Deleted" : "Modified"
                        };

                        // For modified files, obtain the diff between the previous commit and current.
                        if (changeCode == "M")
                        {
                            string fullPath = Path.Combine(Application.dataPath.Replace("/Assets", ""), filePath);
                            string currentContent = File.ReadAllText(fullPath);
                            string previousContent = GetFileContentFromGit(filePath, "HEAD~1");
                            entry.DiffResults = YamlDiffer.DiffYaml(previousContent, currentContent);
                        }
                        assetDiffEntries.Add(entry);
                        YamlDiffData.DiffEntries[entry.AssetPath] = entry;
                    }
                }
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("Error fetching git diff: " + e.Message);
        }
    }

    /// <summary>
    /// Opens two file panels so the user can manually select files to compare.
    /// </summary>
    private void ManualFileSelection()
    {
        string fileA = EditorUtility.OpenFilePanel("Select First YAML File", Application.dataPath, "prefab,asset,yaml");
        string fileB = EditorUtility.OpenFilePanel("Select Second YAML File", Application.dataPath, "prefab,asset,yaml");
        if (!string.IsNullOrEmpty(fileA) && !string.IsNullOrEmpty(fileB))
        {
            AssetDiffEntry entry = new ()
            {
                AssetPath = "Manual Diff: " + Path.GetFileName(fileA) + " vs " + Path.GetFileName(fileB),
                ChangeType = "Manual"
            };
            string contentA = File.ReadAllText(fileA);
            string contentB = File.ReadAllText(fileB);
            entry.DiffResults = YamlDiffer.DiffYaml(contentA, contentB);
            assetDiffEntries.Add(entry);
        }
    }

    /// <summary>
    /// Helper that runs a Git command to retrieve the file content from a specific commit.
    /// </summary>
    /// <param name="assetPath"></param>
    /// <param name="commitRef"></param>
    /// <returns></returns>
    private string GetFileContentFromGit(string assetPath, string commitRef)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = $"show {commitRef}:{assetPath}",
                WorkingDirectory = Application.dataPath.Replace("/Assets", ""),
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process proc = Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("Error getting file content from git: " + e.Message);
            return "";
        }
    }
}
#endif