using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

/// <summary>
/// No more "PLAY IN VR / CONTROLLER" chooser: the APK opens straight into the game.
///
/// Keeps RemoteController.unity out of the build and SampleScene as the first scene - when the
/// editor loads, and again right before every build, so nothing can put the chooser back.
/// </summary>
[InitializeOnLoad]
public class StartDirectlyInGame : IPreprocessBuildWithReport
{
    const string kGameScene = "Assets/Scenes/SampleScene.unity";
    const string kChooserScene = "Assets/Scenes/RemoteController.unity";

    public int callbackOrder => -1000;

    static StartDirectlyInGame()
    {
        EditorApplication.delayCall += Apply;
    }

    public void OnPreprocessBuild(BuildReport report) => Apply();

    [MenuItem("Tools/Emergency/Start Directly In Game (no chooser)")]
    static void Apply()
    {
        var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(kGameScene, true) };
        foreach (var s in EditorBuildSettings.scenes)
            if (s.path != kGameScene && s.path != kChooserScene) scenes.Add(s);

        // Only write when something actually changes.
        var current = EditorBuildSettings.scenes;
        bool same = current.Length == scenes.Count;
        for (int i = 0; same && i < current.Length; i++)
            same = current[i].path == scenes[i].path && current[i].enabled == scenes[i].enabled;
        if (same) return;

        EditorBuildSettings.scenes = scenes.ToArray();
        UnityEngine.Debug.Log("[StartDirectlyInGame] Build opens straight into SampleScene (chooser scene removed from the build).");
    }
}
