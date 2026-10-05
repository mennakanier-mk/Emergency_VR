using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Audit Scene Lights
///   Tools > Emergency VR > Turn Off Vehicle Lights
///
/// The scene FILE only lists four lights, all directional, because every vehicle is a prefab
/// instance and its lights live inside the prefab. Reading the .unity file therefore misses
/// them entirely - they only appear once the hierarchy is walked in the Editor, which is what
/// this does.
///
/// It also reports emissive materials, because a glow on a wheel is just as likely to be a
/// material with Emission ticked as it is to be a real light, and the two look identical in a
/// screenshot while needing opposite fixes.
///
/// Writes SceneLightReport.txt in the project folder.
/// </summary>
public static class LightAudit
{
    const string kReportPath = "SceneLightReport.txt";

    [MenuItem("Tools/Emergency VR/Audit Scene Lights")]
    public static void Audit()
    {
        var scene = SceneManager.GetActiveScene();
        var report = new StringBuilder();

        report.AppendLine($"Scene light audit - '{scene.name}'");
        report.AppendLine(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        report.AppendLine();

        // ---------------------------------------------------------- lights
        var lights = new List<Light>();
        foreach (var root in scene.GetRootGameObjects())
            lights.AddRange(root.GetComponentsInChildren<Light>(true));

        report.AppendLine($"LIGHTS IN HIERARCHY: {lights.Count}");
        report.AppendLine();
        report.AppendLine("  on   type         intensity   range  shadows   path");

        int onCount = 0, vehicleLights = 0;

        foreach (var l in lights)
        {
            bool on = l.enabled && l.gameObject.activeInHierarchy;
            if (on) onCount++;

            string path = Path(l.transform);
            bool onVehicle = path.ToLowerInvariant().Contains("veh");
            if (on && onVehicle) vehicleLights++;

            report.AppendLine($"  {(on ? "ON " : "off")}  {l.type,-12} {l.intensity,8:0.00} " +
                              $"{l.range,7:0.00}  {l.shadows,-8} {path}");
        }

        report.AppendLine();
        report.AppendLine($"  enabled: {onCount}   of those on vehicles: {vehicleLights}");
        report.AppendLine();

        // ---------------------------------------------------------- emissive materials
        report.AppendLine("EMISSIVE MATERIALS ON VEHICLES AND WHEELS");
        report.AppendLine();

        var seen = new HashSet<Material>();
        int emissive = 0;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                string path = Path(r.transform);
                string lower = path.ToLowerInvariant();
                if (!lower.Contains("veh") && !lower.Contains("wheel")) continue;

                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m)) continue;
                    if (!IsEmissive(m)) continue;

                    emissive++;
                    Color c = m.HasProperty("_EmissionColor")
                        ? m.GetColor("_EmissionColor") : Color.black;

                    report.AppendLine($"  {m.name,-34} emission {c.r:0.00},{c.g:0.00},{c.b:0.00}" +
                                      $"   shader {m.shader.name}");
                    report.AppendLine($"      first seen on {path}");
                }
            }
        }

        if (emissive == 0) report.AppendLine("  none.");

        // ---------------------------------------------------------- the usual suspects
        report.AppendLine();
        report.AppendLine("RENDER SETTINGS THAT MAKE THINGS LOOK LIT WHEN THEY ARE NOT");
        report.AppendLine();
        report.AppendLine($"  Ambient mode          {RenderSettings.ambientMode}");
        report.AppendLine($"  Ambient intensity     {RenderSettings.ambientIntensity:0.00}");
        report.AppendLine($"  Ambient light         {RenderSettings.ambientLight}");
        report.AppendLine($"  Reflection intensity  {RenderSettings.reflectionIntensity:0.00}");
        report.AppendLine($"  Realtime reflections  {QualitySettings.realtimeReflectionProbes}");
        report.AppendLine($"  Skybox                {(RenderSettings.skybox == null ? "none" : RenderSettings.skybox.name)}");
        report.AppendLine();
        report.AppendLine("  A bright sheen that appears on every wheel at once, with no light");
        report.AppendLine("  near them, is usually reflection or ambient hitting a smooth,");
        report.AppendLine("  metallic material - not a lamp. Check Smoothness and Metallic on the");
        report.AppendLine("  wheel material before hunting for a light that is not there.");

        File.WriteAllText(kReportPath, report.ToString());

        Debug.Log($"[LightAudit] {lights.Count} lights ({onCount} on, {vehicleLights} of those on " +
                  $"vehicles), {emissive} emissive vehicle materials. Written to {kReportPath}.");
    }

    /// <summary>
    /// Disables every light that sits under an object whose name mentions a vehicle. Sirens and
    /// headlights included - they are lights on vehicles, which is what was asked for. Re-enable
    /// individual ones afterwards if you want the sirens back.
    /// </summary>
    [MenuItem("Tools/Emergency VR/Turn Off Vehicle Lights")]
    public static void TurnOffVehicleLights()
    {
        var scene = SceneManager.GetActiveScene();
        var log = new StringBuilder("[LightAudit] turning off vehicle lights\n");

        int off = 0;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var l in root.GetComponentsInChildren<Light>(true))
            {
                if (l.type == LightType.Directional) continue;   // never the sun
                if (!l.enabled) continue;

                string path = Path(l.transform).ToLowerInvariant();
                if (!path.Contains("veh")) continue;

                Undo.RecordObject(l, "Turn off vehicle light");
                l.enabled = false;
                EditorUtility.SetDirty(l);

                log.AppendLine($"  off: {Path(l.transform)}  ({l.type}, {l.intensity:0.0})");
                off++;
            }
        }

        if (off == 0)
        {
            Debug.Log("[LightAudit] No enabled non-directional lights on vehicles. The glow is " +
                      "not coming from a light - run Audit Scene Lights and look at the emissive " +
                      "materials and reflection settings instead.");
            return;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        log.AppendLine($"  total {off}. Scene saved.");
        Debug.Log(log.ToString());
    }

    static bool IsEmissive(Material m)
    {
        if (!m.HasProperty("_EmissionColor")) return false;
        if (!m.IsKeywordEnabled("_EMISSION")) return false;

        Color c = m.GetColor("_EmissionColor");
        return c.maxColorComponent > 0.01f;
    }

    static string Path(Transform t)
    {
        var sb = new StringBuilder(t.name);
        for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
        return sb.ToString();
    }
}
