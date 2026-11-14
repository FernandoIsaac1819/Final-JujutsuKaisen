using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// After each script reload, gives any PlayerController in the open scenes that has no
/// PlayerSettings the default asset, and marks the scene dirty so a save keeps it.
/// Also available manually via Tools > JJK > Assign Default Player Settings.
[InitializeOnLoad]
public static class PlayerSettingsAutoAssign
{
    private const string DefaultAssetPath = "Assets/_Characters/Yuta/YutaPlayerSettings.asset";

    static PlayerSettingsAutoAssign()
    {
        EditorApplication.delayCall += AssignMissing;
    }

    [MenuItem("Tools/JJK/Assign Default Player Settings")]
    private static void AssignMissing()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        var asset = AssetDatabase.LoadAssetAtPath<PlayerSettings>(DefaultAssetPath);
        if (asset == null)
        {
            Debug.LogWarning($"[PlayerSettings] Default asset not found at {DefaultAssetPath}.");
            return;
        }

        foreach (var pc in Object.FindObjectsByType<PlayerController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var so = new SerializedObject(pc);
            var prop = so.FindProperty("settings");
            if (prop == null || prop.objectReferenceValue != null) continue;

            prop.objectReferenceValue = asset;
            so.ApplyModifiedProperties();
            EditorSceneManager.MarkSceneDirty(pc.gameObject.scene);
            Debug.Log($"[PlayerSettings] Assigned {asset.name} to '{pc.name}'. Save the scene to keep it.", pc);
        }
    }
}
