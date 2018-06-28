using UnityEditor;
using UnityEngine;

// Add a SaveGameObject to any prefabs you want to save instances of in your save game data
public class SaveGameObject : SaveableMonoBehaviour
#if UNITY_EDITOR
    , ISerializationCallbackReceiver
#endif
{
    [HideInInspector] public string PrefabName;

    class TransformData
    {
        [SaveGameField] public Vector3 localPosition;
        [SaveGameField] public Quaternion localRotation;
        [SaveGameField] public Vector3 localScale;
    }
    [SaveGameField] TransformData _transform
    {
        get
        {
            return new TransformData
            {
                localPosition = transform.localPosition,
                localRotation = transform.localRotation,
                localScale = transform.localScale
            };
        }
        set 
        {
            transform.localPosition = value.localPosition;
            transform.localRotation = value.localRotation;
            transform.localScale = value.localScale;
        }
    }

#if UNITY_EDITOR
    void ISerializationCallbackReceiver.OnBeforeSerialize()
    {
        if (Application.isEditor)
        {
            SaveGameObject prefab = this;
            if (PrefabUtility.GetPrefabType(this) != PrefabType.Prefab)
            {
                prefab = (SaveGameObject)PrefabUtility.GetPrefabParent(this);
            }
            if (prefab != null)
            {
                PrefabName = AssetDatabase.GetAssetPath(prefab);
            }
        }
    }
    void ISerializationCallbackReceiver.OnAfterDeserialize()
    { }
#endif
}
