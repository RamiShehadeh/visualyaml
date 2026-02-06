# Visual YAML - Implementation Plan

## Executive Summary

Visual YAML is a Unity Editor tool that visualizes diffs of Unity YAML-serialized assets (prefabs, scenes, materials, ScriptableObjects). The current v0.0.2 implementation has a working prototype but suffers from critical gaps that make it unreliable for production use. This plan addresses every identified issue and lays out a phased approach to make the tool professional-grade, targeting **Unity 2022.3 LTS** as the primary platform.

---

## Current State Assessment

### What Works
- Basic YAML multi-document parsing via YamlDotNet
- Header extraction (`--- !u!{classId} &{fileId}`)
- Simple recursive field-level diffing of scalar/mapping/sequence nodes
- Git integration (fetch changed files, compare to commits, manual file pick)
- Hierarchical tree view UI with color-coded change badges
- MonoBehaviour script name resolution via GUID -> AssetDatabase
- Component re-identification when fileIDs change (CompKey matching)
- GUID prettification in diff values

### Critical Issues

| # | Issue | Severity | Impact |
|---|-------|----------|--------|
| 1 | **package.json targets `"unity": "6000.0"`** | Critical | Package won't install on 2022.3 LTS at all |
| 2 | **RectTransform (class 224) not handled** | Critical | All UI GameObjects are invisible in the hierarchy - their components appear as orphans |
| 3 | **PrefabInstance (!u!1001) not parsed** | Critical | Nested prefabs/prefab variants are completely ignored. Their modifications (overrides) are invisible |
| 4 | **`stripped` documents crash YamlDotNet** | Critical | Any prefab with nested prefab references will have stripped Transform/GO docs that fail to parse |
| 5 | **No class ID -> type name map** | High | Only knows types by top-level YAML key; unknown class IDs show raw number |
| 6 | **Assembly definition is misconfigured** | High | Name is `"ramis.Editor"` with no Editor-only platform restriction - could compile into builds |
| 7 | **Models in global namespace** | Medium | `AssetDiffEntry`, `DiffResult`, `PrefabGraph` etc. pollute global namespace, risk conflicts |
| 8 | **No automated tests** | High | No way to validate changes don't break existing behavior |
| 9 | **Index-based sequence diffing** | High | Inserting one element at the start of an array shows every subsequent element as "modified" |
| 10 | **O(N^2) hierarchy resolution** | Medium | `BuildPathToRoot` and orphan detection scan all transforms per lookup |
| 11 | **Git only compares HEAD~1 vs HEAD** | Medium | Can't compare working tree vs staged, or arbitrary commit ranges |
| 12 | **`#if UNITY_EDITOR` wrapping is redundant** | Low | Files are in Editor/ with Editor asmdef; the preprocessor guards are unnecessary |
| 13 | **Regex compiled per call** | Low | `TryResolveMonoScriptName` creates new Regex objects each invocation |
| 14 | **No error recovery for malformed YAML** | Medium | One bad document in a file causes entire file to fail silently |
| 15 | **m_Component format varies by Unity version** | High | Older: `- 4: {fileID: 8}`, Newer: `- component: {fileID: 8}` - only partially handled |

---

## Target Architecture

```
Editor/
  Core/
    UnityClassIds.cs            # Static class ID -> type name dictionary
    YamlPreprocessor.cs         # Sanitize Unity YAML for YamlDotNet (strip directives, handle stripped, dedup keys)
    YamlHeaderParser.cs         # Parse --- !u!{classId} &{fileId} [stripped]
    UnityYamlDocument.cs        # Parsed document model
    UnityYamlParser.cs          # Multi-document extraction + single-document parse
    PrefabGraph.cs              # Graph/node/component models
    PrefabGraphBuilder.cs       # Build hierarchy from documents (Transform + RectTransform)
    PrefabOverrideParser.cs     # Parse PrefabInstance m_Modifications into structured overrides
    DiffResult.cs               # Diff result model
    DiffEngine.cs               # Core diff algorithm with improved sequence handling
    TypeResolver.cs             # MonoBehaviour/script name resolution (moved from parser)
  Git/
    GitRunner.cs                # Process execution helper
    GitDiffProvider.cs          # Changed file detection, commit history, arbitrary range support
  UI/
    DiffTreeView.cs             # TreeView rendering
    Styles.cs                   # Shared colors, GUIStyles
  Windows/
    VisualYamlWindow.cs         # Main EditorWindow (renamed for clarity)
  VisualYAML.Editor.asmdef      # Proper assembly definition
Tests/
  Editor/
    YamlHeaderParserTests.cs
    UnityYamlParserTests.cs
    DiffEngineTests.cs
    PrefabGraphBuilderTests.cs
    VisualYAML.Tests.Editor.asmdef
```

---

## Implementation Phases

### Phase 0: Foundation & Compatibility Fix
**Goal:** Make the package installable on Unity 2022.3 LTS and fix the assembly definition.

#### Tasks:
1. **Fix `package.json`**
   - Change `"unity": "6000.0"` to `"unity": "2022.3"`
   - Update `"version"` to `"0.1.0"` (semver: new minor = breaking changes expected)
   - Add `"unityRelease": "0f1"` for minimum patch version

2. **Fix assembly definition** (`ramis.visualyaml.Editor.asmdef`)
   - Rename to `VisualYAML.Editor.asmdef`
   - Set `"name": "VisualYAML.Editor"`
   - Add `"includePlatforms": ["Editor"]`
   - Add `"references": []` (empty, uses implicit UnityEditor/UnityEngine)
   - Add `"autoReferenced": true`
   - Add `"rootNamespace": "VisualYAML"`

3. **Move all public types into `VisualYAML` namespace**
   - Currently `Models.cs` classes are in global namespace
   - Other files use `YamlPrefabDiff` namespace
   - Unify everything under `VisualYAML`

4. **Remove redundant `#if UNITY_EDITOR`** from all files (Editor asmdef handles this)

5. **Verify YamlDotNet.dll compatibility**
   - Confirm the bundled DLL works with .NET Standard 2.1 (Unity 2022.3's default API compat level)
   - If not, source an appropriate build or switch to NuGet reference

**Acceptance:** Package installs cleanly in a fresh Unity 2022.3 LTS project via Package Manager "Add from disk."

---

### Phase 1: YAML Preprocessing & Robust Parsing
**Goal:** Handle all Unity YAML edge cases that currently cause parse failures.

#### Tasks:

1. **Create `YamlPreprocessor.cs`**
   The core problem is that Unity YAML is not standard YAML. Before passing to YamlDotNet, we must sanitize:

   ```csharp
   public static class YamlPreprocessor
   {
       // Input: raw Unity YAML file content
       // Output: list of (headerLine, sanitizedBodyText) per document
       public static List<(string header, string body)> SplitAndSanitize(string rawYaml);
   }
   ```

   Preprocessing steps per document:
   - **Strip `%YAML` and `%TAG` directives** (already done, but formalize)
   - **Remove `stripped` keyword from header** (capture it as a flag, remove from the line before passing to YamlDotNet)
   - **Handle duplicate keys** by appending a disambiguator suffix (e.g., `key__dup_2`)
   - **Normalize line endings** to `\n`

2. **Improve `YamlHeaderParser.cs`**
   - Add parsing of `stripped` flag from header line
   - Return a structured `DocumentHeader` record instead of out params:
     ```csharp
     public record struct DocumentHeader(int ClassId, long FileId, bool IsStripped);
     ```

3. **Create `UnityClassIds.cs`**
   - Static dictionary mapping all common class IDs to human-readable names:
     ```
     1 -> "GameObject"
     4 -> "Transform"
     20 -> "Camera"
     23 -> "MeshRenderer"
     33 -> "MeshFilter"
     65 -> "BoxCollider"
     82 -> "AudioSource"
     95 -> "Animator"
     108 -> "Light"
     114 -> "MonoBehaviour"
     224 -> "RectTransform"
     1001 -> "PrefabInstance"
     ... (50+ entries)
     ```
   - Fallback: return `"UnknownType({classId})"` for unmapped IDs
   - Method: `IsTransformType(int classId)` returns true for both 4 and 224

4. **Refactor `UnityYamlParser.cs`** (renamed from `UnityYamlParsing.cs`)
   - Use the new preprocessor pipeline
   - Handle stripped documents gracefully (create UnityYamlDocument with `IsStripped=true`, null Yaml)
   - Use class ID map for initial TypeName before MonoBehaviour resolution
   - Move MonoBehaviour resolution to a separate `TypeResolver.cs` so the parser doesn't depend on AssetDatabase (enables testing)
   - Better error handling: log which document failed (include fileId and classId in error message), continue parsing remaining documents

**Acceptance:** Can parse any `.prefab`, `.unity`, `.asset`, `.mat` file from a Unity 2022.3 project without exceptions, including files with nested prefabs, UI elements, and stripped references.

---

### Phase 2: RectTransform & PrefabInstance Support
**Goal:** Build correct hierarchies for UI objects and handle nested prefab references.

#### Tasks:

1. **Fix PrefabGraphBuilder for RectTransform**
   - Current code filters `docs.Where(x => x.TypeName == "Transform")` - this misses RectTransform entirely
   - Change to: `docs.Where(x => UnityClassIds.IsTransformType(x.ClassId))`
   - When looking up the top-level YAML key for RectTransform, use `"RectTransform"` (class 224) not `"Transform"`
   - The `m_Father`, `m_Children`, `m_GameObject` fields work identically for RectTransform

2. **Handle stripped documents in graph building**
   - Stripped transforms should create placeholder PrefabNodes
   - They reference objects in other prefabs via `m_CorrespondingSourceObject`
   - At minimum: create a node with the name "(PrefabInstance)" so the hierarchy isn't broken
   - Stretch: resolve the source prefab name if the GUID is available

3. **Create `PrefabOverrideParser.cs`**
   - Parse `PrefabInstance` documents (class 1001)
   - Extract `m_Modifications` list into structured `PropertyOverride` objects:
     ```csharp
     public class PropertyOverride
     {
         public long TargetFileId;
         public string TargetGuid;
         public string PropertyPath;  // e.g. "m_LocalPosition.x"
         public string Value;
         public string ObjectReference;
     }
     ```
   - Extract `m_RemovedComponents`, `m_RemovedGameObjects`, `m_AddedGameObjects`, `m_AddedComponents`
   - Extract `m_SourcePrefab` GUID for display

4. **Integrate PrefabInstance diffs into DiffEngine**
   - When diffing two PrefabInstance documents, compare their `m_Modifications` lists semantically
   - Match modifications by `(targetFileId, propertyPath)` pair
   - Show added/removed/changed overrides clearly
   - Display the source prefab name (resolved from GUID) as context

5. **Add parent-child index to PrefabGraph**
   - Currently `BuildPathToRoot` does O(N) linear scan to find parent
   - Add `Dictionary<long, long> ChildToParentTransform` built during construction
   - This makes hierarchy path resolution O(depth) instead of O(N * depth)

**Acceptance:** A prefab containing Canvas > Panel > Button (all RectTransform) shows correct hierarchy. A scene with nested prefab instances shows the instance with its overrides.

---

### Phase 3: Diff Engine Improvements
**Goal:** Reduce noise, improve accuracy of change detection.

#### Tasks:

1. **Improve sequence/array diffing**
   Current approach: compare arrays index-by-index. Problem: insert at index 0 shows all subsequent elements as modified.

   Improvement strategy — use key-based matching for known array types:
   - `m_Component` arrays: match by `fileID` inside each entry
   - `m_Children` arrays: match by `fileID`
   - `m_Modifications` (PrefabInstance): match by `(target.fileID, propertyPath)`
   - Generic arrays: fall back to index-based but add heuristic LCS (Longest Common Subsequence) for small arrays (< 100 elements)

2. **Filter noise from insignificant changes**
   - `m_ObjectHideFlags: 0` changes (almost always noise)
   - `m_CorrespondingSourceObject` / `m_PrefabInstance` / `m_PrefabAsset` reference changes (internal Unity bookkeeping)
   - `serializedVersion` changes (Unity version upgrade artifact)
   - Already filtering `m_Component` changes on GameObjects (good)
   - Make the filter list configurable (list of field path patterns to ignore)

3. **Improve CompKey re-identification**
   - Current CompKey excludes Transform/GameObject, but these can also have fileID churn
   - For Transforms: key by `owner GO name + child index` as fallback
   - For GameObjects: key by `m_Name` + position in hierarchy

4. **Handle cross-version YAML differences**
   - Some fields are added in newer Unity versions (e.g., `m_ConstrainProportionsScale` added in 2022+)
   - Fields present in new but not old should be flagged as "added (version upgrade)" rather than a real diff when the value is the default
   - Maintain a small list of known default values for version-added fields

5. **Improve value display**
   - For reference fields `{fileID: X, guid: Y, type: Z}`: resolve and show `"AssetName (guid)"`
   - For Vector3/Quaternion inline maps: display as `(x, y, z)` / `(x, y, z, w)` instead of raw YAML
   - For color fields: display as `RGBA(r, g, b, a)` with a color swatch in the UI
   - Round floats to reasonable precision (6 decimal places) to avoid IEEE 754 noise

**Acceptance:** Inserting a component mid-list doesn't flag every subsequent component as changed. Version upgrade artifacts are clearly labeled or filterable.

---

### Phase 4: Git Integration Improvements
**Goal:** Support real-world Git workflows beyond "last commit."

#### Tasks:

1. **Refactor Git code into dedicated classes**
   - `GitRunner.cs`: Process execution with timeout, proper stream reading (avoid deadlocks), error classification
   - `GitDiffProvider.cs`: All diff/log/status operations

2. **Support multiple comparison modes**
   - Working tree vs HEAD (unstaged changes)
   - Staged vs HEAD (what would be committed)
   - HEAD vs HEAD~N (last N commits)
   - Arbitrary commit A vs commit B
   - Branch A vs Branch B
   - Single commit (show what that commit changed)

3. **Improve commit picker UI**
   - Show commit list in a proper scrollable panel instead of GenericMenu (which has size limits)
   - Show commit hash, message, author, date
   - Allow selecting two commits for range comparison
   - Add a text field for pasting arbitrary commit hashes

4. **Handle renamed/moved files**
   - `git diff -M` flag for rename detection
   - When file is renamed, show old path -> new path and still diff content correctly
   - `git log --follow` for history across renames (already done)

5. **Handle files outside Assets/**
   - Packages/ folder files (for package development)
   - Allow configuring which paths to scan

**Acceptance:** User can compare any two commits, see working tree changes, and track files across renames.

---

### Phase 5: UI Polish
**Goal:** Make the UI professional and usable for daily workflow.

#### Tasks:

1. **Resizable split panel**
   - Replace fixed 320px left panel with draggable splitter
   - Remember split position via EditorPrefs

2. **Search and filter**
   - Text filter: type to filter changes by GameObject name, component type, or field path
   - Change type filter: toggles for added/removed/modified
   - Scope filter: show only changes in selected GameObject subtree

3. **Expand/collapse controls**
   - "Expand All" / "Collapse All" buttons
   - Expand to specific depth (show GameObjects only, show components, show all fields)
   - Remember expansion state per asset

4. **Detail panel for selected field**
   - Click a field change row to see full old/new values in a scrollable text area
   - Side-by-side comparison view for large values
   - Copy button for old/new values

5. **Better asset list (left panel)**
   - Show file icon (prefab/scene/material icon from EditorGUIUtility)
   - Show change count badge per asset
   - Sort by: name, change count, change type
   - Group by folder path

6. **Keyboard navigation**
   - Arrow keys to navigate changes
   - Enter to expand/collapse
   - Ctrl+F to focus search

7. **Context menu on diff rows**
   - "Copy field path"
   - "Copy old value" / "Copy new value"
   - "Select GameObject in scene" (if the scene is loaded)
   - "Ping asset" (for GUID references)

8. **Fix menu path**
   - README says `Window > YAML Prefab Diff` but code says `Tools/YAML Diff Tool`
   - Standardize to `Tools/Visual YAML/Diff Tool`

**Acceptance:** The UI feels polished and comparable to commercial Unity Editor extensions.

---

### Phase 6: Testing
**Goal:** Establish a test suite that validates core parsing and diffing logic.

#### Tasks:

1. **Create test assembly**
   - `Tests/Editor/VisualYAML.Tests.Editor.asmdef`
   - Reference `VisualYAML.Editor` assembly
   - Use Unity Test Framework (NUnit)

2. **YAML parser tests**
   - Test header parsing for all formats: `--- !u!114 &-1234`, `--- !u!4 &5678 stripped`, `--- 4 &5678`
   - Test document extraction from multi-document files
   - Test handling of malformed YAML (graceful degradation)
   - Test with real prefab YAML snippets for Transform, RectTransform, MonoBehaviour, PrefabInstance

3. **Graph builder tests**
   - Test simple hierarchy: Root > Child > Grandchild
   - Test UI hierarchy with RectTransform
   - Test stripped document handling
   - Test orphan nodes promoted to roots

4. **Diff engine tests**
   - Test no changes (identical files)
   - Test field modification
   - Test component added/removed
   - Test GameObject added/removed
   - Test fileID churn (same component, different ID)
   - Test sequence element insertion
   - Test PrefabInstance modification diffs

5. **Test fixtures**
   - Create a set of `.prefab` / `.unity` test fixture files
   - Store in `Tests/Editor/Fixtures/`
   - Cover: simple prefab, UI prefab, nested prefab, prefab variant, scene file

**Acceptance:** 80%+ code coverage on Core/ classes. All tests pass in Unity 2022.3.

---

## Implementation Order & Dependencies

```
Phase 0 (Foundation)          ← START HERE
    │
    ▼
Phase 1 (Robust Parsing)     ← Depends on Phase 0
    │
    ├──► Phase 2 (RectTransform & PrefabInstance)  ← Depends on Phase 1
    │
    └──► Phase 6 (Testing)   ← Can start in parallel with Phase 2
         │
         ▼
Phase 3 (Diff Engine)        ← Depends on Phase 2; tests validate changes
    │
    ▼
Phase 4 (Git Improvements)   ← Independent of Phases 2-3, but do after for stability
    │
    ▼
Phase 5 (UI Polish)          ← Last; depends on all other phases being stable
```

**Recommended approach:** Implement Phases 0-2 as a single PR to get a correct, working tool. Then iterate on Phases 3-6 as separate PRs.

---

## Version-Specific Considerations

### Unity 2022.3 LTS (Primary Target)
- .NET Standard 2.1 API compatibility level (default)
- `m_Component` array uses `component: {fileID: X}` format (not `4: {fileID: X}`)
- `m_ConstrainProportionsScale` field exists on Transform
- `serializedVersion` may differ from older versions
- IMGUI TreeView API is stable and well-documented

### Unity 2021.3 LTS (Secondary)
- Same .NET Standard 2.1 support
- Mostly compatible YAML format
- Minor differences in some serialized fields
- Consider supporting if low effort (same asmdef, same APIs)

### Unity 6000.x (Future)
- Introduces UI Toolkit for Editor UI (could migrate from IMGUI in future)
- YAML format is compatible with 2022.3 for our purposes
- May deprecate some IMGUI APIs in the long term

### Handling Version Differences
- The tool operates on YAML text, not on live Unity objects, so most version differences are in field names/presence rather than API differences
- The class ID mapping is stable across all modern Unity versions
- The key risk is YamlDotNet compatibility — the bundled DLL must match the project's API compat level

---

## Risk Assessment

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| YamlDotNet can't handle some Unity YAML | High | Preprocessing layer sanitizes before parsing. Consider VYaml as alternative |
| Large scene files cause perf issues | Medium | Lazy loading, background parsing, progress bar |
| PrefabInstance diffs are too complex | Medium | Start with showing overrides as flat list, iterate on hierarchical display |
| Breaking changes in future Unity versions | Low | YAML format has been stable since 2018.4. Class IDs never change |
| User has non-standard Git setup | Low | Clear error messages, allow manual file selection as fallback |

---

## Success Criteria

The tool is "professional" when a developer can:
1. Open it in Unity 2022.3 without errors
2. Click "Fetch Changed" and see all modified prefabs/scenes in the current commit
3. Select a prefab and instantly understand *what* changed (which GameObjects, which components, which fields)
4. See correct hierarchies for UI prefabs (RectTransform)
5. See nested prefab override changes
6. Compare to any previous commit
7. Search/filter through changes
8. Trust that no changes are silently missed
