using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Audit Building Colliders      (read-only, writes a report)
///   Tools > Emergency VR > Add Colliders To All Buildings
///   Tools > Emergency VR > Remove Added Building Colliders
///
/// Finishes the job of making the city solid. You added 44 box colliders and 24 mesh colliders
/// by hand; this finds every building that still has none and gives it one.
///
/// What counts as a building is decided by SHAPE, not by name. The models here are called
/// Cube.007, mesh_1.004 and Object_54, so a name filter would be guesswork. The test is:
///
///   tall enough      - a building is at least minHeight off the ground. This is also what
///                      excludes the roads and pavements, which are large but flat, and the
///                      kerb strips.
///   wide enough      - at least minFootprint across, which drops signs, bollards and litter.
///   not a vehicle    - anything under a VEH_ object, or named wheel / tyre, is skipped; cars
///                      are moving and a static collider under one would drag the map around.
///   not already solid- skipped if it, or an ancestor whose collider already encloses it, has
///                      a Collider. This is what makes the tool safe to run twice, and what
///                      stops it doubling up on the ones you did by hand.
///
/// BoxCollider by default. For architecture a box is what you want: a MeshCollider on every
/// building means PhysX cooking and carrying a full collision mesh per house, which on a phone
/// costs memory and load time for detail nobody can walk into anyway. Buildings that genuinely
/// need their shape - an archway you walk through, a courtyard - are the exception, and the
/// report flags the tall thin ones worth checking by hand.
/// </summary>
public static class BuildingColliderTool
{
    const string kReportPath = "BuildingColliderReport.txt";

    // A building is at least this tall. Roads, kerbs and pavements fall below it.
    const float kMinHeight = 2.5f;

    // ...and at least this wide. Drops signs, poles, bins and debris.
    const float kMinFootprint = 1.5f;

    // Above this, a box is a bad approximation and the report says to look at it.
    const float kSuspiciousAspect = 6f;

    [MenuItem("Tools/Emergency VR/Audit Building Colliders")]
    public static void Audit() { Run(write: false); }

    [MenuItem("Tools/Emergency VR/Add Colliders To All Buildings")]
    public static void AddColliders() { Run(write: true); }

    static void Run(bool write)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[BuildingCollider] Exit Play mode first.");
            return;
        }

        var scene = SceneManager.GetActiveScene();

        var candidates = new List<MeshRenderer>();
        int skippedVehicle = 0, skippedSmall = 0, skippedFlat = 0, alreadySolid = 0;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (IsVehicle(r.transform)) { skippedVehicle++; continue; }

                Bounds b = r.bounds;

                if (b.size.y < kMinHeight)
                {
                    // Flat and large is a road; flat and small is litter. Counted apart so the
                    // report says which filter is doing the work.
                    if (Mathf.Max(b.size.x, b.size.z) >= kMinFootprint) skippedFlat++;
                    else skippedSmall++;
                    continue;
                }

                if (Mathf.Max(b.size.x, b.size.z) < kMinFootprint) { skippedSmall++; continue; }

                if (HasCoveringCollider(r)) { alreadySolid++; continue; }

                candidates.Add(r);
            }
        }

        // ---------------------------------------------------------------- report
        var report = new StringBuilder();
        report.AppendLine($"Building collider {(write ? "pass" : "audit")} - scene '{scene.name}'");
        report.AppendLine(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        report.AppendLine();
        report.AppendLine($"  already have a collider     {alreadySolid}");
        report.AppendLine($"  need one                    {candidates.Count}");
        report.AppendLine($"  skipped, part of a vehicle  {skippedVehicle}");
        report.AppendLine($"  skipped, flat (road/kerb)   {skippedFlat}");
        report.AppendLine($"  skipped, too small          {skippedSmall}");
        report.AppendLine();
        report.AppendLine($"  a building = taller than {kMinHeight} m and wider than {kMinFootprint} m");
        report.AppendLine();

        if (candidates.Count == 0)
        {
            report.AppendLine("  Nothing left to do - every building already has a collider.");
            File.WriteAllText(kReportPath, report.ToString());
            Debug.Log("[BuildingCollider] Every building already has a collider. Report written " +
                      $"to {kReportPath}.");
            return;
        }

        if (write && !EditorUtility.DisplayDialog(
                "Add building colliders",
                $"{candidates.Count} buildings have no collider.\n\n" +
                $"Add a BoxCollider to each?\n\n" +
                $"{alreadySolid} already have one and will not be touched.",
                "Add them", "Cancel"))
            return;

        report.AppendLine("  object                                        w x h x d");

        int added = 0;
        var suspicious = new List<string>();

        if (write)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Add building colliders");
        }

        try
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var r = candidates[i];

                if (EditorUtility.DisplayCancelableProgressBar(
                        "Building colliders", r.name, (float)i / candidates.Count))
                    break;

                Bounds b = r.bounds;
                report.AppendLine($"  {Path(r.transform),-44} " +
                                  $"{b.size.x,6:0.0} x {b.size.y,6:0.0} x {b.size.z,6:0.0}");

                // A very tall, very thin box is usually a spire, a mast or an L-shaped mesh
                // whose bounding box swallows the courtyard next to it.
                float thin = Mathf.Min(b.size.x, b.size.z);
                if (thin > 0.01f && b.size.y / thin > kSuspiciousAspect)
                    suspicious.Add(r.name);

                if (!write) continue;

                var box = Undo.AddComponent<BoxCollider>(r.gameObject);

                // AddComponent already fits the box to the renderer, but only when the mesh is
                // readable at this moment; setting it from local bounds is deterministic.
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    box.center = mf.sharedMesh.bounds.center;
                    box.size = mf.sharedMesh.bounds.size;
                }

                added++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (suspicious.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("  WORTH A LOOK - tall and thin, so a box may cover more than the");
            report.AppendLine("  building does. Swap these for a MeshCollider if you can walk into");
            report.AppendLine("  thin air near them:");
            foreach (var s in suspicious) report.AppendLine($"      {s}");
        }

        File.WriteAllText(kReportPath, report.ToString());

        if (!write)
        {
            Debug.Log($"[BuildingCollider] AUDIT: {candidates.Count} buildings need a collider, " +
                      $"{alreadySolid} already have one. Nothing changed. See {kReportPath}.");
            return;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log($"[BuildingCollider] Added {added} BoxColliders. {alreadySolid} were already " +
                  $"solid and were left alone. {suspicious.Count} flagged as worth checking. " +
                  $"Report: {kReportPath}. Scene saved.");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// True if this renderer is already covered. Checks itself first, then any ancestor whose
    /// collider bounds actually enclose it - a collider three levels up on a different wing of
    /// the building does not count as covering this one.
    /// </summary>
    static bool HasCoveringCollider(MeshRenderer r)
    {
        if (r.GetComponent<Collider>() != null) return true;

        Bounds mine = r.bounds;

        for (Transform t = r.transform.parent; t != null; t = t.parent)
        {
            foreach (var c in t.GetComponents<Collider>())
            {
                if (c == null || !c.enabled) continue;
                if (c.bounds.Contains(mine.min) && c.bounds.Contains(mine.max)) return true;
            }
        }

        return false;
    }

    static bool IsVehicle(Transform t)
    {
        for (Transform c = t; c != null; c = c.parent)
        {
            string n = c.name.ToLowerInvariant();
            if (n.StartsWith("veh") || n.Contains("wheel") || n.Contains("tyre") ||
                n.Contains("tire") || n.Contains("donkey") || n.Contains("hourse") ||
                n.Contains("horse"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Removes BoxColliders from objects that look like buildings, for undoing a pass that went
    /// wrong. Leaves MeshColliders alone - those were put there deliberately.
    /// </summary>
    [MenuItem("Tools/Emergency VR/Remove Added Building Colliders")]
    public static void RemoveBoxColliders()
    {
        var scene = SceneManager.GetActiveScene();

        if (!EditorUtility.DisplayDialog(
                "Remove building colliders",
                "Remove every BoxCollider from building-sized objects?\n\n" +
                "MeshColliders are kept. This also removes boxes you added by hand.",
                "Remove", "Cancel"))
            return;

        int removed = 0;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (IsVehicle(r.transform)) continue;

                Bounds b = r.bounds;
                if (b.size.y < kMinHeight) continue;
                if (Mathf.Max(b.size.x, b.size.z) < kMinFootprint) continue;

                var box = r.GetComponent<BoxCollider>();
                if (box == null) continue;

                Undo.DestroyObjectImmediate(box);
                removed++;
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log($"[BuildingCollider] Removed {removed} BoxColliders. Scene saved.");
    }

    static string Path(Transform t)
    {
        var sb = new StringBuilder(t.name);
        for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
        string s = sb.ToString();
        return s.Length <= 44 ? s : "..." + s.Substring(s.Length - 41);
    }
}
