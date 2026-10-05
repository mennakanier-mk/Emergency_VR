#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Keeps the ambulance ready in the Editor, so what you see before Play is what Play starts with:
///  - puts an AmbulanceResponse on the vehicle that carries the paramedic rig (so its settings
///    can be tuned in the Inspector);
///  - seats the rig inside the ambulance - the paramedic in his seat, the stretcher against the
///    rear doors, both at the right size - exactly as AmbulanceResponse will at runtime.
/// Runs after scripts load and right before Play; also Tools > Emergency VR > Place Paramedic In Ambulance.
/// </summary>
[InitializeOnLoad]
static class AmbulanceEditorSetup
{
    static AmbulanceEditorSetup()
    {
        EditorApplication.delayCall += () => { if (!Application.isPlaying) Run(false); };
        EditorApplication.playModeStateChanged -= OnPlay;
        EditorApplication.playModeStateChanged += OnPlay;
    }

    static void OnPlay(PlayModeStateChange s)
    {
        if (s == PlayModeStateChange.ExitingEditMode) Run(false);
    }

    [MenuItem("Tools/Emergency VR/Place Paramedic In Ambulance")]
    static void Menu() => Run(true);

    static void Run(bool verbose)
    {
        foreach (var d in Object.FindObjectsByType<RoadDriver>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Transform rig = null;
            foreach (var t in d.GetComponentsInChildren<Transform>(true))
                if (t.Find("STR_ROOT") != null) { rig = t; break; }
            if (rig == null) continue;

            var amb = d.GetComponent<AmbulanceResponse>();
            bool added = false;
            if (amb == null) { amb = Undo.AddComponent<AmbulanceResponse>(d.gameObject); added = true; }

            // Where it will start driving at Play - so it does not jump there at the first frame.
            Undo.RecordObject(d.transform, "Place ambulance at its start");
            Undo.RecordObject(d, "Place ambulance at its start");
            bool placed = amb.PlaceAtStartInEditor();
            if (placed) { EditorUtility.SetDirty(d); Physics.SyncTransforms(); }

            Vector3 p0 = rig.localPosition; Quaternion r0 = rig.localRotation; Vector3 s0 = rig.localScale;
            Undo.RegisterFullObjectHierarchyUndo(rig.gameObject, "Place paramedic in ambulance");
            amb.PlaceRigInEditor();

            // The oxygen mask with its Inspector settings, so its shape can be edited before Play.
            var mask = rig.Find("FA_mask");
            if (mask != null && mask.GetComponent<OxygenMask>() == null)
            {
                Undo.AddComponent<OxygenMask>(mask.gameObject);
                added = true;
            }

            bool moved = (rig.localPosition - p0).sqrMagnitude > 1e-8f || Quaternion.Angle(rig.localRotation, r0) > 0.01f ||
                         (rig.localScale - s0).sqrMagnitude > 1e-12f;
            if (placed)
            {
                EditorSceneManager.MarkSceneDirty(d.gameObject.scene);
                Debug.Log($"[Ambulance] '{d.name}' placed at its starting spot behind the accident car (where Play starts it). Save the scene to keep it.", d);
            }
            if (added || moved)
            {
                EditorSceneManager.MarkSceneDirty(d.gameObject.scene);
                Debug.Log($"[Ambulance] paramedic rig seated inside '{d.name}' in the Editor" +
                          (added ? " (AmbulanceResponse added)" : "") + ". Save the scene (Ctrl+S) to keep it.", d);
            }
            else if (verbose) Debug.Log($"[Ambulance] '{d.name}': rig already in place.", d);
        }
    }
}
#endif
