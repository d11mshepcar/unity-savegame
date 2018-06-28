using UnityEngine;
using System.Collections;
using UnityEditor;

[CustomEditor(typeof(SaveGame))]
public class SaveGameEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        if (GUILayout.Button("Add prefabs used in scene"))
        {
            SaveGame saveGame = (SaveGame)target;
            foreach (var savegameobj in GameObject.FindObjectsOfType<SaveGameObject>())
            {
                SaveGameObject prefab = (SaveGameObject)PrefabUtility.GetPrefabParent(savegameobj);
                if (prefab)
                    saveGame.AddSaveablePrefab(prefab);
            }
        }
    }
}
