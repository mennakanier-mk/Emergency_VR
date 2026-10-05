using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Quality > Fix The Lighting
///   Tools > Emergency VR > Quality > Match Sun To Sky      (separate - it changes the look)
///
/// The lighting pass that does not need a bake.
///
/// Without baked lightmaps there is no bounce light and no contact darkening in corners - that
/// much is simply not available, and no amount of tuning invents it. What IS available, and what
/// this fixes, is everything that was actively working against the picture:
///
///   Two spot lights at intensity 5000. Whatever they were meant to be, at that value every
///   surface they touch clips to white, and the bloom pass then smears that white over the
///   neighbouring pixels. A blown-out highlight destroys more detail than a dim one, because
///   clipped pixels carry no information at all - they are all exactly the same white.
///
///   Two point lights at intensity 50 over a range of 0.3 m. A range that short does not light
///   a scene, it makes a hot dot; the intensity is fighting a range that cannot deliver it.
///
///   Four shadow cascades over 80 m. Cascades split the shadow map by distance, so four of them
///   spend three quarters of a 2048 map on ground you are not looking at, leaving the near
///   shadows - the only ones you can actually see in a viewer - with a quarter of the resolution
///   they would get from two. It is slower AND blurrier. That combination is rare enough to be
///   worth saying out loud.
///
/// Intensity is read in whatever unit the light is set to. URP can express a light in Lumen or in
/// its own arbitrary scale, and the same number means wildly different things in each - so the
/// unit is resolved per light rather than assumed, which is the difference between fixing a light
/// and breaking a correct one.
/// </summary>
public static class VRLightingFix
{
    // Targets, per light unit. A headlight really is about 1000-1500 lumens; in URP's arbitrary
    // scale the same light reads about 8.
    const float kSpotLumen   = 1400f;
    const float kSpotDefault = 8f;

    const float kPointLumen   = 500f;
    const float kPointDefault = 4f;

    const float kSirenRange = 3.5f;

    [MenuItem("Tools/Emergency VR/Quality/Fix The Lighting")]
    public static void Fix()
    {
        var sb = new StringBuilder("[Quality] lighting fix\n\n");

        FixLights(sb);
        FixAmbient(sb);
        FixShadowSettings(sb);
        FixBloom(sb);
        ReportSunVersusSky(sb);

        var scene = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);

        sb.AppendLine();
        sb.AppendLine("  Scene marked dirty - press Ctrl+S to keep it.");

        Debug.Log(sb.ToString());
    }

    // ------------------------------------------------------------------ lights

    static void FixLights(StringBuilder sb)
    {
        sb.AppendLine("LIGHTS");

        var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include,
                                                     FindObjectsSortMode.None);

        int touched = 0;

        foreach (var l in lights)
        {
            if (!l.isActiveAndEnabled) continue;
            if (l.type == LightType.Directional) continue;      // handled separately

            bool lumen = IsLumen(l);
            float before = l.intensity;
            float beforeRange = l.range;
            var beforeShadows = l.shadows;

            float target = l.type == LightType.Spot
                ? (lumen ? kSpotLumen : kSpotDefault)
                : (lumen ? kPointLumen : kPointDefault);

            // Only pull a light DOWN. One that is already modest was deliberate, and overwriting
            // it would undo work rather than fix a fault.
            if (before <= target * 1.25f && beforeRange >= 1f && beforeShadows == LightShadows.None)
            {
                sb.AppendLine($"  {l.name,-24} {l.type,-6} left alone (int {before:0.0}, range {beforeRange:0.0})");
                continue;
            }

            Undo.RecordObject(l, "Fix lighting");

            if (before > target) l.intensity = target;

            // A range under a metre cannot light anything that is not touching it.
            if (l.range < 1f) l.range = kSirenRange;

            // Extra-light shadows are switched off in the live pipeline asset, so these cost
            // nothing - but they are also doing nothing, and a setting that lies about what it
            // does is the one that wastes an afternoon later.
            l.shadows = LightShadows.None;

            EditorUtility.SetDirty(l);
            touched++;

            sb.AppendLine($"  {l.name,-24} {l.type,-6} " +
                          $"int {before:0.0} -> {l.intensity:0.0}" +
                          $"{(lumen ? " lm" : "")}   " +
                          $"range {beforeRange:0.0} -> {l.range:0.0}   " +
                          $"shadows {beforeShadows} -> None   " +
                          $"[on '{Parent(l)}']");
        }

        // The sun: the one light that should keep its shadows.
        var sun = lights.FirstOrDefault(l => l.isActiveAndEnabled && l.type == LightType.Directional);

        if (sun != null)
        {
            sb.AppendLine();
            sb.AppendLine($"  sun                      '{sun.name}' int {sun.intensity:0.00}, " +
                          $"elevation {NormalizeAngle(sun.transform.eulerAngles.x):0}deg, " +
                          $"shadows {sun.shadows}   (left as it is)");

            int extraSuns = lights.Count(l => l.isActiveAndEnabled && l.type == LightType.Directional) - 1;

            if (extraSuns > 0)
            {
                sb.AppendLine($"  WARNING: {extraSuns} more directional light(s) are also on. Two suns " +
                              "means two sets of shadows falling in different directions, which reads " +
                              "as the scene having no single light source at all.");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"  {touched} light(s) changed");
        sb.AppendLine();
    }

    /// <summary>
    /// Which unit this light's intensity is expressed in.
    ///
    /// Asked through SerializedObject rather than a typed property because the field has moved
    /// between URP versions, and a version that does not have it should mean "assume the
    /// arbitrary scale", not a compile error.
    /// </summary>
    static bool IsLumen(Light l)
    {
        var data = l.GetComponent<UniversalAdditionalLightData>();
        if (data == null) return false;

        var so = new SerializedObject(data);
        var p = so.FindProperty("m_LightUnit");
        if (p == null) return false;

        // LightUnit: Lumen = 0, Candela = 1, Lux = 2, Nits = 3, Ev100 = 4
        return p.intValue == 0;
    }

    static string Parent(Light l)
    {
        return l.transform.parent != null ? l.transform.parent.name : "(scene root)";
    }

    static float NormalizeAngle(float a)
    {
        a = Mathf.Repeat(a, 360f);
        return a > 180f ? a - 360f : a;
    }

    // ------------------------------------------------------------------ ambient

    static void FixAmbient(StringBuilder sb)
    {
        sb.AppendLine("AMBIENT");

        // RenderSettings has no object to hand to Undo - the lighting settings live in the scene
        // itself, so this one change is not undoable. It is a single number, written below.
        float before = RenderSettings.ambientIntensity;

        sb.AppendLine($"  source                   {RenderSettings.ambientMode}   " +
                      "(Skybox is right - it gives the sky's own colour from above and the " +
                      "ground's from below, instead of one flat tint from everywhere)");

        // Above 1 the sky fills the shadows faster than the sun can carve them, and the whole
        // scene loses the contrast that tells you which way the light is coming from.
        if (RenderSettings.ambientIntensity > 1.05f)
        {
            RenderSettings.ambientIntensity = 1f;
            sb.AppendLine($"  intensity                {before:0.00} -> 1.00   " +
                          "(over 1 the sky washes out the sun's own shadows)");
        }
        else
        {
            sb.AppendLine($"  intensity                {before:0.00}   (left as it is)");
        }

        sb.AppendLine($"  reflection intensity     {RenderSettings.reflectionIntensity:0.00}");
        sb.AppendLine();
    }

    // ------------------------------------------------------------------ shadows

    static void FixShadowSettings(StringBuilder sb)
    {
        sb.AppendLine("SHADOWS (live pipeline asset)");

        var asset = ActiveAsset();

        if (asset == null)
        {
            sb.AppendLine("  no URP asset resolved - nothing changed");
            sb.AppendLine();
            return;
        }

        var so = new SerializedObject(asset);

        int cascadesBefore = GetInt(so, "m_ShadowCascadeCount");
        float distanceBefore = GetFloat(so, "m_ShadowDistance");

        SetInt(so, "m_ShadowCascadeCount", 2);
        SetFloat(so, "m_ShadowDistance", 45f);

        // With two cascades the split decides where the sharp near map ends. A quarter puts the
        // change-over at about 11 m, past everything the wearer can actually look at closely.
        SetFloat(so, "m_Cascade2Split", 0.25f);

        // Eight extra lights per object, in a scene whose extra lights are two sirens, costs a
        // per-object light loop that finds nothing seven times out of eight.
        int perObjectBefore = GetInt(so, "m_AdditionalLightsPerObjectLimit");
        SetInt(so, "m_AdditionalLightsPerObjectLimit", 4);

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);

        sb.AppendLine($"  asset                    {asset.name}");
        sb.AppendLine($"  cascades                 {cascadesBefore} -> 2      " +
                      "(fewer passes AND a sharper near shadow)");
        sb.AppendLine($"  shadow distance          {distanceBefore:0} -> 45 m");
        sb.AppendLine($"  extra lights per object  {perObjectBefore} -> 4");
        sb.AppendLine();
    }

    static UniversalRenderPipelineAsset ActiveAsset()
    {
        var perLevel = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
        if (perLevel != null) return perLevel;

        return GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
    }

    static int GetInt(SerializedObject so, string p)
    {
        var prop = so.FindProperty(p);
        return prop != null ? prop.intValue : -1;
    }

    static float GetFloat(SerializedObject so, string p)
    {
        var prop = so.FindProperty(p);
        return prop != null ? prop.floatValue : -1f;
    }

    static void SetInt(SerializedObject so, string p, int v)
    {
        var prop = so.FindProperty(p);
        if (prop != null) prop.intValue = v;
    }

    static void SetFloat(SerializedObject so, string p, float v)
    {
        var prop = so.FindProperty(p);
        if (prop != null) prop.floatValue = v;
    }

    // ------------------------------------------------------------------ bloom

    static void FixBloom(StringBuilder sb)
    {
        sb.AppendLine("BLOOM");

        var volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include,
                                                       FindObjectsSortMode.None);

        bool found = false;

        foreach (var v in volumes)
        {
            if (v.sharedProfile == null) continue;

            foreach (var c in v.sharedProfile.components)
            {
                var bloom = c as Bloom;
                if (bloom == null) continue;

                found = true;

                float thresholdBefore = bloom.threshold.value;
                float intensityBefore = bloom.intensity.value;

                Undo.RecordObject(bloom, "Fix bloom");

                // Threshold is the brightness a pixel has to reach before it glows. Below 1 it
                // catches ordinary lit surfaces - a white wall in sunlight - and the whole image
                // goes soft. At 1.1 only genuinely bright things bloom, which is what bloom is
                // for.
                bloom.threshold.overrideState = true;
                bloom.threshold.value = Mathf.Max(1.1f, thresholdBefore);

                bloom.intensity.overrideState = true;
                bloom.intensity.value = Mathf.Min(0.35f, Mathf.Max(0.1f, intensityBefore));

                // High quality filtering is several more blur passes, per eye.
                bloom.highQualityFiltering.overrideState = true;
                bloom.highQualityFiltering.value = false;

                EditorUtility.SetDirty(bloom);
                EditorUtility.SetDirty(v.sharedProfile);

                sb.AppendLine($"  {v.name}");
                sb.AppendLine($"    threshold              {thresholdBefore:0.00} -> {bloom.threshold.value:0.00}");
                sb.AppendLine($"    intensity              {intensityBefore:0.00} -> {bloom.intensity.value:0.00}");
                sb.AppendLine($"    high quality filter    off   (several extra blur passes, per eye)");
            }
        }

        if (!found) sb.AppendLine("  no Bloom override found");

        AssetDatabase.SaveAssets();
        sb.AppendLine();
    }

    // ------------------------------------------------------------------ sun vs sky

    static void ReportSunVersusSky(StringBuilder sb)
    {
        sb.AppendLine("SUN AND SKY");

        var sun = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                        .FirstOrDefault(l => l.type == LightType.Directional && l.isActiveAndEnabled);

        string sky = RenderSettings.skybox != null ? RenderSettings.skybox.name : "none";

        if (sun == null)
        {
            sb.AppendLine("  no active directional light - the scene has no sun at all");
            sb.AppendLine();
            return;
        }

        float elevation = NormalizeAngle(sun.transform.eulerAngles.x);

        sb.AppendLine($"  skybox                   {sky}");
        sb.AppendLine($"  sun elevation            {elevation:0} deg");

        // A skybox paints the sun where the artist put it. A directional light casts shadows from
        // wherever it is pointed. Nothing connects the two, so they drift apart silently and the
        // result is a scene whose shadows fall at an angle its own sky does not explain.
        bool noonSky = sky.ToLowerInvariant().Contains("noon");

        if (noonSky && elevation < 45f)
        {
            sb.AppendLine();
            sb.AppendLine($"  MISMATCH: the sky is a noon sky but the sun sits at {elevation:0} degrees,");
            sb.AppendLine("  which is late afternoon. The shadows are long and raking while the sky");
            sb.AppendLine("  above says the sun is overhead. Run 'Match Sun To Sky' to lift it to 55,");
            sb.AppendLine("  or change the skybox instead - this one is left for you because it is a");
            sb.AppendLine("  look, not a fault.");
        }
        else
        {
            sb.AppendLine("  sun and sky agree");
        }

        sb.AppendLine();
    }

    [MenuItem("Tools/Emergency VR/Quality/Match Sun To Sky")]
    public static void MatchSun()
    {
        var sun = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                        .FirstOrDefault(l => l.type == LightType.Directional && l.isActiveAndEnabled);

        if (sun == null)
        {
            Debug.LogWarning("[Quality] no active directional light to move.");
            return;
        }

        Undo.RecordObject(sun.transform, "Match sun to sky");

        Vector3 e = sun.transform.eulerAngles;
        float before = e.x;

        e.x = 55f;
        sun.transform.eulerAngles = e;

        EditorUtility.SetDirty(sun.transform);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

        Debug.Log($"[Quality] sun elevation {before:0} -> 55 deg, to match a noon sky. " +
                  "Shadows are shorter and the light reads as overhead. Ctrl+Z puts it back.", sun);
    }
}
