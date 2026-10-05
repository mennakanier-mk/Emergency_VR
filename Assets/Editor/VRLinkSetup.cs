using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Two Phone Setup > Set Up The Second Phone
///   Tools > Emergency VR > Two Phone Setup > Check The Link
///
/// Does the whole two-phone arrangement in one go, because every part of it is somewhere a
/// hand-made mistake goes unnoticed until the APK is on a phone.
///
/// What it does:
///   1. Puts a VR Link Receiver on the rig in the city scene, so the game listens.
///   2. Makes Assets/Scenes/RemoteController.unity - the launch screen with PLAY IN VR and
///      CONTROLLER, and the four arrows behind it.
///   3. Puts that scene FIRST in Build Settings and the city second.
///
/// Step 3 is the one that matters most and the one most easily got wrong. The scene that is first
/// in Build Settings is the one Android opens, so the chooser has to be first - otherwise the
/// build loads the whole city on both phones and the controller never appears. Both phones get
/// the same APK; each one picks its side on first launch and remembers it.
/// </summary>
public static class VRLinkSetup
{
    const string kControllerScene = "Assets/Scenes/RemoteController.unity";

    [MenuItem("Tools/Emergency VR/Two Phone Setup/Set Up The Second Phone")]
    public static void SetUp()
    {
        var log = new StringBuilder("[VRLink] setup\n\n");

        string gameScenePath = AddReceiverToOpenScene(log);
        if (gameScenePath == null) return;

        CreateControllerScene(gameScenePath, log);
        OrderBuildScenes(gameScenePath, log);

        log.AppendLine();
        log.AppendLine("  Build once. Install the same APK on both phones.");
        log.AppendLine("  One taps PLAY IN VR, the other taps CONTROLLER.");
        log.AppendLine("  If they do not find each other, read the address printed along the");
        log.AppendLine("  bottom of the game's screen and type it into the controller.");

        Debug.Log(log.ToString());
    }

    // ------------------------------------------------------------------ 1. the receiver

    static string AddReceiverToOpenScene(StringBuilder log)
    {
        var scene = SceneManager.GetActiveScene();

        if (string.IsNullOrEmpty(scene.path))
        {
            Debug.LogError("[VRLink] Save the city scene first - an unsaved scene has no path " +
                           "to put in Build Settings.");
            return null;
        }

        var existing = Object.FindFirstObjectByType<VRLinkReceiver>();

        if (existing != null)
        {
            log.AppendLine($"  receiver already on '{existing.gameObject.name}'");
        }
        else
        {
            // The rig, identified by what it is rather than by what it is called: the thing that
            // owns the locomotion is the thing that needs the arrows.
            var locomotion = Object.FindFirstObjectByType<PhoneVRLocomotion>();

            GameObject host = locomotion != null
                ? locomotion.gameObject
                : (Object.FindFirstObjectByType<SimpleStereoVR>() != null
                    ? Object.FindFirstObjectByType<SimpleStereoVR>().gameObject
                    : null);

            if (host == null)
            {
                Debug.LogError("[VRLink] No PhoneVRLocomotion or SimpleStereoVR in this scene. " +
                               "Open the city scene with the VR rig in it and run this again.");
                return null;
            }

            Undo.AddComponent<VRLinkReceiver>(host);
            log.AppendLine($"  receiver added to '{host.name}'");
        }

        // Make sure locomotion will actually read it.
        foreach (var loco in Object.FindObjectsByType<PhoneVRLocomotion>(FindObjectsInactive.Include,
                                                                        FindObjectsSortMode.None))
        {
            Undo.RecordObject(loco, "Enable remote");
            loco.useRemote = true;
            loco.remote = Object.FindFirstObjectByType<VRLinkReceiver>();
            EditorUtility.SetDirty(loco);

            log.AppendLine($"  '{loco.name}' will accept the arrows");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        log.AppendLine($"  saved {scene.path}");
        return scene.path;
    }

    // ------------------------------------------------------------------ 2. the controller scene

    static void CreateControllerScene(string gameScenePath, StringBuilder log)
    {
        string gameSceneName = Path.GetFileNameWithoutExtension(gameScenePath);

        var cityScene = SceneManager.GetActiveScene();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

        // A camera with nothing to draw but the UI. Solid colour rather than skybox, because a
        // skybox on a controller screen is a few megabytes of texture for something nobody looks
        // at.
        var camGo = new GameObject("UI Camera");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
        cam.cullingMask = 0;
        cam.orthographic = true;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 10f;

        var go = new GameObject("VR Link Controller");
        var controller = go.AddComponent<VRLinkController>();
        controller.gameSceneName = gameSceneName;

        // Said explicitly rather than trusting which scene is active. A new additive scene does
        // not reliably become the active one, and objects created into the city scene by mistake
        // would be saved into the city - a change that is easy to miss and unpleasant to undo.
        SceneManager.MoveGameObjectToScene(camGo, scene);
        SceneManager.MoveGameObjectToScene(go, scene);

        Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, kControllerScene);

        log.AppendLine($"  made {kControllerScene}  ->  PLAY loads '{gameSceneName}'");

        // Leave the city scene as the open one, so this does not quietly change what is being
        // worked on.
        EditorSceneManager.CloseScene(scene, true);

        if (cityScene.IsValid()) SceneManager.SetActiveScene(cityScene);
    }

    // ------------------------------------------------------------------ 3. build order

    static void OrderBuildScenes(string gameScenePath, StringBuilder log)
    {
        var wanted = new List<EditorBuildSettingsScene>
        {
            new EditorBuildSettingsScene(kControllerScene, true),
            new EditorBuildSettingsScene(gameScenePath, true)
        };

        // Keep anything else that was already listed, after those two, so an existing test scene
        // is not silently dropped.
        foreach (var s in EditorBuildSettings.scenes)
        {
            if (s.path == kControllerScene || s.path == gameScenePath) continue;
            wanted.Add(s);
        }

        EditorBuildSettings.scenes = wanted.ToArray();

        log.AppendLine("  build order:");
        for (int i = 0; i < wanted.Count; i++)
            log.AppendLine($"    {i}  {wanted[i].path}{(wanted[i].enabled ? "" : "   (off)")}");
    }

    // ------------------------------------------------------------------ check

    [MenuItem("Tools/Emergency VR/Two Phone Setup/Check The Link")]
    public static void Check()
    {
        var log = new StringBuilder("[VRLink] check\n\n");
        bool ok = true;

        var receiver = Object.FindFirstObjectByType<VRLinkReceiver>();
        log.AppendLine(receiver != null
            ? $"  receiver      on '{receiver.gameObject.name}'"
            : "  receiver      MISSING - run Set Up The Second Phone");
        if (receiver == null) ok = false;

        var loco = Object.FindFirstObjectByType<PhoneVRLocomotion>();

        if (loco == null)
        {
            log.AppendLine("  locomotion    MISSING - nothing would move even with a controller");
            ok = false;
        }
        else
        {
            log.AppendLine($"  locomotion    on '{loco.name}', remote {(loco.useRemote ? "on" : "OFF")}");
            if (!loco.useRemote) ok = false;

            var cc = loco.GetComponent<CharacterController>();
            if (cc == null) cc = loco.GetComponentInParent<CharacterController>();
            if (cc == null) cc = loco.GetComponentInChildren<CharacterController>();

            log.AppendLine(cc != null
                ? $"  controller    CharacterController on '{cc.name}', radius {cc.radius:0.00}, height {cc.height:0.00}"
                : "  controller    NO CharacterController - this alone stops all movement");
            if (cc == null) ok = false;

            // The other silent stopper: a second script writing the same CharacterController.
            var others = loco.GetComponents<MonoBehaviour>();
            foreach (var m in others)
            {
                if (m == null || m == loco) continue;
                if (!m.enabled) continue;

                string n = m.GetType().Name;
                if (n == "SimpleVRMovement" || n == "FirstPersonController" || n == "PlayerMovement")
                {
                    log.AppendLine($"  conflict      '{n}' is also enabled on this object - two " +
                                   "scripts moving one CharacterController fight each other. " +
                                   "Turn it off.");
                    ok = false;
                }
            }
        }

        bool controllerSceneExists = File.Exists(kControllerScene);
        log.AppendLine(controllerSceneExists
            ? "  scene         RemoteController.unity present"
            : "  scene         MISSING - run Set Up The Second Phone");
        if (!controllerSceneExists) ok = false;

        var scenes = EditorBuildSettings.scenes;
        bool firstIsChooser = scenes.Length > 0 && scenes[0].path == kControllerScene && scenes[0].enabled;

        log.AppendLine(firstIsChooser
            ? "  build order   chooser is first - correct"
            : "  build order   chooser is NOT first - the APK would open straight into the city");
        if (!firstIsChooser) ok = false;

        log.AppendLine();
        log.AppendLine(ok
            ? "  Ready to build."
            : "  Fix the lines above, then run Check The Link again.");

        Debug.Log(log.ToString());
    }

    // ------------------------------------------------------------------ convenience

    /// <summary>
    /// Turns the chooser off again, so the APK opens straight into the city.
    /// Worth doing once the two phones are set up and you no longer want the launch screen.
    /// </summary>
    [MenuItem("Tools/Emergency VR/Two Phone Setup/Skip The Chooser (city opens directly)")]
    public static void SkipChooser()
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);

        int idx = scenes.FindIndex(s => s.path == kControllerScene);
        if (idx <= 0)
        {
            Debug.Log("[VRLink] the chooser is not first, so nothing to skip.");
            return;
        }

        var chooser = scenes[idx];
        scenes.RemoveAt(idx);
        scenes.Add(chooser);          // still built, so PLAY -> city -> back still resolves

        EditorBuildSettings.scenes = scenes.ToArray();

        Debug.Log($"[VRLink] chooser moved to last. The APK now opens '{scenes[0].path}' " +
                  "directly, and the controller phone needs its own build with the chooser first.");
    }
}
