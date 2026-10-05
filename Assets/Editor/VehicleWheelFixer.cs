using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Finds every vehicle's wheels and writes them into the scene.
///
///   Tools > Emergency VR > Fix All Vehicle Wheels
///   Tools > Emergency VR > Audit Vehicle Wheels     (read-only)
///
/// Why this exists: 34 of the 36 VehicleControllers in SampleScene have empty Front Wheels /
/// Rear Wheels lists and rely on the runtime auto-detect in Start(). That detect is name-based -
/// it wants "wheel", "tire" or "tyre" somewhere in the name - and a model exported with its
/// wheels called Object_60, Circle.004 or mesh_1.007 gives it nothing to match. The vehicle then
/// drives perfectly with four wheels welded in place, which is exactly the symptom.
///
/// Baking the lists in the Editor also means the result is visible in the Inspector and survives
/// into the build, instead of being re-guessed on every load.
///
/// Three passes, in order, stopping at the first that finds anything:
///
///   1. Name    - children called *wheel* / *tire* / *tyre*.
///   2. Sibling - the same search across the parent's other children, for models where the
///                wheels sit beside the body rather than under it.
///   3. Shape   - no names to go on, so look for wheels: a wheel is a disc (its thinnest
///                dimension is well under its widest), it sits in the lower part of the
///                vehicle, it is small relative to the whole vehicle, and it has three or more
///                near-identical siblings. Four objects of the same size, all low, all disc
///                shaped, are wheels whatever they are called.
///
/// Wheel radius is measured from the mesh rather than trusted - one vehicle in this scene had
/// radius 3, which is a 6-metre tyre and rolls about six times too slowly to read as motion.
/// </summary>
public static class VehicleWheelFixer
{
    const string kReportPath = "VehicleWheelReport.txt";

    [MenuItem("Tools/Emergency VR/Fix All Vehicle Wheels")]
    public static void Fix() { Run(write: true); }

    [MenuItem("Tools/Emergency VR/Audit Vehicle Wheels")]
    public static void Audit() { Run(write: false); }

    /// <summary>
    /// Drops the Play-mode probe into the scene. Reading the code cannot tell a frozen wheel
    /// from a parked car - both look identical - so this measures it instead.
    /// </summary>
    [MenuItem("Tools/Emergency VR/Add Vehicle Motion Probe")]
    public static void AddProbe()
    {
        var scene = SceneManager.GetActiveScene();

        foreach (var root in scene.GetRootGameObjects())
        {
            var existing = root.GetComponentInChildren<VehicleMotionProbe>(true);
            if (existing == null) continue;

            Selection.activeObject = existing.gameObject;
            Debug.Log("[VehicleWheelFixer] Probe already in the scene. Press Play and read the " +
                      "table it logs after 4 seconds.", existing);
            return;
        }

        var go = new GameObject("Vehicle Motion Probe");
        Undo.RegisterCreatedObjectUndo(go, "Add vehicle motion probe");
        go.AddComponent<VehicleMotionProbe>();

        Selection.activeObject = go;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("[VehicleWheelFixer] Probe added. Press Play, wait 4 seconds, and read the " +
                  "table in the Console. Delete the object when done.", go);
    }

    static void Run(bool write)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[VehicleWheelFixer] Exit Play mode first.");
            return;
        }

        var scene = SceneManager.GetActiveScene();
        var report = new StringBuilder();
        report.AppendLine($"Vehicle wheel {(write ? "fix" : "audit")} - scene '{scene.name}'");
        report.AppendLine(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        report.AppendLine();

        var controllers = new List<VehicleController>();
        foreach (var root in scene.GetRootGameObjects())
            controllers.AddRange(root.GetComponentsInChildren<VehicleController>(true));

        if (controllers.Count == 0)
        {
            Debug.LogWarning("[VehicleWheelFixer] No VehicleController in the open scene.");
            return;
        }

        int fixedCount = 0, alreadyOk = 0, failed = 0;
        var failures = new List<string>();

        if (write)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Fix vehicle wheels");
        }

        for (int i = 0; i < controllers.Count; i++)
        {
            var vc = controllers[i];
            if (vc == null) continue;

            EditorUtility.DisplayProgressBar("Vehicle wheels", vc.name,
                                             (float)i / controllers.Count);

            int already = CountValid(vc.frontWheels) + CountValid(vc.rearWheels);

            string method;
            var wheels = FindWheels(vc.transform, out method);

            report.AppendLine($"{vc.name}");
            report.AppendLine($"    mode {vc.navigationMode}, speed {vc.moveSpeed}, " +
                              $"waypoints {(vc.waypoints == null ? 0 : vc.waypoints.Length)}");
            report.AppendLine($"    already assigned: {already}");
            report.AppendLine($"    found: {wheels.Count} via {method}");

            foreach (var w in wheels)
                report.AppendLine($"        {w.name}");

            if (wheels.Count == 0)
            {
                failed++;
                failures.Add(vc.name);

                // Clear rather than leave alone. A previous run of this tool wrote 141 wheels
                // into the two carts; leaving that in place would have them steering the whole
                // city. An empty list is the honest answer for a vehicle with no wheel objects.
                if (write && (CountValid(vc.frontWheels) + CountValid(vc.rearWheels)) > 0)
                {
                    var clearSo = new SerializedObject(vc);
                    WriteList(clearSo.FindProperty("frontWheels"), new List<Transform>());
                    WriteList(clearSo.FindProperty("rearWheels"), new List<Transform>());
                    clearSo.ApplyModifiedProperties();
                    EditorUtility.SetDirty(vc);
                    report.AppendLine("    cleared a previously written wheel list.");
                }

                report.AppendLine("    RESULT: no wheel objects of its own - left with none.");
                report.AppendLine();
                continue;
            }

            float radius = MeasureRadius(wheels);
            report.AppendLine($"    radius: {vc.wheelRadius:0.000} -> {radius:0.000}");

            int detached = 0;
            foreach (var w in wheels)
                if (!w.IsChildOf(vc.transform)) detached++;

            // Reported, not repaired. Almost everything here is a prefab instance, and Unity
            // refuses to restructure those from script - the re-parent silently does nothing,
            // which is worse than saying so. If a wheel really is detached, move it in the
            // Hierarchy by hand.
            if (detached > 0)
                report.AppendLine($"    DETACHED: {detached} of these are not under the vehicle - " +
                                  "they will be left behind when it drives off. Drag them under " +
                                  $"{vc.name} in the Hierarchy.");

            if (!write)
            {
                if (already > 0) alreadyOk++; else fixedCount++;
                report.AppendLine();
                continue;
            }

            // Split front / rear along the vehicle's own forward, in world space, so a model
            // rotated on import does not scramble the order.
            Vector3 forward = GroundForward(vc.transform);
            Vector3 centre = Vector3.zero;
            foreach (var w in wheels) centre += w.position;
            centre /= wheels.Count;

            wheels.Sort((a, b) => Vector3.Dot(b.position - centre, forward)
                                         .CompareTo(Vector3.Dot(a.position - centre, forward)));

            var front = new List<Transform>();
            var rear = new List<Transform>();
            int half = wheels.Count / 2;
            for (int k = 0; k < wheels.Count; k++)
                (k < half ? front : rear).Add(wheels[k]);

            var so = new SerializedObject(vc);
            WriteList(so.FindProperty("frontWheels"), front);
            WriteList(so.FindProperty("rearWheels"), rear);

            var radiusProp = so.FindProperty("wheelRadius");
            if (radiusProp != null) radiusProp.floatValue = radius;

            // The lists are authoritative now; leaving auto-detect on would clear and re-guess
            // them on load and undo this.
            var autoProp = so.FindProperty("autoDetectWheels");
            if (autoProp != null) autoProp.boolValue = false;

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(vc);

            report.AppendLine($"    RESULT: {front.Count} front / {rear.Count} rear, auto-detect off.");
            report.AppendLine();

            if (already > 0) alreadyOk++; else fixedCount++;
        }

        EditorUtility.ClearProgressBar();

        report.AppendLine("----");
        report.AppendLine($"vehicles: {controllers.Count}");
        report.AppendLine($"wheels resolved: {fixedCount + alreadyOk}");
        report.AppendLine($"no wheels found: {failed}");
        if (failures.Count > 0) report.AppendLine("  " + string.Join(", ", failures));

        File.WriteAllText(kReportPath, report.ToString());

        if (write)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        Debug.Log($"[VehicleWheelFixer] {controllers.Count} vehicles, " +
                  $"{fixedCount + alreadyOk} with wheels, {failed} without. " +
                  $"Full report written to {kReportPath}." +
                  (write ? " Scene saved." : " (audit only, nothing changed)"));
    }

    // ---------------------------------------------------------------- finding

    static List<Transform> FindWheels(Transform vehicle, out string method)
    {
        var byName = new List<Transform>();
        CollectByName(vehicle, byName);
        if (byName.Count > 0) { method = "name"; return byName; }

        // The model was flattened on import: VEH_silver_c, VEHBODY_silver_c and
        // VEHWHEEL_silver_c_001..004 all sit side by side instead of nested. Match on the tag
        // in the middle of the name and pull them back together.
        var byTag = CollectByTag(vehicle);
        if (byTag.Count > 0) { method = "name tag across scene"; return byTag; }

        // Siblings, but ONLY ones carrying this vehicle's tag.
        //
        // The first version of this recursed into every sibling and took any wheel it found.
        // Every vehicle in this scene is a child of Emergency_VR360, so for VEH_wagon - a cart,
        // which genuinely has no VEHWHEEL_ objects of its own - that swept up all 141 wheels in
        // the city and handed them to the cart. Run it and two carts try to steer the whole map.
        // A fallback that widens until it finds something will always find something.
        if (vehicle.parent != null)
        {
            string tag = VehicleTag(vehicle.name);
            if (!string.IsNullOrEmpty(tag))
            {
                var sib = new List<Transform>();
                foreach (Transform s in vehicle.parent)
                {
                    if (s == vehicle) continue;
                    if (!IsWheelName(s.name)) continue;
                    if (!TagMatches(s.name, tag)) continue;
                    if (!sib.Contains(s)) sib.Add(s);
                }
                if (sib.Count > 0 && sib.Count <= kMaxWheels)
                {
                    method = "sibling name";
                    return sib;
                }
            }
        }

        var byShape = CollectByShape(vehicle);
        if (byShape.Count > 0 && byShape.Count <= kMaxWheels)
        {
            method = "shape";
            return byShape;
        }

        method = "nothing";
        return new List<Transform>();
    }

    /// <summary>No road vehicle in this scene has more than eight wheels. Anything past that is
    /// a search that has gone wide, not a lorry.</summary>
    const int kMaxWheels = 8;

    /// <summary>
    /// VEH_silver_c -> tag "silver_c" -> any VEHWHEEL_silver_c_* anywhere in the scene.
    /// Deliberately searches the whole scene, because the whole point is that the wheels are no
    /// longer anywhere near their vehicle in the hierarchy.
    /// </summary>
    static List<Transform> CollectByTag(Transform vehicle)
    {
        var found = new List<Transform>();

        string tag = VehicleTag(vehicle.name);
        if (string.IsNullOrEmpty(tag)) return found;

        foreach (var root in vehicle.gameObject.scene.GetRootGameObjects())
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!IsWheelName(t.name)) continue;

                if (!TagMatches(t.name, tag)) continue;
                if (!found.Contains(t)) found.Add(t);
            }
        }

        // More than a lorry's worth means the tag matched too loosely. Better to report nothing
        // than to hand one car another car's wheels.
        if (found.Count > kMaxWheels) found.Clear();

        return found;
    }

    /// <summary>
    /// "silver" must not swallow "silver_c", or VEH_silver takes VEH_silver_c's wheels. The tag
    /// has to be followed by a separator or the end of the name.
    /// </summary>
    static bool TagMatches(string objectName, string tag)
    {
        string n = objectName.ToLowerInvariant();

        int at = n.IndexOf(tag, System.StringComparison.Ordinal);
        if (at < 0) return false;

        int after = at + tag.Length;
        if (after >= n.Length) return true;

        char c = n[after];
        return c == '_' || c == '.' || c == ' ' || c == '-';
    }

    /// <summary>VEH_silver_c -> "silver_c". VEHBODY_ and VEHWHEEL_ strip the same way.</summary>
    static string VehicleTag(string objectName)
    {
        if (string.IsNullOrEmpty(objectName)) return null;

        string n = objectName.ToLowerInvariant();
        foreach (var prefix in new[] { "vehbody_", "vehwheel_", "veh_" })
            if (n.StartsWith(prefix, System.StringComparison.Ordinal))
                return n.Substring(prefix.Length);

        return null;
    }

    static void CollectByName(Transform root, List<Transform> into)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            // Walk up to the highest ancestor that still reads as a wheel, so the wheel root is
            // rotated rather than a mesh child inside it.
            Transform pick = null;
            for (Transform c = r.transform; c != null && c != root.parent; c = c.parent)
                if (IsWheelName(c.name)) pick = c;

            if (pick != null && !into.Contains(pick)) into.Add(pick);
        }
    }

    /// <summary>
    /// Geometry fallback for models whose wheels have no useful names. Looks for a set of
    /// near-identical discs sitting low on the vehicle.
    /// </summary>
    static List<Transform> CollectByShape(Transform vehicle)
    {
        var result = new List<Transform>();

        var renderers = vehicle.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length < 4) return result;

        // Whole-vehicle bounds, to judge "small" and "low" against.
        Bounds whole = renderers[0].bounds;
        foreach (var r in renderers) whole.Encapsulate(r.bounds);

        float wholeSpan = Mathf.Max(whole.size.x, Mathf.Max(whole.size.y, whole.size.z));
        if (wholeSpan <= 0.001f) return result;

        var candidates = new List<(Transform t, float span)>();

        foreach (var r in renderers)
        {
            Vector3 s = r.bounds.size;
            float big = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            float small = Mathf.Min(s.x, Mathf.Min(s.y, s.z));
            if (big <= 0.001f) continue;

            // A disc: clearly thinner one way than the other two.
            if (small / big > 0.6f) continue;

            // Small relative to the vehicle - excludes the body panels and the chassis.
            if (big > wholeSpan * 0.4f) continue;

            // Low - wheels live in the bottom half.
            float heightFraction = (r.bounds.center.y - whole.min.y) / Mathf.Max(0.001f, whole.size.y);
            if (heightFraction > 0.5f) continue;

            candidates.Add((r.transform, big));
        }

        if (candidates.Count < 3) return result;

        // Keep only the group that are all about the same size. Wheels match each other; a
        // mirror, a light and an exhaust do not.
        candidates.Sort((a, b) => a.span.CompareTo(b.span));
        float median = candidates[candidates.Count / 2].span;

        foreach (var c in candidates)
            if (Mathf.Abs(c.span - median) <= median * 0.35f)
                result.Add(c.t);

        // Three or more identical low discs is a wheelset. Two is a coincidence.
        if (result.Count < 3) result.Clear();

        return result;
    }

    static bool IsWheelName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        string s = n.ToLowerInvariant();
        return s.Contains("wheel") || s.Contains("tire") || s.Contains("tyre");
    }

    // ---------------------------------------------------------------- measuring

    /// <summary>
    /// Radius = half the widest cross-section, measured from the renderer. Never bounds.extents.y,
    /// which is the axle half-width on a wheel whose pivot came in rotated.
    /// </summary>
    static float MeasureRadius(List<Transform> wheels)
    {
        float best = 0f;

        foreach (var w in wheels)
        {
            var r = w.GetComponentInChildren<Renderer>();
            if (r == null) continue;

            Vector3 e = r.bounds.extents;
            float radius = Mathf.Max(e.x, Mathf.Max(e.y, e.z));
            if (radius > best) best = radius;
        }

        // Anything outside this is a measurement gone wrong, not a real tyre.
        return (best > 0.05f && best < 2f) ? best : 0.35f;
    }

    static Vector3 GroundForward(Transform t)
    {
        Vector3 f = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        if (f.sqrMagnitude > 0.01f) return f.normalized;

        f = Vector3.ProjectOnPlane(t.up, Vector3.up);
        if (f.sqrMagnitude > 0.01f) return f.normalized;

        return Vector3.forward;
    }

    static int CountValid(List<Transform> list)
    {
        if (list == null) return 0;
        int n = 0;
        foreach (var t in list) if (t != null) n++;
        return n;
    }

    static void WriteList(SerializedProperty prop, List<Transform> values)
    {
        if (prop == null || !prop.isArray) return;

        prop.ClearArray();
        for (int i = 0; i < values.Count; i++)
        {
            prop.InsertArrayElementAtIndex(i);
            prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }
    }
}
