using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Fix Ambient Glow
///
/// Removes the "everything is lit from nowhere" look.
///
/// The scene has exactly three lights switched on: the sun, and the ambulance's two sirens,
/// which have a range of 0.3 m. None of them can be lighting every wheel in the city. What was,
/// was this pair:
///
///   Ambient intensity     1.71   - ambient light arrives on every surface from every direction
///                                  at once, with no falloff and no shadow. At 1.71 it is not a
///                                  fill any more, it is a second sun with no direction, and the
///                                  giveaway is that the glow appears on everything equally.
///   Reflection intensity  1.00   - with realtime reflection probes on, every metallic or smooth
///   Realtime reflections  on       material mirrors the bright sky. On a tyre, that reads as a
///                                  highlight nobody put there.
///
/// Realtime reflection probes were off in this project until the render-quality pass turned them
/// on; they were a mistake on a phone anyway, so they go back off.
///
/// Numbers are exposed as constants below. If the scene ends up too flat, raise kAmbient first -
/// it is the one doing most of the work.
/// </summary>
public static class AmbientGlowFix
{
    /// <summary>1.0 is the neutral value for skybox ambient: the sky contributes what it is,
    /// no more. Above that you are multiplying light that has no source.</summary>
    const float kAmbient = 1.0f;

    /// <summary>Enough sky reflection to keep glass and chrome alive, not enough to put a
    /// highlight on rubber.</summary>
    const float kReflection = 0.35f;

    [MenuItem("Tools/Emergency VR/Fix Ambient Glow")]
    public static void Apply()
    {
        var scene = SceneManager.GetActiveScene();
        var log = new StringBuilder("[AmbientGlowFix]\n");

        log.AppendLine(Change("Ambient intensity", RenderSettings.ambientIntensity, kAmbient));
        RenderSettings.ambientIntensity = kAmbient;

        log.AppendLine(Change("Reflection intensity", RenderSettings.reflectionIntensity, kReflection));
        RenderSettings.reflectionIntensity = kReflection;

        if (QualitySettings.realtimeReflectionProbes)
        {
            log.AppendLine("  Realtime reflection probes   ON  ->  OFF");
            QualitySettings.realtimeReflectionProbes = false;
        }
        else
        {
            log.AppendLine("  Realtime reflection probes   already off");
        }

        // Ambient colour only applies in Flat / Trilight, but it is stored either way and this
        // one is a strong orange - it would bite the moment the ambient mode changed.
        if (RenderSettings.ambientMode != UnityEngine.Rendering.AmbientMode.Skybox)
        {
            log.AppendLine($"  Ambient mode is {RenderSettings.ambientMode}, not Skybox - the " +
                           $"orange ambient colour {RenderSettings.ambientLight} IS being used. " +
                           "Set it to something neutral, or switch the mode to Skybox.");
        }

        log.AppendLine();
        log.AppendLine("  Lights actually on in this scene: the sun, plus the ambulance's two " +
                       "sirens at 0.3 m range. Nothing was lighting the wheels.");

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        EditorApplication.ExecuteMenuItem("File/Save Project");

        log.AppendLine("  Scene saved.");
        Debug.Log(log.ToString());
    }

    /// <summary>
    /// The sirens are 50 intensity over a 0.3 m range, which in URP is a small white blob rather
    /// than a flashing light. Separate menu item because they are meant to be there - this only
    /// makes them behave.
    /// </summary>
    [MenuItem("Tools/Emergency VR/Tame Siren Lights")]
    public static void TameSirens()
    {
        var scene = SceneManager.GetActiveScene();
        var log = new StringBuilder("[AmbientGlowFix] sirens\n");

        int n = 0;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var l in root.GetComponentsInChildren<Light>(true))
            {
                if (l.type != LightType.Point) continue;
                if (!l.name.ToLowerInvariant().Contains("siren")) continue;

                Undo.RecordObject(l, "Tame siren");

                log.AppendLine($"  {l.name}: intensity {l.intensity:0.0} -> 8, " +
                               $"range {l.range:0.00} -> 3.0, shadows {l.shadows} -> None");

                l.intensity = 8f;
                l.range = 3f;
                l.shadows = LightShadows.None;   // a 0.3 m shadow-casting point light is pure cost

                EditorUtility.SetDirty(l);
                n++;
            }
        }

        if (n == 0)
        {
            Debug.Log("[AmbientGlowFix] No point lights named *siren* found.");
            return;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        log.AppendLine($"  {n} siren lights adjusted. Scene saved.");
        Debug.Log(log.ToString());
    }

    static string Change(string label, float from, float to)
    {
        return Mathf.Approximately(from, to)
            ? $"  {label,-28} {to:0.00}  (already correct)"
            : $"  {label,-28} {from:0.00}  ->  {to:0.00}";
    }
}
