using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Unity.XR.CoreUtils;

public static class EmergencySceneSetup
{
    [InitializeOnLoadMethod]
    static void Schedule() { EditorApplication.delayCall += RunRequested; }

    static void RunRequested()
    {
        if (!File.Exists("Library/EmergencySceneSetup.request")) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        File.Delete("Library/EmergencySceneSetup.request");
        try { Apply(); }
        catch (Exception e) { File.WriteAllText("Library/EmergencySceneSetup.result", e.ToString()); Debug.LogException(e); }
    }

    [MenuItem("Tools/Emergency VR/Apply Android Lighting and Grounding")]
    public static void Apply()
    {
        var scene = SceneManager.GetActiveScene();
        if (EditorApplication.isPlayingOrWillChangePlaymode || scene.path != "Assets/Scenes/SampleScene.unity")
            throw new InvalidOperationException("Open SampleScene in Edit mode first.");
        var transforms = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToArray();
        var movement = transforms.Select(t => t.GetComponent<SimpleVRMovement>()).Single(m => m != null);
        var origin = movement.GetComponent<XROrigin>();
        if (!origin || !origin.Camera || !origin.CameraFloorOffsetObject) throw new InvalidOperationException("Missing XR rig camera.");
        var controller = movement.GetComponent<CharacterController>();
        if (!controller) throw new InvalidOperationException("Missing CharacterController.");

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Android lighting and grounded XR preview");
        var lights = transforms.Select(t => t.GetComponent<Light>()).Where(l => l && l.type == LightType.Directional).ToArray();
        var sun = lights.First(l => l.name == "Directional Light");
        foreach (var light in lights)
        {
            Undo.RecordObject(light, "Neutral daylight");
            light.enabled = light == sun;
            if (light == sun)
            {
                light.color = Color.white;
                light.useColorTemperature = false;
                light.intensity = 1.1f;
                light.shadows = LightShadows.Soft;
                light.shadowStrength = 0.85f;
                light.shadowBias = 0.05f;
                light.shadowNormalBias = 0.3f;
            }
        }
        RenderSettings.sun = sun;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.42f, 0.45f, 0.5f);
        RenderSettings.ambientEquatorColor = new Color(0.32f, 0.33f, 0.35f);
        RenderSettings.ambientGroundColor = new Color(0.2f, 0.2f, 0.2f);
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.reflectionIntensity = 0.6f;

        foreach (var path in new[] { "Assets/Settings/Project Configuration/Performance URP Config.asset", "Assets/Settings/Project Configuration/Quality URP Config.asset" })
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            Undo.RecordObject(pipeline, "Android shadow quality");
            var settings = new SerializedObject(pipeline);
            Set(settings, "m_MainLightShadowsSupported", true);
            Set(settings, "m_MainLightShadowmapResolution", path.Contains("Performance") ? 2048 : 4096);
            Set(settings, "m_ShadowDistance", path.Contains("Performance") ? 80f : 120f);
            Set(settings, "m_ShadowCascadeCount", path.Contains("Performance") ? 2 : 4);
            Set(settings, "m_Cascade2Split", 0.3f);
            Set(settings, "m_ShadowDepthBias", 0.5f);
            Set(settings, "m_ShadowNormalBias", 0.3f);
            Set(settings, "m_MSAA", 4);
            Set(settings, "m_RenderScale", 1f);
            Set(settings, "m_SupportsHDR", true);
            Set(settings, "m_SoftShadowsSupported", true);
            settings.ApplyModifiedProperties();
            EditorUtility.SetDirty(pipeline);
        }
        QualitySettings.shadows = UnityEngine.ShadowQuality.All;
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        QualitySettings.lodBias = 1f;
        QualitySettings.globalTextureMipmapLimit = 0;

        int collidersAdded = 0;
        foreach (var t in transforms)
        {
            var renderer = t.GetComponent<MeshRenderer>();
            var mesh = t.GetComponent<MeshFilter>();
            if (!renderer || !mesh || !mesh.sharedMesh) continue;
            // Only observed ground/road/pavement materials near ground height.
            var ground = renderer.bounds.max.y < 8f && renderer.sharedMaterials.Any(m => m &&
                (m.name == "Material.027" || m.name == "Material.028" || m.name == "Material.015" || m.name == "Material.029"));
            if (ground && !t.GetComponent<Collider>())
            {
                var collider = Undo.AddComponent<MeshCollider>(t.gameObject);
                collider.sharedMesh = mesh.sharedMesh;
                collider.convex = false;
                PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
                collidersAdded++;
            }
            if (ground)
            {
                Undo.RecordObject(renderer, "Ground receives shadows");
                renderer.receiveShadows = true;
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
        }
        var grid = transforms.First(t => t.name == "Grid").GetComponent<BoxCollider>();
        Undo.RecordObject(grid, "Give fallback ground thickness");
        grid.size = new Vector3(grid.size.x, 0.5f, grid.size.z);
        grid.center = new Vector3(grid.center.x, -0.25f, grid.center.z);
        Physics.SyncTransforms();

        var camera = origin.Camera;
        var cameraPosition = camera.transform.position;
        if (!Physics.Raycast(cameraPosition, Vector3.down, out var hit, 20f, ~0, QueryTriggerInteraction.Ignore))
            throw new InvalidOperationException("No walkable surface under camera; rig left untouched.");
        var offset = origin.CameraFloorOffsetObject.transform;
        Undo.RecordObjects(new UnityEngine.Object[] { origin, origin.transform, offset, camera, camera.transform, controller, movement }, "Align camera and body over road");
        float heading = camera.transform.eulerAngles.y;
        controller.enabled = false;
        origin.transform.SetPositionAndRotation(new Vector3(cameraPosition.x, hit.point.y + 0.05f, cameraPosition.z), Quaternion.Euler(0, heading, 0));
        origin.CameraYOffset = 1.7f;
        offset.localPosition = new Vector3(0, 1.7f, 0);
        offset.localRotation = Quaternion.identity;
        camera.transform.localPosition = Vector3.zero;
        camera.transform.localRotation = Quaternion.identity;
        controller.height = 1.8f;
        controller.center = new Vector3(0, 0.9f, 0);
        controller.radius = 0.25f;
        controller.skinWidth = 0.03f;
        controller.stepOffset = 0.3f;
        controller.enabled = true;
        movement.allowVerticalMovement = false;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 500f;
        camera.allowHDR = true;
        camera.allowMSAA = true;
        camera.allowDynamicResolution = false;
        var cameraData = camera.GetUniversalAdditionalCameraData();
        Undo.RecordObject(cameraData, "Neutral tonemapping");
        cameraData.renderPostProcessing = true;
        cameraData.antialiasing = AntialiasingMode.None; // Pipeline MSAA avoids blurring textures.

        const string profilePath = "Assets/Settings/Emergency Daylight Volume.asset";
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
        if (!profile)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.Neutral);
            AssetDatabase.AddObjectToAsset(tone, profile);
            EditorUtility.SetDirty(profile);
        }
        var volumeObject = transforms.FirstOrDefault(t => t.name == "Emergency Daylight Volume");
        Volume volume;
        if (!volumeObject)
        {
            var go = new GameObject("Emergency Daylight Volume");
            Undo.RegisterCreatedObjectUndo(go, "Daylight volume");
            volume = go.AddComponent<Volume>();
        }
        else volume = volumeObject.GetComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 10;
        volume.sharedProfile = profile;
        foreach (var obj in new UnityEngine.Object[] { origin, origin.transform, offset, camera, camera.transform, controller, movement, cameraData })
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
            EditorUtility.SetDirty(obj);
        }
        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene);
        EmergencySceneAudit.Write();
        File.WriteAllText("Library/EmergencySceneSetup.result", $"OK: added {collidersAdded} ground colliders; spawn ground={hit.collider.name} at {hit.point}; camera={camera.transform.position}; sun={sun.intensity}, {sun.shadows}.");
    }

    static void Set(SerializedObject obj, string name, bool value) { obj.FindProperty(name).boolValue = value; }
    static void Set(SerializedObject obj, string name, int value) { obj.FindProperty(name).intValue = value; }
    static void Set(SerializedObject obj, string name, float value) { obj.FindProperty(name).floatValue = value; }
}
