# Visual YAML - Project Guide

## What This Is

A Unity Editor extension that visualizes diffs of Unity YAML-serialized assets (prefabs, scenes, materials, ScriptableObjects). Intended for teams using Git, where raw YAML diffs of `.prefab`/`.unity` files are unreadable.

## Target Platform

- **Primary:** Unity 2022.3 LTS
- **API Compatibility Level:** .NET Standard 2.1
- **Editor-only package** (no runtime code)

## Project Structure

```
Editor/
  Core/               Core parsing, diffing, and graph-building logic
    UnityClassIds.cs      Static class ID -> type name mapping (130+ types)
    YamlPreprocessor.cs   Split + sanitize Unity YAML for YamlDotNet
    YamlHeaderParser.cs   Parse "--- !u!{classId} &{fileId} [stripped]" headers
    UnityYamlDocument.cs  Parsed document model
    UnityYamlParser.cs    Multi-document extraction + per-document parsing
    TypeResolver.cs       MonoBehaviour script name resolution via AssetDatabase
    Models.cs             Data models (AssetDiffEntry, DiffResult, PrefabGraph, etc.)
    PrefabGraphBuilder.cs Build GO/Transform hierarchy (handles RectTransform + stripped)
    DiffEngine.cs         Recursive field-level diff with noise filtering
  Git/                Git integration layer
    GitRunner.cs          Process execution with timeout and async stream reading
    GitDiffProvider.cs    Changed file detection, commit history, comparison modes
  UI/
    DiffTreeView.cs       IMGUI TreeView rendering with search and context menu
  Windows/
    VisualYamlWindow.cs   Main EditorWindow with resizable split panel
  VisualYAML.Editor.asmdef  Editor-only assembly definition
Plugins/
  YamlDotNet.dll        Third-party YAML parser (v11+, .NET Standard 2.1)
Tests/
  Editor/               Unit tests (NUnit via Unity Test Framework)
```

## Key Concepts

### Unity YAML Format
- Files start with `%YAML 1.1` and `%TAG !u! tag:unity3d.com,2011:` directives
- Each serialized object is a separate YAML document: `--- !u!{classId} &{fileId}`
- Class IDs: 1=GameObject, 4=Transform, 114=MonoBehaviour, 224=RectTransform, 1001=PrefabInstance
- Objects reference each other via `{fileID: N}` within a file
- Cross-file refs use `{fileID: N, guid: HEXSTRING, type: N}`
- `stripped` keyword after fileId marks placeholder objects for nested prefab refs

### Diff Pipeline
1. `YamlPreprocessor.SplitAndSanitize()` — split raw YAML, strip directives, handle `stripped`
2. `UnityYamlParser.ExtractDocuments()` — parse each document header + body via YamlDotNet
3. `TypeResolver.ResolveMonoBehaviourNames()` — resolve script GUIDs to class names
4. `PrefabGraphBuilder.Build()` — build hierarchy tree (Transform + RectTransform)
5. `DiffEngine.Diff()` — match documents by fileId/CompKey, recurse YAML nodes, filter noise
6. `DiffTreeView` — display results grouped by: GameObject > Component > Field

### Known YamlDotNet Limitations
- `stripped` keyword in document headers is invalid YAML — removed by preprocessor
- `%TAG` directive scope resets at `---` boundaries — directives stripped entirely
- Duplicate keys may cause exceptions — handled gracefully (document returned without Yaml)
- Large files (10k+ lines) can be slow

## Build & Test

Open in Unity 2022.3 LTS. No build step needed — it's an Editor extension.

- **Open tool:** Tools > Visual YAML > Diff Tool (in Unity menu bar)
- **Run tests:** Window > General > Test Runner > EditMode tab > Run All

## Coding Conventions

- Namespace: `VisualYAML` for all types
- All Editor code in `Editor/` folder with Editor-only assembly definition
- No `#if UNITY_EDITOR` guards needed (assembly definition handles platform restriction)
- Use C# 9 features available in Unity 2022.3 (pattern matching, target-typed new)
- Use `long` for fileIds (can be negative), `int` for classIds
- All types `internal` except the EditorWindow class (needs `public` for Unity serialization)

## Common Tasks

### Adding support for a new Unity class ID
1. Add the mapping in `Editor/Core/UnityClassIds.cs`
2. If it's a transform-like type, add to `IsTransformType()`
3. If it needs special diff handling, add case in `DiffEngine`

### Debugging YAML parse failures
1. Get the raw YAML text from the problematic file
2. Check if the document header has `stripped` — these are handled as placeholders
3. Check for duplicate keys in the YAML body
4. Check the Unity console for `[VisualYAML] YAML parse error` messages
5. Try parsing the sanitized text with YamlDotNet in isolation
