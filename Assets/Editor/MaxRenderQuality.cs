using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Pushes the project to its sharpest sensible settings.
///
///   Tools > Emergency VR > Max Render Quality (Settings)   - instant
///   Tools > Emergency VR > Max Render Quality (Textures)   - slow, reimports art
///
/// One thing this CANNOT fix, because it is not a quality setting: the Scale slider in the
/// Game view toolbar. Unity renders the Game view at (window size / Scale) and then magnifies
/// the result, so at 4.5x you are looking at roughly a third of the pixels blown up. It is a
/// magnifying glass over a finished image. Put Scale back to 1x to judge sharpness, and use
/// the Scene view when you want to inspect detail up close.
///
/// What is actually worth changing, and is changed here:
///
///   Anisotropic filtering   Off / Per-Texture -> Forced On. This is the big one for a street
///                           scene: road and pavement textures seen at a grazing angle turn to
///                           mush without it, which reads as "low quality" more than resolution
///                           ever does.
///   Texture mipmap limit    Any half-res limit -> 0 (full res). A limit of 1 silently throws
///                           away half of every texture's resolution.
///   MSAA                    -> 4x, and the eye render textures now match it. On the phone the
///                           edges of buildings against the sky are the first thing that looks
///                           cheap.
///   LOD bias                -> 2. Models swap to their low-detail versions at twice the
///                           distance, so nothing pops down while you are looking at it.
///   URP render scale        -> 1.0, with no upscaler in the way.
///   Shadows                 -> 2048 map, 4 cascades, soft.
///
/// All of this costs frame rate on a phone. If the build starts to stutter, the first things to
/// give back are MSAA (4 -> 2) and the shadow cascade count (4 -> 2).
/// </summary>
public static class MaxRenderQuality
{
    [MenuItem("Tools/Emergency VR/Max Render Quality (Settings)")]
    public static void ApplySettings()
    {
        var log = new StringBuilder("[MaxRenderQuality] settings\n");

        ApplyQualityLevels(log);
        ApplyUrpAssets(log);

        AssetDatabase.SaveAssets();
        EditorApplication.ExecuteMenuItem("File/Save Project");

        log.AppendLine();
        log.AppendLine("  Reminder: the Game view Scale slider magnifies an already-rendered " +
                       "image. Set it to 1x before judging sharpness.");

        Debug.Log(log.ToString());
    }

    // ---------------------------------------------------------------- quality levels

    static void ApplyQualityLevels(StringBuilder log)
    {
        int original = QualitySettings.GetQualityLevel();
        string[] names = QualitySettings.names;

        for (int i = 0; i < names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);

            var before = $"aniso {QualitySettings.anisotropicFiltering}, " +
                         $"mipLimit {QualitySettings.globalTextureMipmapLimit}, " +
                         $"AA {QualitySettings.antiAliasing}x, " +
                         $"lodBias {QualitySettings.lodBias:0.0}";

            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            QualitySettings.globalTextureMipmapLimit = 0;
            QualitySettings.antiAliasing = 4;
            QualitySettings.lodBias = 2f;
            QualitySettings.maximumLODLevel = 0;
            QualitySettings.realtimeReflectionProbes = true;
            QualitySettings.softVegetation = true;
            QualitySettings.billboardsFaceCameraPosition = true;

            log.AppendLine($"  [{names[i]}] {before}");
            log.AppendLine($"  [{names[i]}] -> aniso ForceEnable, mipLimit 0, AA 4x, lodBias 2.0");
        }

        QualitySettings.SetQualityLevel(original, true);
        log.AppendLine($"  Active quality level: {names[original]}");
    }

    // ---------------------------------------------------------------- URP assets

    static void ApplyUrpAssets(StringBuilder log)
    {
        // "Assets" only. An unscoped FindAssets also returns pipeline assets that ship inside
        // read-only packages - com.unity.xr.androidxr-openxr carries one - and writing to those
        // makes Unity warn that an immutable package was altered, for a file the project never
        // renders with.
        var guids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset", new[] { "Assets" });
        if (guids.Length == 0)
        {
            log.AppendLine("  URP: no pipeline assets found.");
            return;
        }

        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null) continue;

            var so = new SerializedObject(asset);

            log.AppendLine($"  URP: {System.IO.Path.GetFileName(path)}");

            SetFloat(so, "m_RenderScale", 1f, log);
            SetInt(so, "m_MSAA", 4, log);

            // 0 = Automatic. An upscaler only matters below render scale 1, and at 1 it can
            // still soften the image on some drivers.
            SetInt(so, "m_UpscalingFilter", 0, log);

            SetInt(so, "m_MainLightShadowmapResolution", 2048, log);
            SetInt(so, "m_AdditionalLightsShadowmapResolution", 2048, log);
            SetInt(so, "m_ShadowCascadeCount", 4, log);
            SetInt(so, "m_SoftShadowQuality", 2, log);
            SetFloat(so, "m_ShadowDistance", 80f, log);

            // Colour banding in a sky gradient is the other thing that reads as "low quality".
            SetBool(so, "m_SupportsHDR", true, log);
            SetInt(so, "m_ColorGradingMode", 1, log);          // High Dynamic Range
            SetInt(so, "m_ColorGradingLutSize", 32, log);

            SetInt(so, "m_AdditionalLightsPerObjectLimit", 8, log);

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
        }
    }

    static void SetInt(SerializedObject so, string prop, int value, StringBuilder log)
    {
        var p = so.FindProperty(prop);
        if (p == null) return;
        if (p.intValue == value) return;
        log.AppendLine($"      {prop,-38} {p.intValue}  ->  {value}");
        p.intValue = value;
    }

    static void SetFloat(SerializedObject so, string prop, float value, StringBuilder log)
    {
        var p = so.FindProperty(prop);
        if (p == null) return;
        if (Mathf.Approximately(p.floatValue, value)) return;
        log.AppendLine($"      {prop,-38} {p.floatValue}  ->  {value}");
        p.floatValue = value;
    }

    static void SetBool(SerializedObject so, string prop, bool value, StringBuilder log)
    {
        var p = so.FindProperty(prop);
        if (p == null) return;
        if (p.boolValue == value) return;
        log.AppendLine($"      {prop,-38} {p.boolValue}  ->  {value}");
        p.boolValue = value;
    }

    // ---------------------------------------------------------------- textures

    /// <summary>
    /// Raises every art texture to 2048 with mipmaps, trilinear filtering and 8x anisotropy,
    /// and gives Android an ASTC 6x6 override. Only ever raises - a texture already at 4096
    /// is left alone.
    ///
    /// This reimports, which on a scene this size takes minutes. Separate menu item on purpose.
    /// </summary>
    [MenuItem("Tools/Emergency VR/Max Render Quality (Textures)")]
    public static void ApplyTextures()
    {
        // The art folders. Package samples, TMP and the VR template are left as shipped.
        string[] folders =
        {
            "Assets/map",
            "Assets/matrial",
            "Assets/charcter",
            "Assets/hands",
            "Assets/hourse and donkey",
            "Assets/AllSkyFree"
        };

        var live = new List<string>();
        foreach (var f in folders)
            if (AssetDatabase.IsValidFolder(f)) live.Add(f);

        if (live.Count == 0)
        {
            Debug.LogWarning("[MaxRenderQuality] None of the art folders were found. Nothing done.");
            return;
        }

        var guids = AssetDatabase.FindAssets("t:Texture2D", live.ToArray());

        if (!EditorUtility.DisplayDialog(
                "Max Render Quality",
                $"Reimport {guids.Length} textures at up to 2048 with 8x anisotropy?\n\n" +
                "This can take several minutes and will grow the APK.",
                "Do it", "Cancel"))
            return;

        int changed = 0;

        try
        {
            AssetDatabase.StartAssetEditing();

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);

                if (EditorUtility.DisplayCancelableProgressBar(
                        "Max Render Quality", path, (float)i / Mathf.Max(1, guids.Length)))
                    break;

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                bool dirty = false;

                if (importer.maxTextureSize < 2048)
                {
                    importer.maxTextureSize = 2048;
                    dirty = true;
                }

                // Mipmaps are what stop distant road markings crawling and sparkling. Off is
                // only correct for UI sprites, which are not in these folders.
                if (importer.textureType == TextureImporterType.Default && !importer.mipmapEnabled)
                {
                    importer.mipmapEnabled = true;
                    dirty = true;
                }

                if (importer.mipmapEnabled && importer.anisoLevel < 8)
                {
                    importer.anisoLevel = 8;
                    dirty = true;
                }

                if (importer.filterMode != FilterMode.Trilinear)
                {
                    importer.filterMode = FilterMode.Trilinear;
                    dirty = true;
                }

                var android = importer.GetPlatformTextureSettings("Android");
                if (!android.overridden ||
                    android.maxTextureSize < 2048 ||
                    android.format != TextureImporterFormat.ASTC_6x6)
                {
                    android.overridden = true;
                    android.maxTextureSize = Mathf.Max(2048, android.maxTextureSize);
                    android.format = TextureImporterFormat.ASTC_6x6;
                    android.compressionQuality = 100;
                    android.textureCompression = TextureImporterCompression.CompressedHQ;
                    importer.SetPlatformTextureSettings(android);
                    dirty = true;
                }

                if (!dirty) continue;

                importer.SaveAndReimport();
                changed++;
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
        }

        Debug.Log($"[MaxRenderQuality] textures: {changed} of {guids.Length} raised to 2048 / " +
                  "8x aniso / ASTC 6x6 for Android. The rest were already at least that good.");
    }

    // ---------------------------------------------------------------- report

    [MenuItem("Tools/Emergency VR/Report Render Quality")]
    public static void Report()
    {
        var sb = new StringBuilder("[MaxRenderQuality] current\n");
        sb.AppendLine($"  Quality level          {QualitySettings.names[QualitySettings.GetQualityLevel()]}");
        sb.AppendLine($"  Anisotropic filtering  {QualitySettings.anisotropicFiltering}");
        sb.AppendLine($"  Texture mipmap limit   {QualitySettings.globalTextureMipmapLimit}  (0 = full resolution)");
        sb.AppendLine($"  Anti-aliasing          {QualitySettings.antiAliasing}x");
        sb.AppendLine($"  LOD bias               {QualitySettings.lodBias:0.0}");
        sb.AppendLine($"  Realtime reflections   {QualitySettings.realtimeReflectionProbes}");

        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset",
                                                     new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null) continue;

            var so = new SerializedObject(asset);
            sb.AppendLine($"  {System.IO.Path.GetFileName(path)}");
            sb.AppendLine($"      render scale       {Read(so, "m_RenderScale")}");
            sb.AppendLine($"      MSAA               {Read(so, "m_MSAA")}");
            sb.AppendLine($"      shadow map         {Read(so, "m_MainLightShadowmapResolution")}");
            sb.AppendLine($"      cascades           {Read(so, "m_ShadowCascadeCount")}");
        }

        sb.AppendLine();
        sb.AppendLine("  Game view Scale is NOT a quality setting - it magnifies the rendered " +
                      "image. Keep it at 1x.");

        Debug.Log(sb.ToString());
    }

    static string Read(SerializedObject so, string prop)
    {
        var p = so.FindProperty(prop);
        if (p == null) return "(absent)";
        return p.propertyType == SerializedPropertyType.Float
            ? p.floatValue.ToString("0.###")
            : p.intValue.ToString();
    }
}
