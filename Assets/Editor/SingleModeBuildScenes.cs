using System.Collections.Generic;
using UnityEditor;

/// <summary>
/// One mode only: the APK opens straight into the game.
///
/// The old two-phone setup put RemoteController.unity (the PLAY IN VR / CONTROLLER chooser)
/// first in Build Settings. This removes it from the build and keeps the game scene first -
/// automatically, every time the scripts reload, so it cannot creep back in.
/// </summary>
[InitializeOnLoad]
public static class SingleModeBuildScenes
{
    const string kGameScene = "Assets/Scenes/SampleScene.unity";
    const string kChooserScene = "Assets/Scenes/RemoteController.unity";

    static SingleModeBuildScenes()
    {
        EditorApplication.delayCall += Apply;
    }

    [MenuItem("Tools/Emergency/Build Opens Game Directly")]
    static void Apply()
    {
        var current = EditorBuildSettings.scenes;
        var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(kGameScene, true) };
        foreach (var s in current)
        {
            if (s.path == kGameScene || s.path == kChooserScene) continue;
            scenes.Add(s);
        }

        bool same = current.Length == scenes.Count;
        for (int i = 0; same && i < scenes.Count; i++)
            same = current[i].path == scenes[i].path && current[i].enabled == scenes[i].enabled;
        if (same) return;

        EditorBuildSettings.scenes = scenes.ToArray();
        UnityEngine.Debug.Log("[SingleModeBuildScenes] Build now opens '" + kGameScene +
                              "' directly (mode chooser removed from the build).");
    }
}
