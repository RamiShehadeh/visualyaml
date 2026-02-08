using System;
using System.Collections.Generic;

namespace AssetDiff
{
    [Serializable]
    internal class AssetDiffEntry
    {
        public string AssetPath;
        public string ChangeType;    // Added | Modified | Deleted | Renamed | Manual
        public List<DiffResult> DiffResults = new List<DiffResult>();
        public PrefabGraph OldGraph;
        public PrefabGraph NewGraph;
    }

    [Serializable]
    internal class DiffResult
    {
        public string ComponentType;     // e.g., Transform, MeshRenderer, MyScript
        public string FieldPath;         // YAML field path within the component
        public string HierarchyPath;     // Full GO hierarchy + component label
        public string ChangeType;        // added | removed | modified
        public string OldValue;
        public string NewValue;
        public long? DocFileId;
        public int? ClassId;
        public string OwnerGameObject;
        public bool IsDocumentLevel;     // true if this is a whole-document add/remove (not a field diff)
    }

    [Serializable]
    internal class PrefabGraph
    {
        public List<PrefabNode> Roots = new List<PrefabNode>();
        public Dictionary<long, PrefabNode> TransformToNode = new Dictionary<long, PrefabNode>();
        public Dictionary<long, GameObjectInfo> GameObjects = new Dictionary<long, GameObjectInfo>();
        public Dictionary<long, ComponentInfo> Components = new Dictionary<long, ComponentInfo>();
        public Dictionary<long, long> ComponentToGameObject = new Dictionary<long, long>();
        // Indexed parent lookup: child transform fileId -> parent transform fileId
        public Dictionary<long, long> ChildToParentTransform = new Dictionary<long, long>();
        // GO fileId -> PrefabNode (fast lookup by GO instead of scanning TransformToNode)
        public Dictionary<long, PrefabNode> GameObjectToNode = new Dictionary<long, PrefabNode>();
    }

    [Serializable]
    internal class PrefabNode
    {
        public string Name;
        public long GameObjectFileId;
        public long TransformFileId;
        public List<long> ComponentFileIds = new List<long>();
        public List<PrefabNode> Children = new List<PrefabNode>();

        public override string ToString() => Name;
    }

    [Serializable]
    internal class GameObjectInfo
    {
        public long FileId;
        public string Name;
        public List<long> ComponentIds = new List<long>();
    }

    [Serializable]
    internal class ComponentInfo
    {
        public long FileId;
        public int ClassId;
        public string TypeName;
        public long OwnerGameObjectFileId;
    }
}
