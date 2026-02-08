using System.Collections.Generic;

namespace AssetDiff
{
    internal static class UnityClassIds
    {
        private static readonly Dictionary<int, string> Map = new Dictionary<int, string>
        {
            { 1, "GameObject" },
            { 2, "Component" },
            { 4, "Transform" },
            { 8, "Behaviour" },
            { 12, "ParticleAnimator" },
            { 15, "EllipsoidParticleEmitter" },
            { 20, "Camera" },
            { 21, "Material" },
            { 23, "MeshRenderer" },
            { 25, "Renderer" },
            { 27, "Texture" },
            { 28, "Texture2D" },
            { 29, "OcclusionCullingSettings" },
            { 30, "GraphicsSettings" },
            { 33, "MeshFilter" },
            { 43, "Mesh" },
            { 48, "Shader" },
            { 49, "TextAsset" },
            { 50, "Rigidbody2D" },
            { 54, "Rigidbody" },
            { 55, "PhysicsManager" },
            { 56, "Collider" },
            { 57, "Joint" },
            { 58, "CircleCollider2D" },
            { 59, "HingeJoint" },
            { 60, "PolygonCollider2D" },
            { 61, "BoxCollider2D" },
            { 62, "PhysicsMaterial2D" },
            { 64, "MeshCollider" },
            { 65, "BoxCollider" },
            { 66, "CompositeCollider2D" },
            { 68, "EdgeCollider2D" },
            { 70, "CapsuleCollider2D" },
            { 72, "ComputeShader" },
            { 74, "AnimationClip" },
            { 78, "AudioListener" },
            { 81, "AudioSource" },  // Note: some docs say 82
            { 82, "AudioSource" },
            { 83, "AudioClip" },
            { 84, "RenderTexture" },
            { 87, "MeshParticleEmitter" },
            { 89, "Cubemap" },
            { 91, "AnimatorOverrideController" },
            { 95, "Animator" },
            { 96, "TrailRenderer" },
            { 98, "BillboardRenderer" },
            { 102, "SortingGroup" },
            { 104, "RenderSettings" },
            { 108, "Light" },
            { 109, "CGProgram" },
            { 111, "Animation" },
            { 114, "MonoBehaviour" },
            { 115, "MonoScript" },
            { 117, "Texture3D" },
            { 119, "Projector" },
            { 120, "LineRenderer" },
            { 121, "Flare" },
            { 128, "Font" },
            { 129, "PlayerSettings" },
            { 131, "GUITexture" },
            { 132, "GUIText" },
            { 134, "PhysicMaterial" },
            { 135, "SphereCollider" },
            { 136, "CapsuleCollider" },
            { 137, "SkinnedMeshRenderer" },
            { 141, "BuildSettings" },
            { 142, "AssetBundle" },
            { 143, "CharacterController" },
            { 144, "CharacterJoint" },
            { 145, "SpringJoint" },
            { 146, "WheelCollider" },
            { 147, "ResourceManager" },
            { 150, "PreloadData" },
            { 152, "MovieTexture" },
            { 153, "ConfigurableJoint" },
            { 154, "TerrainCollider" },
            { 156, "TerrainData" },
            { 157, "LightmapSettings" },
            { 158, "WebCamTexture" },
            { 162, "EditorSettings" },
            { 180, "OcclusionArea" },
            { 181, "Tree" },
            { 183, "NavMeshAgent" },
            { 184, "NavMeshSettings" },
            { 192, "OcclusionPortal" },
            { 195, "NavMeshObstacle" },
            { 196, "ParticleSystem" },
            { 198, "ParticleSystemRenderer" },
            { 199, "ShaderVariantCollection" },
            { 205, "LODGroup" },
            { 206, "BlendTree" },
            { 207, "Motion" },
            { 208, "NavMeshAreas" },
            { 210, "SpriteAtlas" },
            { 212, "SpriteRenderer" },
            { 213, "Sprite" },
            { 218, "Terrain" },
            { 220, "LightProbeGroup" },
            { 221, "AnimatorController" },
            { 222, "CanvasRenderer" },
            { 223, "Canvas" },
            { 224, "RectTransform" },
            { 225, "CanvasGroup" },
            { 226, "BillboardAsset" },
            { 228, "WindZone" },
            { 229, "NavMeshData" },
            { 230, "AudioMixer" },
            { 231, "AudioMixerController" },
            { 232, "AudioMixerGroupController" },
            { 233, "AudioMixerEffectController" },
            { 234, "AudioMixerSnapshotController" },
            { 236, "ReflectionProbe" },
            { 237, "LightProbeProxyVolume" },
            { 240, "LightProbes" },
            { 258, "VideoPlayer" },
            { 271, "VisualEffect" },
            { 290, "VisualEffectAsset" },
            { 319, "AimConstraint" },
            { 320, "PositionConstraint" },
            { 321, "RotationConstraint" },
            { 322, "ScaleConstraint" },
            { 328, "VideoClip" },
            { 329, "ParentConstraint" },
            { 330, "LookAtConstraint" },
            { 1001, "PrefabInstance" },
            { 1002, "EditorExtensionImpl" },
            { 1003, "AssetImporter" },
        };

        public static string GetTypeName(int classId)
        {
            if (Map.TryGetValue(classId, out string name))
                return name;
            return "UnknownType(" + classId + ")";
        }

        public static bool IsTransformType(int classId)
        {
            return classId == 4 || classId == 224;
        }

        public static bool IsGameObject(int classId)
        {
            return classId == 1;
        }

        public static bool IsPrefabInstance(int classId)
        {
            return classId == 1001;
        }

        public static string GetTransformKey(int classId)
        {
            return classId == 224 ? "RectTransform" : "Transform";
        }
    }
}
