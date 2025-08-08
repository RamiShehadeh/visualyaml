#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;


[Serializable]
public class AssetDiffEntry
{
    public string AssetPath;    // e.g. Assets/Prefabs/My.prefab or "Manual Diff: A vs B"
    public string ChangeType;   // Added | Modified | Deleted | Manual
    public List<DiffResult> DiffResults = new();
    public PrefabGraph OldGraph; // optional, when available
    public PrefabGraph NewGraph; // optional, when available
}

[Serializable]
public class DiffResult
{
    public string ComponentType;     // e.g. Transform, MeshRenderer, MyMono
    public string FieldPath;         // YAML field path within the component
    public string HierarchyPath;     // GameObject hierarchy + component label
    public string ChangeType;        // added | removed | modified
    public string OldValue;          // display text after GUID prettification
    public string NewValue;          // display text after GUID prettification
    public long? DocFileId;          // YAML document fileID owning this change
    public int? ClassId;             // YAML class id (u!xx) when known
    public string OwnerGameObject;   // Resolved GameObject name of the doc
}

[Serializable]
public class PrefabGraph
{
    public List<PrefabNode> Roots = new();
    public Dictionary<long, PrefabNode> TransformToNode = new();
    public Dictionary<long, GameObjectInfo> GameObjects = new();
    public Dictionary<long, ComponentInfo> Components = new();
    public Dictionary<long, long> ComponentToGameObject = new(); // comp fileId -> go fileId
}

[Serializable]
public class PrefabNode
{
    public string Name;
    public long GameObjectFileId;
    public long TransformFileId;
    public List<long> ComponentFileIds = new(); // excludes Transform
    public List<PrefabNode> Children = new();

    public override string ToString() => Name;
}

[Serializable]
public class GameObjectInfo
{
    public long FileId;
    public string Name;
    public List<long> ComponentIds = new(); // includes Transform id as well
}

[Serializable]
public class ComponentInfo
{
    public long FileId;
    public int ClassId; // Unity YAML class id (e.g. 114 = MonoBehaviour)
    public string TypeName; // e.g. Transform, MeshRenderer, MyMono
    public long OwnerGameObjectFileId; // back-reference
}


#endif