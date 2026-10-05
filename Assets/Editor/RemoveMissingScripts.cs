using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Strips components whose script no longer exists.
///
///   Tools > Emergency VR > Remove Missing Scripts
///
/// These are left behind whenever a MonoBehaviour is deleted while an object still carries it -
/// here, the Cardboard components that stayed on the rig after the Google plugin was removed and
/// CardboardStartup was emptied out.
///
/// They are not merely untidy. The Inspector builds a SerializedObject for every component it
/// draws, and a missing script has no object to build one from, which is where
///
///     MissingReferenceException: The variable m_Targets of GameObjectInspector doesn't exist
///     SerializedObjectNotCreatableException: Object at index 0 is null
///
/// come from. Those two errors follow the selection around and look like a bug in whatever you
/// happen to have selected. Removing the dead components clears them.
///
/// Walks every root object in the open scene, including inactive children.
/// </summary>
public static class RemoveMissingScripts
{
    [MenuItem("Tools/Emergency VR/Remove Missing Scripts")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[RemoveMissingScripts] Exit Play mode first.");
            return;
        }

        var scene = SceneManager.GetActiveScene();
        var log = new StringBuilder($"[RemoveMissingScripts] scene '{scene.name}'\n");

        int objects = 0, removed = 0;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (count == 0) continue;

                Undo.RegisterCompleteObjectUndo(go, "Remove missing scripts");
                int cleared = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);

                objects++;
                removed += cleared;
                log.AppendLine($"  {Path(t)}: removed {cleared}");
            }
        }

        if (removed == 0)
        {
            log.AppendLine("  Nothing to remove - no missing scripts in this scene.");
            Debug.Log(log.ToString());
            return;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        log.AppendLine($"  Total: {removed} dead component(s) on {objects} object(s). Scene saved.");
        Debug.Log(log.ToString());
    }

    static string Path(Transform t)
    {
        var sb = new StringBuilder(t.name);
        for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
        return sb.ToString();
    }
}
