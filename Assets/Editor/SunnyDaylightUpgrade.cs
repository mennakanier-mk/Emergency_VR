using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// One-shot high-quality daylight pass for Emergency_VR (Quest / Android standalone, URP 17 / Unity 6.3).
///
/// Tools > Emergency VR > Apply Sunny Daylight (High Quality)
///
/// What it does:
///   1. Collapses the 4 competing directional lights down to a single warm afternoon sun with soft shadows.
///   2. Disables MultiSunBlender so nothing re-blends the sun back to orange at Play.
///   3. Builds a procedural warm-afternoon skybox + skybox ambient + warm distance haze.
///   4. Fills the (currently empty) "Emergency Daylight Volume" profile with a VR-safe grade:
///      Neutral tonemap, warm white balance, bloom, split-tone. No vignette / DoF / motion blur in VR.
///   5. Raises both URP configs as far as Quest can carry: sharper main-light shadows via a tighter
///      shadow distance, high soft-shadow filtering, per-pixel additional lights, probe blending,
///      HDR color grading.
///   6. Bumps the active QualitySettings level (texture / aniso / LOD / skinning).
///
/// Re-runnable and Undo-able. Tuning lives in the CONFIG block below.
/// </summary>
public static class SunnyDaylightUpgrade
{
    // ---------------------------------------------------------------- CONFIG

    const string kScenePath = "Assets/Scenes/SampleScene.unity";
    const string kVolumeProfilePath = "Assets/Settings/Emergency Daylight Volume.asset";
    const string kSkyboxPath = "Assets/Settings/Sunny Afternoon Sky.mat";
    const string kPerfUrpPath = "Assets/Settings/Project Configuration/Performance URP Config.asset";
    const string kQualityUrpPath = "Assets/Settings/Project Configuration/Quality URP Config.asset";

    // Sun: warm golden afternoon.
    const float kSunPitch = 30f;    // degrees above horizon-ish; low => long shadows
    const float kSunYaw = -42f;
    const float kSunKelvin = 4700f; // warm white
    const float kSunIntensity = 1.5f;
    const float kSunShadowStrength = 0.8f;

    // Quest budget. Halving shadow distance doubles shadow texel density at the same map size,
    // which buys far more perceived sharpness than a 4096 map would at 80m.
    const int kPerfShadowRes = 2048;
    const float kPerfShadowDistance = 50f;
    const int kQualityShadowRes = 4096;
    const float kQualityShadowDistance = 110f;

    // Raise to 1.15f-1.2f if you have headroom; costs ~30% fill. Left at native by default.
    const float kPerfRenderScale = 1.0f;

    // ---------------------------------------------------------------- ENTRY

    [MenuItem("Tools/Emergency VR/Apply Sunny Daylight (High Quality)")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play mode first.");

        var scene = SceneManager.GetActiveScene();
        if (scene.path != kScenePath)
            throw new InvalidOperationException($"Open {kScenePath} first (active scene is '{scene.path}').");

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Apply sunny daylight");
        int group = Undo.GetCurrentGroup();

        var sun = ConfigureLights(scene);
        var skybox = BuildSkybox();
        ConfigureEnvironment(sun, skybox);
        BuildVolumeProfile();
        TuneUrpAsset(kPerfUrpPath, performance: true);
        TuneUrpAsset(kQualityUrpPath, performance: false);
        TuneQualitySettings();
        WarnAboutConflicts(scene);

        Undo.CollapseUndoOperations(group);

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        DynamicGI.UpdateEnvironment();
        EditorApplication.ExecuteMenuItem("File/Save Project"); // flushes ProjectSettings/QualitySettings.asset

        Debug.Log("[SunnyDaylightUpgrade] Done. Single warm sun, procedural afternoon sky, graded volume, " +
                  "Quest-tuned URP configs. Reopen Window > Rendering > Lighting and re-bake only if you " +
                  "actually use baked lightmaps.");
    }

    // ---------------------------------------------------------------- LIGHTS

    static Light ConfigureLights(Scene scene)
    {
        var all = scene.GetRootGameObjects()
                       .SelectMany(r => r.GetComponentsInChildren<Light>(true))
                       .ToArray();

        var directionals = all.Where(l => l.type == LightType.Directional).ToArray();
        if (directionals.Length == 0)
            throw new InvalidOperationException("No directional light in the scene.");

        // Prefer whatever is already RenderSettings.sun so existing references stay valid,
        // otherwise the object literally named "Directional Light".
        Light sun = null;
        if (RenderSettings.sun != null && directionals.Contains(RenderSettings.sun))
            sun = RenderSettings.sun;
        sun ??= directionals.FirstOrDefault(l => l.gameObject.name == "Directional Light") ?? directionals[0];

        foreach (var light in directionals)
        {
            Undo.RecordObject(light, "Sunny daylight");
            if (light != sun)
            {
                // Three directional lights at intensity 8 fighting each other is what was
                // flattening the scene. Only one may be the main light in URP anyway.
                light.enabled = false;
                EditorUtility.SetDirty(light);
                continue;
            }

            light.enabled = true;
            light.color = Color.white;
            light.useColorTemperature = true;
            light.colorTemperature = kSunKelvin;
            light.intensity = kSunIntensity;
            light.bounceIntensity = 1.1f;
            light.lightmapBakeType = LightmapBakeType.Realtime;

            light.shadows = LightShadows.Soft;
            light.shadowStrength = kSunShadowStrength;
            light.shadowBias = 0.04f;
            light.shadowNormalBias = 0.35f;
            light.shadowNearPlane = 0.15f;
            light.shadowResolution = UnityEngine.Rendering.LightShadowResolution.VeryHigh;

            light.renderMode = LightRenderMode.ForcePixel;
            light.cullingMask = ~0;
            EditorUtility.SetDirty(light);

            Undo.RecordObject(light.transform, "Sun angle");
            light.transform.rotation = Quaternion.Euler(kSunPitch, kSunYaw, 0f);
            EditorUtility.SetDirty(light.transform);
        }

        // MultiSunBlender rewrites sun color/intensity/rotation in Start(); it has to go quiet.
        foreach (var blender in scene.GetRootGameObjects()
                                     .SelectMany(r => r.GetComponentsInChildren<MultiSunBlender>(true)))
        {
            Undo.RecordObject(blender, "Disable MultiSunBlender");
            blender.enabled = false;
            blender.masterSunLight = sun;
            EditorUtility.SetDirty(blender);
        }

        return sun;
    }

    // ---------------------------------------------------------------- SKY + ENVIRONMENT

    static Material BuildSkybox()
    {
        var shader = Shader.Find("Skybox/Procedural");
        if (shader == null)
        {
            Debug.LogWarning("[SunnyDaylightUpgrade] Skybox/Procedural not found; keeping the existing skybox.");
            return RenderSettings.skybox;
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(kSkyboxPath);
        if (mat == null)
        {
            mat = new Material(shader) { name = "Sunny Afternoon Sky" };
            AssetDatabase.CreateAsset(mat, kSkyboxPath);
        }
        else if (mat.shader != shader)
        {
            mat.shader = shader;
        }

        Undo.RecordObject(mat, "Sunny sky");

        // High-quality sun disk; the procedural sky reads RenderSettings.sun for its direction.
        mat.SetFloat("_SunDisk", 2f);
        mat.DisableKeyword("_SUNDISK_NONE");
        mat.DisableKeyword("_SUNDISK_SIMPLE");
        mat.EnableKeyword("_SUNDISK_HIGH_QUALITY");

        mat.SetFloat("_SunSize", 0.045f);
        mat.SetFloat("_SunSizeConvergence", 3f);
        mat.SetFloat("_AtmosphereThickness", 1.35f); // thicker => warmer, hazier afternoon
        mat.SetColor("_SkyTint", new Color(0.62f, 0.55f, 0.47f, 1f));
        mat.SetColor("_GroundColor", new Color(0.42f, 0.38f, 0.33f, 1f));
        mat.SetFloat("_Exposure", 1.35f);

        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void ConfigureEnvironment(Light sun, Material skybox)
    {
        // RenderSettings is a static façade over the scene's RenderSettings object, so there is
        // nothing to Undo.RecordObject here; the scene save below is what persists these.
        RenderSettings.sun = sun;
        if (skybox != null) RenderSettings.skybox = skybox;

        // Ambient straight off the sky: warm bounce up top, earthy fill below, for free.
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1.0f;

        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        RenderSettings.defaultReflectionResolution = 256;
        RenderSettings.reflectionIntensity = 1.0f;
        RenderSettings.reflectionBounces = 1;

        // Warm haze: cheap on mobile, and it is what sells distance in a headset.
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(0.85f, 0.77f, 0.64f, 1f);
        RenderSettings.fogDensity = 0.0035f;

        RenderSettings.haloStrength = 0.35f;
        RenderSettings.flareStrength = 0.8f;
    }

    // ---------------------------------------------------------------- POST-PROCESSING

    static void BuildVolumeProfile()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(kVolumeProfilePath);
        if (profile == null)
        {
            Debug.LogWarning($"[SunnyDaylightUpgrade] {kVolumeProfilePath} missing; skipping the grade.");
            return;
        }

        Undo.RecordObject(profile, "Daylight grade");

        var tonemap = GetOrAdd<Tonemapping>(profile);
        // Neutral, not ACES: ACES crushes midtones and reads as dim through a headset lens.
        Set(tonemap.mode, TonemappingMode.Neutral);

        var color = GetOrAdd<ColorAdjustments>(profile);
        Set(color.postExposure, 0.12f);
        Set(color.contrast, 8f);
        Set(color.saturation, 12f);
        Set(color.hueShift, 0f);
        Set(color.colorFilter, new Color(1f, 0.965f, 0.90f, 1f));

        var wb = GetOrAdd<WhiteBalance>(profile);
        Set(wb.temperature, 14f);
        Set(wb.tint, 3f);

        var bloom = GetOrAdd<Bloom>(profile);
        Set(bloom.threshold, 1.05f);
        Set(bloom.intensity, 0.45f);
        Set(bloom.scatter, 0.62f);
        Set(bloom.clamp, 24f);
        Set(bloom.tint, new Color(1f, 0.93f, 0.82f, 1f));
        Set(bloom.highQualityFiltering, false);   // keep off on Quest
        Set(bloom.downscale, BloomDownscaleMode.Half);
        Set(bloom.maxIterations, 4);
        Set(bloom.dirtIntensity, 0f);

        var split = GetOrAdd<ShadowsMidtonesHighlights>(profile);
        Set(split.shadows, new Vector4(0.94f, 0.97f, 1.08f, 0f));    // cool shadows
        Set(split.midtones, new Vector4(1f, 1f, 1f, 0f));
        Set(split.highlights, new Vector4(1.06f, 1.01f, 0.93f, 0f)); // warm highlights
        Set(split.shadowsStart, 0f);
        Set(split.shadowsEnd, 0.3f);
        Set(split.highlightsStart, 0.55f);
        Set(split.highlightsEnd, 1f);

        // Explicitly neutralise anything that is uncomfortable or nauseating in VR.
        Disable<Vignette>(profile);
        Disable<DepthOfField>(profile);
        Disable<MotionBlur>(profile);
        Disable<ChromaticAberration>(profile);
        Disable<LensDistortion>(profile);
        Disable<FilmGrain>(profile);
        Disable<PaniniProjection>(profile);

        EditorUtility.SetDirty(profile);
    }

    static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
    {
        if (profile.TryGet<T>(out var existing))
        {
            existing.active = true;
            return existing;
        }

        var comp = profile.Add<T>(true);
        comp.name = typeof(T).Name;
        comp.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
        AssetDatabase.AddObjectToAsset(comp, profile);
        comp.active = true;
        return comp;
    }

    static void Disable<T>(VolumeProfile profile) where T : VolumeComponent
    {
        if (profile.TryGet<T>(out var comp))
            comp.active = false;
    }

    static void Set<T>(VolumeParameter<T> param, T value)
    {
        param.overrideState = true;
        param.value = value;
    }

    // ---------------------------------------------------------------- URP ASSETS

    static void TuneUrpAsset(string path, bool performance)
    {
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        if (pipeline == null)
        {
            Debug.LogWarning($"[SunnyDaylightUpgrade] Missing URP asset at {path}.");
            return;
        }

        Undo.RecordObject(pipeline, "URP quality");
        var so = new SerializedObject(pipeline);

        SetProp(so, "m_SupportsHDR", true);
        SetProp(so, "m_HDRColorBufferPrecision", 0);          // R11G11B10, cheap on tile memory
        SetProp(so, "m_MSAA", 4);
        SetProp(so, "m_RenderScale", performance ? kPerfRenderScale : 1.0f);
        SetProp(so, "m_UseSRPBatcher", true);

        SetProp(so, "m_MainLightRenderingMode", 1);           // PerPixel
        SetProp(so, "m_MainLightShadowsSupported", true);
        SetProp(so, "m_MainLightShadowmapResolution", performance ? kPerfShadowRes : kQualityShadowRes);

        SetProp(so, "m_AdditionalLightsRenderingMode", 1);    // PerPixel: headlights / beacons now light the world
        SetProp(so, "m_AdditionalLightsPerObjectLimit", performance ? 4 : 8);
        SetProp(so, "m_AdditionalLightShadowsSupported", !performance);
        SetProp(so, "m_AdditionalLightsShadowmapResolution", performance ? 512 : 2048);

        SetProp(so, "m_AnyShadowsSupported", true);
        SetProp(so, "m_SoftShadowsSupported", true);
        SetProp(so, "m_SoftShadowQuality", 2);                // High
        SetProp(so, "m_ShadowDistance", performance ? kPerfShadowDistance : kQualityShadowDistance);
        SetProp(so, "m_ShadowCascadeCount", performance ? 2 : 4);
        SetProp(so, "m_Cascade2Split", 0.25f);
        SetProp(so, "m_CascadeBorder", 0.2f);
        SetProp(so, "m_ShadowDepthBias", 0.4f);
        SetProp(so, "m_ShadowNormalBias", 0.35f);
        SetProp(so, "m_ConservativeEnclosingSphere", true);

        SetProp(so, "m_ReflectionProbeBlending", true);
        SetProp(so, "m_ReflectionProbeBoxProjection", true);
        SetProp(so, "m_ReflectionProbeAtlas", true);

        SetProp(so, "m_ColorGradingMode", 1);                 // HDR grading; needs SupportsHDR, set above
        SetProp(so, "m_ColorGradingLutSize", 32);
        SetProp(so, "m_SupportsLightCookies", true);
        SetProp(so, "m_MixedLightingSupported", true);
        SetProp(so, "m_EnableLODCrossFade", !performance);

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(pipeline);
    }

    static void SetProp(SerializedObject so, string name, object value)
    {
        var p = so.FindProperty(name);
        if (p == null)
        {
            Debug.LogWarning($"[SunnyDaylightUpgrade] URP property '{name}' not found on {so.targetObject.name} " +
                             "(URP version drift) - skipped.");
            return;
        }

        switch (value)
        {
            case bool b: p.boolValue = b; break;
            case int i: p.intValue = i; break;
            case float f: p.floatValue = f; break;
            default: Debug.LogWarning($"[SunnyDaylightUpgrade] Unsupported type for '{name}'."); break;
        }
    }

    // ---------------------------------------------------------------- QUALITY SETTINGS

    static void TuneQualitySettings()
    {
        QualitySettings.globalTextureMipmapLimit = 0;                       // full-res textures
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        QualitySettings.skinWeights = SkinWeights.FourBones;
        QualitySettings.lodBias = 1.6f;                                     // hold LOD0 further out
        QualitySettings.realtimeReflectionProbes = true;
        QualitySettings.billboardsFaceCameraPosition = true;
        QualitySettings.particleRaycastBudget = 64;
        QualitySettings.vSyncCount = 0;                                     // XR compositor owns pacing

        var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset").FirstOrDefault();
        if (asset != null) EditorUtility.SetDirty(asset);
    }

    // ---------------------------------------------------------------- SANITY CHECKS

    static void WarnAboutConflicts(Scene scene)
    {
        // These two re-run themselves on every domain reload and re-save the scene.
        foreach (var path in new[] { "Assets/Editor/FixSceneLightingAndClouds.cs",
                                     "Assets/Editor/DustCloudParticleSetup.cs" })
        {
            if (System.IO.File.Exists(path))
                Debug.LogWarning($"[SunnyDaylightUpgrade] {System.IO.Path.GetFileName(path)} has an " +
                                 "[InitializeOnLoadMethod] that auto-runs and saves SampleScene on every script " +
                                 "reload, re-authoring the dust clouds. Run Tools > Emergency VR > Remove Dust " +
                                 "Cloud System to be rid of it.");
        }

        var clouds = GameObject.Find("Global_Dust_Cloud_System");
        if (clouds != null && clouds.activeInHierarchy)
        {
            Debug.LogWarning("[SunnyDaylightUpgrade] 'Global_Dust_Cloud_System' is active: 120 world-space " +
                             "billboards at 25-50m size and 75% alpha. On Quest that is a large overdraw bill and " +
                             "it will wash out the sun.");
        }

        var extraDirectional = scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<Light>(true))
            .Count(l => l.type == LightType.Directional && l.enabled);
        if (extraDirectional != 1)
            Debug.LogWarning($"[SunnyDaylightUpgrade] {extraDirectional} directional lights still enabled; expected 1.");
    }
}
