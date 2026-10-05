using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Switches the project from the Google Cardboard XR Plugin to SimpleStereoVR.
///
/// Tools > Emergency VR > Setup Simple Stereo VR
///
/// Two things have to happen and both are easy to forget:
///
/// 1. Every XR loader for Android must be turned off. SimpleStereoVR owns the camera rig now;
///    an active loader would initialise an XR display and fight it for the head pose. This is
///    the same as unticking everything in Project Settings > XR Plug-in Management > Android,
///    done through SerializedObject so no XR Management types are referenced - those assemblies
///    disappear along with the plugin.
///
/// 2. SimpleStereoVR goes on the rig, and SimpleVRMovement comes off. SimpleVRMovement returns
///    early outside the Editor, so on device it does nothing, but in the Editor it fights
///    PhoneVRLocomotion over the same CharacterController.
///
/// Re-runnable. Reports what it found so a silent no-op is impossible to miss.
/// </summary>
public static class StereoVRSetup
{
    const string kScenePath = "Assets/Scenes/SampleScene.unity";
    const string kRigName = "XR Origin Hands (XR Rig)";
    const string kXrConfigKey = "com.unity.xr.management.loader_settings";
    const string kDistortionShader = "EmergencyVR/StereoBarrelDistortion";
    const string kMaterialPath = "Assets/Resources/StereoDistortion.mat";

    [MenuItem("Tools/Emergency VR/Setup Simple Stereo VR")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[StereoVRSetup] Exit Play mode first.");
            return;
        }

        var log = new StringBuilder("[StereoVRSetup]\n");

        DisableXrLoaders(log);
        EnsureDistortionMaterial(log);
        EnsureShaderAlwaysIncluded(log);
        SetupRig(log);

        AssetDatabase.SaveAssets();
        EditorApplication.ExecuteMenuItem("File/Save Project");

        Debug.Log(log.ToString());
    }

    // ---------------------------------------------------------------- XR loaders

    static void DisableXrLoaders(StringBuilder log)
    {
        if (!EditorBuildSettings.TryGetConfigObject(kXrConfigKey, out Object configObject) ||
            configObject == null)
        {
            log.AppendLine("  XR loaders: no XR Management settings asset - nothing to disable.");
            return;
        }

        // The real field names are Keys / Values, not m_Settings.m_Keys. An earlier version
        // guessed and reported "unexpected XR Management layout" while quietly doing nothing.
        var perTarget = new SerializedObject(configObject);
        var keys = perTarget.FindProperty("Keys");
        var values = perTarget.FindProperty("Values");

        if (keys == null || values == null || !values.isArray)
        {
            log.AppendLine("  XR loaders: could not read Keys/Values - turn them off by hand in " +
                           "Project Settings > XR Plug-in Management.");
            return;
        }

        bool touchedAny = false;

        // Every platform, not just Android: Editor Play mode uses the Desktop settings, so an
        // OpenXR loader left on there keeps the split from ever showing in the Editor.
        for (int i = 0; i < values.arraySize; i++)
        {
            var generalSettings = values.GetArrayElementAtIndex(i).objectReferenceValue;
            if (generalSettings == null) continue;

            string targetName = i < keys.arraySize
                ? BuildTargetGroupName(keys.GetArrayElementAtIndex(i).intValue)
                : "target " + i;

            var general = new SerializedObject(generalSettings);

            var initOnStart = general.FindProperty("m_InitManagerOnStart");
            if (initOnStart != null && initOnStart.boolValue)
            {
                initOnStart.boolValue = false;
                log.AppendLine($"  XR loaders [{targetName}]: Initialize XR on Startup  ON -> OFF");
                touchedAny = true;
            }

            var managerProp = general.FindProperty("m_LoaderManagerInstance");
            general.ApplyModifiedProperties();
            EditorUtility.SetDirty(generalSettings);

            var manager = managerProp != null ? managerProp.objectReferenceValue : null;
            if (manager == null) continue;

            var managerSo = new SerializedObject(manager);
            var loaders = managerSo.FindProperty("m_Loaders");
            if (loaders == null || !loaders.isArray || loaders.arraySize == 0) continue;

            log.AppendLine($"  XR loaders [{targetName}]: removing {loaders.arraySize} loader(s)");
            for (int l = 0; l < loaders.arraySize; l++)
            {
                var loader = loaders.GetArrayElementAtIndex(l).objectReferenceValue;
                log.AppendLine($"      - {(loader != null ? loader.name : "(null)")}");
            }

            loaders.ClearArray();

            // These two make XR Management start the (now empty) loader list automatically.
            var autoLoad = managerSo.FindProperty("m_AutomaticLoading");
            if (autoLoad != null) autoLoad.boolValue = false;
            var autoRun = managerSo.FindProperty("m_AutomaticRunning");
            if (autoRun != null) autoRun.boolValue = false;

            managerSo.ApplyModifiedProperties();
            EditorUtility.SetDirty(manager);
            touchedAny = true;
        }

        perTarget.ApplyModifiedProperties();
        EditorUtility.SetDirty(configObject);

        if (!touchedAny) log.AppendLine("  XR loaders: already clear on every platform.");
    }

    static string BuildTargetGroupName(int group)
    {
        switch (group)
        {
            case 1: return "Standalone";
            case 4: return "iOS";
            case 7: return "Android";
            case 13: return "WSA";
            default: return "group " + group;
        }
    }

    // ---------------------------------------------------------------- distortion material

    /// <summary>
    /// Creates Assets/Resources/StereoDistortion.mat.
    ///
    /// This is the belt to Always Included Shaders' braces, and it is the one that actually
    /// saved the build. A shader reached only through Shader.Find at runtime has nothing
    /// referencing it, so the player build strips it - which is why the first APK showed two
    /// flat rectangles instead of two round lenses while the Editor looked fine. An asset in a
    /// Resources folder is always included and pulls its shader in with it, so SimpleStereoVR
    /// loads the material from Resources first and only falls back to Shader.Find.
    /// </summary>
    static void EnsureDistortionMaterial(StringBuilder log)
    {
        var shader = Shader.Find(kDistortionShader);
        if (shader == null)
        {
            log.AppendLine($"  Material: shader '{kDistortionShader}' not found, so no material " +
                           "was created. Is Assets/Shaders/StereoBarrelDistortion.shader " +
                           "imported and compiling?");
            return;
        }

        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
        {
            AssetDatabase.CreateFolder("Assets", "Resources");
            log.AppendLine("  Material: created Assets/Resources.");
        }

        var existing = AssetDatabase.LoadAssetAtPath<Material>(kMaterialPath);
        if (existing != null)
        {
            if (existing.shader != shader)
            {
                existing.shader = shader;
                EditorUtility.SetDirty(existing);
                log.AppendLine($"  Material: repointed {kMaterialPath} at the distortion shader.");
            }
            else
            {
                log.AppendLine($"  Material: {kMaterialPath} already present.");
            }
            return;
        }

        var mat = new Material(shader) { name = "StereoDistortion" };
        AssetDatabase.CreateAsset(mat, kMaterialPath);
        log.AppendLine($"  Material: created {kMaterialPath} - this is what keeps the lens " +
                       "distortion in the build.");
    }

    // ---------------------------------------------------------------- shader stripping

    /// <summary>
    /// The distortion shader lives only in a material created at runtime, so nothing in the
    /// scene references it and the build strips it out. Registering it here is what keeps it.
    /// </summary>
    static void EnsureShaderAlwaysIncluded(StringBuilder log)
    {
        var shader = Shader.Find(kDistortionShader);
        if (shader == null)
        {
            log.AppendLine($"  Shader: '{kDistortionShader}' not found. Is " +
                           "Assets/Shaders/StereoBarrelDistortion.shader imported and compiling?");
            return;
        }

        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (assets == null || assets.Length == 0 || assets[0] == null)
        {
            log.AppendLine("  Shader: could not open GraphicsSettings - add it by hand under " +
                           "Project Settings > Graphics > Always Included Shaders.");
            return;
        }

        var so = new SerializedObject(assets[0]);
        var list = so.FindProperty("m_AlwaysIncludedShaders");
        if (list == null || !list.isArray)
        {
            log.AppendLine("  Shader: unexpected GraphicsSettings layout - add it by hand.");
            return;
        }

        for (int i = 0; i < list.arraySize; i++)
        {
            if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader)
            {
                log.AppendLine("  Shader: already in Always Included Shaders.");
                return;
            }
        }

        int index = list.arraySize;
        list.InsertArrayElementAtIndex(index);
        list.GetArrayElementAtIndex(index).objectReferenceValue = shader;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(assets[0]);

        log.AppendLine($"  Shader: added '{kDistortionShader}' to Always Included Shaders.");
    }

    // ---------------------------------------------------------------- scene rig

    static void SetupRig(StringBuilder log)
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != kScenePath)
            log.AppendLine($"  Rig: note - active scene is '{scene.path}', not {kScenePath}. " +
                           "Continuing anyway.");

        // Find the rig by what it HAS, not by what it is called. An earlier version matched the
        // name exactly and quietly did nothing when it did not line up.
        GameObject rig = null;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var loco in root.GetComponentsInChildren<PhoneVRLocomotion>(true))
            {
                rig = loco.gameObject;
                break;
            }
            if (rig != null) break;
        }

        if (rig == null)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var cc in root.GetComponentsInChildren<CharacterController>(true))
                {
                    if (cc.GetComponentInChildren<Camera>(true) == null) continue;
                    rig = cc.gameObject;
                    break;
                }
                if (rig != null) break;
            }
        }

        if (rig == null)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != kRigName) continue;
                    rig = t.gameObject;
                    break;
                }
                if (rig != null) break;
            }
        }

        if (rig == null)
        {
            log.AppendLine("  Rig: FAILED - no object found carrying PhoneVRLocomotion, a " +
                           $"CharacterController with a child Camera, or the name '{kRigName}'. " +
                           "Add SimpleStereoVR to your rig by hand.");
            return;
        }

        log.AppendLine($"  Rig: using '{rig.name}'.");

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Setup simple stereo VR");

        if (rig.GetComponent<SimpleStereoVR>() == null)
        {
            Undo.AddComponent<SimpleStereoVR>(rig);
            log.AppendLine($"  Rig: added SimpleStereoVR to '{rig.name}'.");
        }
        else
        {
            log.AppendLine("  Rig: SimpleStereoVR already present.");
        }

        if (rig.GetComponent<PhoneVRLocomotion>() == null)
        {
            Undo.AddComponent<PhoneVRLocomotion>(rig);
            log.AppendLine("  Rig: added PhoneVRLocomotion.");
        }
        else
        {
            log.AppendLine("  Rig: PhoneVRLocomotion already present.");
        }

        var legacy = rig.GetComponent<SimpleVRMovement>();
        if (legacy != null && legacy.enabled)
        {
            Undo.RecordObject(legacy, "Disable SimpleVRMovement");
            legacy.enabled = false;
            EditorUtility.SetDirty(legacy);
            log.AppendLine("  Rig: disabled SimpleVRMovement (it fights PhoneVRLocomotion in the Editor).");
        }

        var controller = rig.GetComponent<CharacterController>();
        if (controller == null)
            log.AppendLine("  Rig: WARNING no CharacterController - PhoneVRLocomotion cannot move anything.");

        var cam = rig.GetComponentInChildren<Camera>(true);
        log.AppendLine(cam != null
            ? $"  Rig: head camera is '{cam.name}'."
            : "  Rig: WARNING no Camera under the rig - SimpleStereoVR has nothing to split.");

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        log.AppendLine("  Rig: scene saved.");
    }
}
