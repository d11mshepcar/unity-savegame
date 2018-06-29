# unity-savegame
Save game system for Unity using Odin Serializer. 

**NOTE:** This currently uses a forked version of odin-serializer which allows deserialization into existing classes.  Sirenix will hopefully be adding this functionality in a future release.

## Usage

Add the `[SaveGameField]` attribute to any field you wish to save in your `MonoBehaviour` classes and inherit from `SaveableMonoBehaviour` instead (or implement the `ISaveable` interface).  Then add a `SaveGame` component somewhere in your scene and call `SaveGame.Save` and `SaveGame.Load` when you want to save or load the state of that scene.

If you want any prefabs you have instantiated to persist in the save game you can add a `SaveGameObject` component to the root of the prefab.  You also need to add these prefabs to the list of `SaveablePrefabs` in your `SaveGame` object.
