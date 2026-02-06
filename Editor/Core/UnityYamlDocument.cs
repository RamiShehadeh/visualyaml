using YamlDotNet.RepresentationModel;

namespace VisualYAML
{
    internal class UnityYamlDocument
    {
        public int ClassId;                // !u!xx
        public long FileId;               // &nnn
        public string TypeName;           // Resolved type name (e.g., "Transform", "MyScript")
        public string RawText;            // Full original text (for display)
        public YamlDocument Yaml;         // Parsed YAML DOM (null for stripped docs)
        public long OwnerGameObjectFileId;// For components: the GO this belongs to
        public string ScriptGuid;         // MonoBehaviour m_Script.guid
        public bool IsStripped;           // True if header contains "stripped"
    }
}
