#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// The street background sound (Assets/sound/street) plays all the time, on a loop, from the
/// camera - so it is always heard the same wherever you walk. An AudioSource with it is put on
/// the VR head camera (the one SimpleStereoVR uses) when the project loads and before Play.
/// Its volume can be changed on that AudioSource.
/// </summary>
[InitializeOnLoad]
static class StreetAmbienceSetup
{
    static StreetAmbienceSetup()
    {
        EditorApplication.delayCall += () => { if (!Application.isPlaying) Run(); };
        EditorApplication.playModeStateChanged -= OnPlay;
        EditorApplication.playModeStateChanged += OnPlay;
    }

    static void OnPlay(PlayModeStateChange s) { if (s == PlayModeStateChange.ExitingEditMode) Run(); }

    [MenuItem("Tools/Emergency VR/Put Street Sound On Camera")]
    static void Run()
    {
        AudioClip clip = null;
        foreach (var guid in AssetDatabase.FindAssets("street t:AudioClip", new[] { "Assets/sound" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant() == "street")
            { clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path); break; }
        }
        if (clip == null) return;

        var vr = Object.FindFirstObjectByType<SimpleStereoVR>();
        Transform head = vr != null && vr.headCamera != null ? vr.headCamera.transform
                       : (Camera.main != null ? Camera.main.transform : null);
        if (head == null) return;

        AudioSource src = null;
        foreach (var a in head.GetComponents<AudioSource>()) if (a.clip == clip) { src = a; break; }
        bool added = false;
        if (src == null) { src = Undo.AddComponent<AudioSource>(head.gameObject); src.clip = clip; src.volume = 0.6f; added = true; }
        else Undo.RecordObject(src, "Street sound");

        bool changed = added || !src.loop || !src.playOnAwake || src.spatialBlend != 0f || src.mute;
        src.loop = true;
        src.playOnAwake = true;
        src.spatialBlend = 0f;         // 2D: the same in both ears, wherever you are
        src.dopplerLevel = 0f;
        src.mute = false;
        if (!changed) return;

        EditorUtility.SetDirty(src);
        EditorSceneManager.MarkSceneDirty(head.gameObject.scene);
        Debug.Log($"[Street sound] '{clip.name}' looping on '{head.name}'. Save the scene (Ctrl+S) to keep it.", src);
    }
}
#endif
