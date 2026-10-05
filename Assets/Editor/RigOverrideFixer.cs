#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// The paramedic rig (em.fbx) is one clip that moves the armature, the stretcher, the bag and
/// the mask TOGETHER. If any child object of the rig has been moved/turned/scaled by hand in the
/// scene, and the clip does not animate that object, the hand edit stays - and the paramedic
/// ends up metres away from his own stretcher. This puts the children back to the FBX values
/// (the rig root itself is placed at runtime by AmbulanceResponse and is left alone).
///
/// Runs once after scripts load, and from Tools > Emergency VR > Reset Paramedic Rig Children.
/// </summary>
[InitializeOnLoad]
static class RigOverrideFixer
{
    static RigOverrideFixer()
    {
        EditorApplication.delayCall += () => { if (!Application.isPlaying) Fix(false); };
        EditorApplication.playModeStateChanged -= OnPlayMode;
        EditorApplication.playModeStateChanged += OnPlayMode;
    }

    static void OnPlayMode(PlayModeStateChange s)
    {
        // Right before Play starts (still in edit mode, so the fix is a real scene change).
        if (s == PlayModeStateChange.ExitingEditMode || s == PlayModeStateChange.EnteredEditMode) Fix(false);
    }

    [MenuItem("Tools/Emergency VR/Reset Paramedic Rig Children")]
    static void FixMenu() => Fix(true);

    static void Fix(bool verbose)
    {
        int fixedCount = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t.Find("STR_ROOT") == null) continue;           // t = a rig root like "em"
            if (!PrefabUtility.IsPartOfPrefabInstance(t)) continue;

            foreach (Transform child in t.GetComponentsInChildren<Transform>(true))
            {
                if (child == t) continue;
                var mods = PrefabUtility.GetPropertyModifications(child.gameObject);
                if (mods == null) continue;
                bool moved = mods.Any(m => m.target == PrefabUtility.GetCorrespondingObjectFromSource(child) &&
                                           (m.propertyPath.StartsWith("m_LocalPosition") ||
                                            m.propertyPath.StartsWith("m_LocalRotation") ||
                                            m.propertyPath.StartsWith("m_LocalScale") ||
                                            m.propertyPath.StartsWith("m_LocalEulerAnglesHint")));
                if (!moved) continue;

                string before = $"pos {child.localPosition:F2} rot {child.localEulerAngles:F0} scale {child.localScale:F2}";
                PrefabUtility.RevertObjectOverride(child, InteractionMode.AutomatedAction);
                Debug.Log($"[RigFix] '{t.name}/{child.name}' had been moved by hand ({before}) - " +
                          $"reset to the FBX: pos {child.localPosition:F2} rot {child.localEulerAngles:F0} scale {child.localScale:F2}.");
                fixedCount++;
            }
            if (fixedCount > 0) EditorSceneManager.MarkSceneDirty(t.gameObject.scene);
        }
        if (verbose || fixedCount > 0)
            Debug.Log($"[RigFix] {fixedCount} rig child transform(s) reset. Save the scene (Ctrl+S) to keep it.");
    }
}
#endif
