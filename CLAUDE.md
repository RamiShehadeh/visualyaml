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
  Internal/         Core parsing, diffing, and graph-building logic
    Models.cs           Data models (AssetDiffEntry, DiffResult, PrefabGraph, etc.)
    YamlHeaderParser.cs Parse "--- !u!{classId} &{fileId}" document headers
    UnityYamlParsing.cs Multi-document YAML extraction and per-document parsing
    DiffEngine.cs       Recursive field-level diff between two parsed YAML files
    PrefabGraphBuilder.cs  Build GameObject/Transform hierarchy tree from parsed docs
  UI/
    DiffTreeView.cs     IMGUI TreeView rendering of diff results
  Windows/
    YamlDiffToolWindow.cs  Main EditorWindow with Git integration
Plugins/
  YamlDotNet.dll      Third-party YAML parser (v11+, .NET Standard 2.1)
Tests/
  Editor/             Unit tests (NUnit via Unity Test Framework)
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
1. Split raw YAML into per-object documents
2. Parse each document header (classId, fileId) and body (via YamlDotNet)
3. Build PrefabGraph: hierarchy tree of GameObjects linked through Transform parent/child
4. Match documents between old/new by fileId (exact) then by stable key (re-identification)
5. Recursively diff matched YAML nodes field-by-field
6. Unmatched docs marked as added/removed
7. Results displayed in TreeView grouped by: GameObject > Component > Field

### Known YamlDotNet Limitations
- `stripped` keyword in document headers is invalid YAML — must be removed before parsing
- `%TAG` directive scope resets at `---` boundaries in YAML 1.2 mode — strip directives
- Duplicate keys cause exceptions — must deduplicate or handle
- Large files (10k+ lines) can be slow

## Build & Test

Open in Unity 2022.3 LTS. No build step needed — it's an Editor extension.

- **Open tool:** Tools > YAML Diff Tool (in Unity menu bar)
- **Run tests:** Window > General > Test Runner > EditMode tab > Run All

## Coding Conventions

- Namespace: `VisualYAML` (migration in progress from `YamlPrefabDiff` and global namespace)
- All Editor code in `Editor/` folder with Editor-only assembly definition
- No `#if UNITY_EDITOR` guards needed (assembly definition handles platform restriction)
- Use C# 9 features available in Unity 2022.3 (records, pattern matching, target-typed new)
- Prefer explicit types over `var` for public/internal API return types
- Use `long` for fileIds (can be negative), `int` for classIds

## Common Tasks

### Adding support for a new Unity class ID
1. Add the mapping in `UnityClassIds.cs` (when created, currently inferred from YAML top key)
2. If it's a transform-like type, add to `IsTransformType()` check
3. If it needs special diff handling, add case in `DiffEngine`

### Debugging YAML parse failures
1. Get the raw YAML text from the problematic file
2. Check if the document header has `stripped` (needs pre-strip)
3. Check for duplicate keys in the YAML body
4. Try parsing the sanitized text with YamlDotNet in isolation

## Implementation Plan

See `PLAN.md` for the full phased implementation plan covering:
- Phase 0: Foundation & compatibility fixes
- Phase 1: Robust YAML preprocessing
- Phase 2: RectTransform & PrefabInstance support
- Phase 3: Diff engine improvements
- Phase 4: Git integration improvements
- Phase 5: UI polish
- Phase 6: Testing
