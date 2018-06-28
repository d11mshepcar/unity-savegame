using OdinSerializer;
using OdinSerializer.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;

interface ISaveable
{
    bool Loaded { get; set; }
    void OnBeforeSave();
    void OnAfterLoad();
}
// Any classes derived from SaveableMonoBehaviour will be stored in the save game
public abstract class SaveableMonoBehaviour : MonoBehaviour, ISaveable
{
    public bool Loaded { get; set; }
    public virtual void OnBeforeSave() { }
    public virtual void OnAfterLoad() { }
}

public abstract class SaveableSerializedMonoBehaviour : SerializedMonoBehaviour, ISaveable
{
    public bool Loaded { get; set; }
    public virtual void OnBeforeSave() { }
    public virtual void OnAfterLoad() { }
}

// Apply the SaveGameField attribute to any fields in a SaveableMonoBehaviour that you want to store in the save game
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class SaveGameField : Attribute
{
}

// Add a SaveGame component into a scene to support loading and saving of SaveableMonoBehaviour classes
public class SaveGame : MonoBehaviour
#if UNITY_EDITOR
    , ISerializationCallbackReceiver
#endif
{
    public enum Format
    {
        CompressedBinary,
        Binary,
        JSON,
    }
    // List of prefabs that can be recreated when loading a save game
    [SerializeField] List<SaveGameObject> SaveablePrefabs;

    // If RequireSaveGameAttributeEverywhere is set then nested types also require 
    // saveable fields to be flagged with the SaveGameField attribute. If not set then
    // standard unity serialization rules apply.
    [SerializeField] bool RequireSaveGameAttributeEverywhere = false;

#if UNITY_EDITOR
    public void AddSaveablePrefab(SaveGameObject prefab)
    {
        if (!SaveablePrefabs.Contains(prefab))
            SaveablePrefabs.Add(prefab);
    }

    void ISerializationCallbackReceiver.OnBeforeSerialize()
    {
        if (UnitySerializationUtility.ForceEditorModeSerialization)
        {
            // save the scene to ensure all supported object types are registered for AOT builds
            Save(Stream.Null);
        }
    }
    void ISerializationCallbackReceiver.OnAfterDeserialize() {}

    // Make sure all SaveablePrefabs are unique and are actually prefabs
    private void OnValidate()
    {
        var validPrefabs = new List<SaveGameObject>();
        foreach (SaveGameObject obj in SaveablePrefabs)
            if (obj != null && !validPrefabs.Contains(obj) && PrefabUtility.GetPrefabType(obj) == PrefabType.Prefab)
                validPrefabs.Add(obj);
        SaveablePrefabs = validPrefabs;
    }
#endif

    ISerializationPolicy _serializationPolicy = 
        new CustomSerializationPolicy("SaveGame", true, (member) =>
        {
            return member.IsDefined<SaveGameField>(true);
        });

    class ReferenceResolver : IExternalIndexReferenceResolver, IExternalStringReferenceResolver
    {
        Dictionary<object, int> _objectToId = new Dictionary<object, int>();
        List<object> _idToObject = new List<object>();
        public Dictionary<string, Transform> RootObjects;

        public IExternalStringReferenceResolver NextResolver { get; set; }

        public void Register(object obj)
        {
            if (obj == null)
                Debug.LogWarning("Missing object reference");
            else
                _objectToId.Add(obj, _idToObject.Count);
            _idToObject.Add(obj);
        }

        public bool CanReference(object value, out int index)
        {
            index = -1;
            return _objectToId.TryGetValue(value, out index);
        }

        public bool CanReference(object value, out string id)
        {
            id = null;
            var unityObject = value as UnityEngine.Object;
            if (unityObject == null)
                return false;
            Transform transform;
            if (value is Component)
                transform = ((Component)value).transform;
            else if (value is GameObject)
                transform = ((GameObject)value).transform;
            else
                return false;
            if (transform != unityObject)
                id = value.GetType().Name;
            while (transform != null)
            {
                int index;
                if (transform != unityObject && CanReference(transform, out index))
                {
                    id = string.Format("@{0}/{1}", index, id);
                    break;
                }
                id = transform.name + "/" + id;
                transform = transform.parent;
            }
            return true;
        }

        public bool TryResolveReference(int index, out object value)
        {
            if (index < 0 || index >= _idToObject.Count)
            {
                value = null;
                return false;
            }
            value = _idToObject[index];
            return true;
        }

        public bool TryResolveReference(string id, out object value)
        {
            string[] path = id.Split('/');
            string type = path[path.Length - 1];
            Transform transform = null;
            if (id[0] == '@' && TryResolveReference(int.Parse(path[0].Substring(1)), out value))
                transform = value as Transform;
            else
                RootObjects.TryGetValue(path[0], out transform);
            for (int i = 1; transform != null && i < path.Length - 1; ++i)
                transform = transform.Find(path[i]);
            if (transform == null || type == "")
                value = transform;
            else if (type == "GameObject")
                value = transform.gameObject;
            else
                value = transform.GetComponent(type);
            return true;
        }
    }

    // Walk the scene searching for saveable objects. Cannot use GameObject.FindObjectsByType because
    // it does not return results in hierarchy order which is important to ensure correct prefab
    // reattachment and predictable serialization order.
    void FindSaveableObjects(Transform root, List<SaveGameObject> prefabs, List<ISaveable> saveables)
    {
        SaveGameObject saveGame = root.GetComponent<SaveGameObject>();
        if (saveGame != null)
            prefabs.Add(saveGame);
        foreach (ISaveable saveable in root.GetComponents<ISaveable>())
            saveables.Add(saveable);
        foreach (Transform child in root)
            FindSaveableObjects(child, prefabs, saveables);
    }

    void Save(Stream stream, DataFormat format)
    {
        var context = new SerializationContext();
        if (RequireSaveGameAttributeEverywhere)
            context.Config.SerializationPolicy = _serializationPolicy;
        var resolver = new ReferenceResolver();
        context.IndexReferenceResolver = resolver;
        context.StringReferenceResolver = resolver;
        var writer = SerializationUtility.CreateWriter(stream, context, format);

        var prefabs = new List<SaveGameObject>();
        var saveables = new List<ISaveable>();
        foreach (GameObject go in gameObject.scene.GetRootGameObjects())
            FindSaveableObjects(go.transform, prefabs, saveables);

#if UNITY_EDITOR
        if (UnitySerializationUtility.ForceEditorModeSerialization)
        {
            // ensure all types are visited for AOT scan
            foreach (var prefab in SaveablePrefabs)
                FindSaveableObjects(prefab.transform, prefabs, saveables);
        }
#endif

        // for each prefab instance store the prefab name and a reference to its parent transform
        var transformSerializer = Serializer.Get<Transform>();
        var prefabsByName = PrefabsByName();
        writer.BeginStructNode("prefabs", null);
        foreach (SaveGameObject obj in prefabs)
        {
            if (prefabsByName.ContainsKey(obj.PrefabName))
            {
                writer.BeginStructNode(null, null);
                writer.WriteString("prefab", obj.PrefabName);
                transformSerializer.WriteValue("parent", obj.transform.parent, writer);
                resolver.Register(obj.transform);
                writer.EndNode(null);
            }
            else
            {
                Debug.LogErrorFormat("Failed serialising {0} due to missing prefab {1} in SaveablePrefabs list", obj, obj.PrefabName);
            }
        }
        writer.EndNode("prefabs");

        var saveableSerializer = Serializer.Get<MonoBehaviour>();
        writer.BeginStructNode("saveables", null);
        foreach (ISaveable saveable in saveables)
        {
            MonoBehaviour unityObject = saveable as MonoBehaviour;
            writer.BeginStructNode(unityObject.name, unityObject.GetType());
            saveableSerializer.WriteValueWeak("$object", unityObject, writer);
            resolver.Register(unityObject);
            saveable.OnBeforeSave();
            IFormatter formatter = FormatterLocator.GetFormatter(unityObject.GetType(), _serializationPolicy);            
            formatter.Serialize(saveable, writer);
            writer.EndNode(unityObject.name);
        }
        writer.EndNode("saveables");
        writer.FlushToStream();
    }
    Dictionary<string, SaveGameObject> PrefabsByName()
    {
        var prefabsByName = new Dictionary<string, SaveGameObject>(SaveablePrefabs.Count);
        foreach (var prefab in SaveablePrefabs)
            prefabsByName[prefab.PrefabName] = prefab;
        return prefabsByName;
    }

    void Load(Stream stream, DataFormat format = DataFormat.Binary)
    {
        var context = new DeserializationContext();
        if (RequireSaveGameAttributeEverywhere)
            context.Config.SerializationPolicy = _serializationPolicy;
        var resolver = new ReferenceResolver();
        context.IndexReferenceResolver = resolver;
        context.StringReferenceResolver = resolver;
        IDataReader reader = SerializationUtility.CreateReader(stream, context, format);       

        // destroy old prefab instances
        foreach (SaveGameObject obj in GameObject.FindObjectsOfType<SaveGameObject>())
            GameObject.DestroyImmediate(obj.gameObject);

        // set up resolver root objects
        GameObject[] rootObjects = gameObject.scene.GetRootGameObjects();
        resolver.RootObjects = new Dictionary<string, Transform>(rootObjects.Length);
        foreach (GameObject go in rootObjects)
            resolver.RootObjects[go.name] = go.transform;

        Type type;
        if (reader.EnterNode(out type) && reader.CurrentNodeName == "prefabs")
        {
            // instantiate saved prefab instances
            var serializer = Serializer.Get<Transform>();
            var prefabsByName = PrefabsByName();
            while (reader.EnterNode(out type))
            {                
                string prefabName;
                if (reader.ReadString(out prefabName))
                {
                    Transform transform = serializer.ReadValue(reader);
                    Transform instance = null;
                    SaveGameObject prefabObj;
                    if (prefabsByName.TryGetValue(prefabName, out prefabObj))
                        instance = GameObject.Instantiate(prefabObj, transform).transform;
                    else
                        Debug.LogWarningFormat("Missing prefab {0} in SaveablePrefabs list", prefabName);
                    resolver.Register(instance);
                    reader.ExitNode();
                }
            }
            reader.ExitNode();
        }

        if (reader.EnterNode(out type) && reader.CurrentNodeName == "saveables")
        {
            var serializer = Serializer.Get<MonoBehaviour>();
            while (reader.EnterNode(out type))
            {
                var saveable = serializer.ReadValueWeak(reader) as ISaveable;
                resolver.Register(saveable);
                if (saveable != null)
                {
                    IFormatter formatter = FormatterLocator.GetFormatter(saveable.GetType(), _serializationPolicy);
                    formatter.Deserialize(saveable, reader);
                    saveable.Loaded = true;
                    saveable.OnAfterLoad();
                }
                reader.ExitNode();
            }
            reader.ExitNode();
        }
    }
    
    // Get full filename for saved data from name with or without path and extension
    public string GetFilename(string name, Format format = Format.CompressedBinary)
    {
        if (!Path.IsPathRooted(name))
            name = Path.Combine(Application.persistentDataPath, name);
        if (!Path.HasExtension(name))
            name = Path.ChangeExtension(name, format == Format.JSON ? "json" : "bin");
        return name;
    }

    public byte[] Save(Format format = Format.CompressedBinary)
    {
        var stream = new MemoryStream();
        Save(stream, format);
        return stream.ToArray();
    }
    public void Save(Stream stream, Format format = Format.CompressedBinary)
    {
        if (format == Format.CompressedBinary)
        {
            using (var compressedStream = new DeflateStream(stream, CompressionMode.Compress))
            {
                Save(compressedStream, Format.Binary);
            }
        }
        else
        {
            Save(stream, format == Format.Binary ? DataFormat.Binary : DataFormat.JSON);
        }
    }
    public void Save(string name, Format format = Format.CompressedBinary)
    {
        using (var stream = new FileStream(GetFilename(name, format), FileMode.Create))
            Save(stream, format);
    }

    public void Load(byte[] data, Format format = Format.CompressedBinary)
    {
        var stream = new MemoryStream();
        stream.Write(data, 0, data.Length);
        stream.Position = 0;
        Load(stream, format);
    }
    public void Load(string name, Format format = Format.CompressedBinary)
    {
        using (var stream = new FileStream(GetFilename(name, format), FileMode.Open))
            Load(stream, format);
    }
    public void Load(Stream stream, Format format = Format.CompressedBinary)
    {
        if (format == Format.CompressedBinary)
        {
            using (var compressedStream = new DeflateStream(stream, CompressionMode.Decompress))
            {
                Load(compressedStream, Format.Binary);
            }
        }
        else
        {
            Load(stream, format == Format.Binary ? DataFormat.Binary : DataFormat.JSON);
        }
    }
}
