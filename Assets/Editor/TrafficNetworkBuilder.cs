using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Build Traffic Network   - make one object per street, plus the ring
///   Tools > Emergency VR > Reassign Vehicles       - after moving a street, put the cars back on
///   Tools > Emergency VR > Clear Traffic Network
///
/// The waypoint approach is gone. Generating two hundred points from measured geometry kept
/// putting them off the tarmac, and a point a car cannot quite reach is a car that drives into
/// the distance for ever. The points were only ever a clumsy way of writing down a line.
///
/// Now each street is ONE object: a position, a heading you set with the Rotate tool, a length
/// and a lane offset. Its gizmo is drawn from the same maths the cars drive on, so if the gizmo
/// is on the road, the cars are on the road. Six objects to nudge instead of two hundred points
/// to regenerate, and "is it right?" is answered by looking rather than by pressing Play.
///
/// This tool only places them somewhere sensible to start from. Aligning them exactly is a
/// thirty-second job with the Move and Rotate tools, and it is the last time it needs doing.
/// </summary>
public static class TrafficNetworkBuilder
{
    const string kRootName = "Traffic Routes";
    const string kReportPath = "TrafficRouteReport.txt";
    const float kFarRadius = 45f;
    const float kSpokeMergeDegrees = 30f;

    // ================================================================ build

    [MenuItem("Tools/Emergency VR/Build Traffic Network")]
    public static void Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Traffic] Exit Play mode first.");
            return;
        }

        var scene = SceneManager.GetActiveScene();

        var markers = new List<Transform>();
        foreach (var root in scene.GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (IsMarkerName(t.name)) markers.Add(t);

        if (markers.Count < 6)
        {
            Debug.LogError($"[Traffic] Only {markers.Count} w1 markers found.");
            return;
        }

        Vector3 rough = Median(markers.Select(m => m.position).ToList());
        var far = markers.Where(m => Flat(m.position - rough).magnitude > kFarRadius).ToList();
        var spokes = GroupIntoSpokes(far, rough);

        if (spokes.Count < 2)
        {
            Debug.LogError("[Traffic] Fewer than two streets found.");
            return;
        }

        Vector3 centre = SolveCentre(spokes, rough);
        centre = SolveCentre(spokes, centre);

        float groundY = markers[0].position.y;

        MeasureRoundabout(scene, centre, out float islandR, out float pavementR);
        if (pavementR < 1f) { islandR = 10f; pavementR = 26f; }

        float ringRadius = Mathf.Lerp(islandR, pavementR, 0.55f);
        float streetStart = pavementR + 6f;

        // ---------------------------------------------------------------- root
        var rootGo = GameObject.Find(kRootName);
        if (rootGo != null) Undo.DestroyObjectImmediate(rootGo);

        rootGo = new GameObject(kRootName);
        Undo.RegisterCreatedObjectUndo(rootGo, "Create traffic network");

        var log = new StringBuilder();
        log.AppendLine($"Traffic network - scene '{scene.name}'");
        log.AppendLine(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        log.AppendLine();
        log.AppendLine($"  roundabout centre ({centre.x:0.0}, {centre.z:0.0})");
        log.AppendLine($"  island {islandR:0.0} m, pavement {pavementR:0.0} m");
        log.AppendLine($"  ring radius {ringRadius:0.0} m, streets start at {streetStart:0.0} m");
        log.AppendLine();

        // ---------------------------------------------------------------- ring
        var ringGo = new GameObject("Roundabout");
        Undo.RegisterCreatedObjectUndo(ringGo, "Create ring");
        ringGo.transform.SetParent(rootGo.transform, false);
        ringGo.transform.position = new Vector3(centre.x, groundY, centre.z);

        var ring = ringGo.AddComponent<TrafficRing>();
        ring.radius = ringRadius;
        ring.height = groundY;

        log.AppendLine($"  Roundabout            r {ringRadius:0.0} m");

        // ---------------------------------------------------------------- streets
        var roads = new List<TrafficRoad>();

        var ordered = spokes
            .Select(s => new { s, mean = Mean(s) })
            .OrderBy(x => Bearing(Flat(x.mean - centre)))
            .ToList();

        foreach (var item in ordered)
        {
            Vector3 mean = item.mean;
            Vector3 dir = Flat(mean - centre).normalized;
            float outer = Flat(mean - centre).magnitude;

            // Straight from just outside the roundabout to the far markers.
            float streetLength = Mathf.Max(20f, outer - streetStart);
            Vector3 midpoint = centre + dir * (streetStart + streetLength * 0.5f);

            // Lane offset from the marker pair when there is one - two markers dropped one per
            // lane are a direct measurement of the street you drew.
            // Your marker pairs sit about 6-7 m apart, which is narrower than the carriageways
            // they mark - the lanes ended up hugging the centre line. Take the pair as a floor,
            // not as the answer.
            float lane = 6f;
            if (item.s.Count == 2)
            {
                float span = Flat(item.s[1].position - item.s[0].position).magnitude;
                if (span > 2f && span < 24f) lane = Mathf.Max(6f, span * 0.5f);
            }

            var go = new GameObject($"Street {Bearing(dir):0} deg");
            Undo.RegisterCreatedObjectUndo(go, "Create street");
            go.transform.SetParent(rootGo.transform, false);
            go.transform.position = new Vector3(midpoint.x, groundY, midpoint.z);
            go.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

            var road = go.AddComponent<TrafficRoad>();
            road.length = streetLength;
            road.laneOffset = lane;
            road.height = groundY;

            roads.Add(road);

            log.AppendLine($"  {go.name,-22} {streetLength:0} m long, lanes +/-{lane:0.0} m");
        }

        File.WriteAllText(kReportPath, log.ToString());

        Selection.activeObject = rootGo;
        EditorSceneManager.MarkSceneDirty(scene);

        Debug.Log($"[Traffic] Built {roads.Count} streets + the roundabout as single objects.\n" +
                  "Look at the green gizmos: drag and rotate each 'Street' object until its two " +
                  "lanes sit on the tarmac, and set Radius on 'Roundabout' so its circle sits on " +
                  "the ring. Then run Reassign Vehicles.");

        Reassign();
    }

    // ================================================================ vehicles

    [MenuItem("Tools/Emergency VR/Reassign Vehicles")]
    public static void Reassign()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Traffic] Exit Play mode first.");
            return;
        }

        var scene = SceneManager.GetActiveScene();

        // Vehicle paths only. A pavement is a TrafficPath too, and without this filter the
        // nearest-path search would happily put a fire engine on it.
        var paths = new List<TrafficPath>();
        foreach (var root in scene.GetRootGameObjects())
            foreach (var p in root.GetComponentsInChildren<TrafficPath>(true))
                if (p.kind == TrafficPath.PathKind.Vehicles) paths.Add(p);

        if (paths.Count == 0)
        {
            Debug.LogError("[Traffic] No streets found. Run Build Traffic Network first.");
            return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Reassign vehicles");

        var log = new StringBuilder("ASSIGNMENTS\n");
        var counts = new Dictionary<TrafficPath, int>();
        foreach (var p in paths) counts[p] = 0;

        int moved = 0;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;

                bool isCar = go.GetComponent<VehicleController>() != null;
                bool isAnimal = go.GetComponent<AnimatedVehicleMover>() != null;
                if (!isCar && !isAnimal) continue;

                TrafficPath best = NearestPath(paths, t.position);
                if (best == null) continue;

                // The old movers would fight the driver for the transform.
                foreach (var vc in go.GetComponents<VehicleController>())
                    if (vc.enabled) { Undo.RecordObject(vc, "Disable"); vc.enabled = false; }

                foreach (var av in go.GetComponents<AnimatedVehicleMover>())
                    if (av.enabled) { Undo.RecordObject(av, "Disable"); av.enabled = false; }

                // The raycast sensor is replaced by exact spacing along the path.
                foreach (var ts in go.GetComponents<TrafficSensor>())
                    if (ts.enabled) { Undo.RecordObject(ts, "Disable"); ts.enabled = false; }

                var driver = go.GetComponent<RoadDriver>();
                if (driver == null) driver = Undo.AddComponent<RoadDriver>(go);
                else Undo.RecordObject(driver, "Reassign");

                driver.path = best;
                driver.speed = isAnimal ? 2.5f : 6f;
                driver.SnapToNearest();

                // Carry over the facing you already tuned. Reverse Direction and Forward Yaw
                // Offset were set per vehicle by hand - a donkey cart authored facing the other
                // way looks wrong the moment it starts moving, and making you redo all of that
                // would be the tool undoing your work.
                var oldCar = go.GetComponent<VehicleController>();
                if (oldCar != null) driver.reverseDirection = oldCar.reverseDirection;

                var oldMover = go.GetComponent<AnimatedVehicleMover>();
                if (oldMover != null)
                {
                    driver.reverseDirection = oldMover.reverseDirection;

                    // Negated on purpose. The old script rotated the REFERENCE vector it
                    // measured the model against; RoadDriver rotates the model itself, which is
                    // what a slider called "facing yaw offset" should do. Those are opposite
                    // senses, so +90 there is -90 here. Identical at 180, which is why the
                    // donkey looked right either way - it would have been wrong the first time
                    // you used any other angle.
                    driver.facingYawOffset = -oldMover.forwardYawOffset;
                }

                EditorUtility.SetDirty(driver);

                counts[best]++;
                moved++;

                log.AppendLine($"  {go.name,-24} {best.name}");
            }
        }

        log.AppendLine();
        foreach (var kv in counts)
            log.AppendLine($"  {kv.Key.name,-24} {kv.Value} vehicles");

        File.AppendAllText(kReportPath, "\n" + log);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log($"[Traffic] {moved} vehicles now driven along {paths.Count} paths. " +
                  $"Report: {kReportPath}. Scene saved.");
    }

    // ================================================================ pedestrians

    /// <summary>
    /// Drops a straight walkway in front of the Scene view camera, ready to be placed. It is
    /// deliberately not measured from anything: pavements do not follow the road centre lines,
    /// and every attempt in this project to infer a route from geometry landed it somewhere
    /// wrong. You can see where the pavement is; put it there.
    /// </summary>
    [MenuItem("Tools/Emergency VR/Add Walkway")]
    public static void AddWalkway()
    {
        var scene = SceneManager.GetActiveScene();

        var rootGo = GameObject.Find(kRootName);
        if (rootGo == null)
        {
            rootGo = new GameObject(kRootName);
            Undo.RegisterCreatedObjectUndo(rootGo, "Create traffic root");
        }

        int n = 0;
        foreach (var root in scene.GetRootGameObjects())
            n += root.GetComponentsInChildren<TrafficWalkway>(true).Length;

        var go = new GameObject($"Walkway {n + 1}");
        Undo.RegisterCreatedObjectUndo(go, "Add walkway");
        go.transform.SetParent(rootGo.transform, false);

        // In front of wherever you are looking, so it is not lost at the origin.
        var view = SceneView.lastActiveSceneView;
        Vector3 at = view != null ? view.pivot : Vector3.zero;

        // Fully qualified: this is a plain static class, not a MonoBehaviour, so the
        // convenience overload is not in scope here.
        var existing = Object.FindFirstObjectByType<TrafficRoad>();
        float y = existing != null ? existing.height : at.y;

        go.transform.position = new Vector3(at.x, y, at.z);

        var way = go.AddComponent<TrafficWalkway>();
        way.kind = TrafficPath.PathKind.Pedestrians;
        way.height = y;
        way.minGap = 1.5f;        // people stand much closer together than cars
        way.slowGap = 4f;

        Selection.activeObject = go;
        EditorSceneManager.MarkSceneDirty(scene);

        Debug.Log("[Traffic] Walkway added. Move and rotate it onto the pavement - straight is " +
                  "usually enough. To bend it, press a white + in the Scene view to drop a " +
                  "corner and drag it.\n" +
                  "Then select the people and run Assign Selected To Nearest Walkway.");
    }

    /// <summary>
    /// Puts whatever is selected onto the nearest pedestrian path. Selection-driven on purpose:
    /// there is no reliable way to tell a pedestrian model from a street prop by name, and
    /// guessing wrong means a bollard strolling down the pavement.
    /// </summary>
    [MenuItem("Tools/Emergency VR/Assign Selected To Nearest Walkway")]
    public static void AssignSelectedToWalkway()
    {
        var scene = SceneManager.GetActiveScene();

        var walkways = new List<TrafficPath>();
        foreach (var root in scene.GetRootGameObjects())
            foreach (var p in root.GetComponentsInChildren<TrafficPath>(true))
                if (p.kind == TrafficPath.PathKind.Pedestrians) walkways.Add(p);

        if (walkways.Count == 0)
        {
            Debug.LogError("[Traffic] No walkways in the scene. Run Add Walkway first.");
            return;
        }

        var picked = Selection.transforms;
        if (picked == null || picked.Length == 0)
        {
            Debug.LogWarning("[Traffic] Select the people in the Hierarchy first.");
            return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Assign to walkway");

        int done = 0;

        foreach (var t in picked)
        {
            var go = t.gameObject;

            TrafficPath best = NearestPath(walkways, t.position);
            if (best == null) continue;

            foreach (var vc in go.GetComponents<VehicleController>())
                if (vc.enabled) { Undo.RecordObject(vc, "Disable"); vc.enabled = false; }

            foreach (var av in go.GetComponents<AnimatedVehicleMover>())
                if (av.enabled) { Undo.RecordObject(av, "Disable"); av.enabled = false; }

            var driver = go.GetComponent<RoadDriver>();
            if (driver == null) driver = Undo.AddComponent<RoadDriver>(go);
            else Undo.RecordObject(driver, "Assign to walkway");

            driver.path = best;
            driver.speed = 1.3f;                 // a walking pace, not a jog
            driver.smoothing = 0.4f;

            var oldMover = go.GetComponent<AnimatedVehicleMover>();
            if (oldMover != null)
            {
                driver.reverseDirection = oldMover.reverseDirection;
                driver.facingYawOffset = -oldMover.forwardYawOffset;
            }

            driver.SnapToNearest();
            EditorUtility.SetDirty(driver);
            done++;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log($"[Traffic] {done} of {picked.Length} selected objects now walk on the nearest " +
                  "walkway. Scene saved.");
    }

    [MenuItem("Tools/Emergency VR/Clear Traffic Network")]
    public static void Clear()
    {
        var rootGo = GameObject.Find(kRootName);
        if (rootGo == null) { Debug.Log("[Traffic] Nothing to clear."); return; }

        if (!EditorUtility.DisplayDialog("Clear traffic network",
                "Delete the streets and the ring?\n\nVehicles stop moving until you rebuild.",
                "Delete", "Cancel"))
            return;

        Undo.DestroyObjectImmediate(rootGo);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[Traffic] Cleared.");
    }

    static TrafficPath NearestPath(List<TrafficPath> paths, Vector3 world)
    {
        TrafficPath best = null;
        float bestSqr = float.MaxValue;

        foreach (var p in paths)
        {
            if (p == null) continue;

            float len = p.Length;
            if (len <= 0.01f) continue;

            // 48 samples is plenty to decide which street something is standing on.
            for (int i = 0; i < 48; i++)
            {
                p.Sample(len * i / 48f, out Vector3 pos, out _);
                float d = Flat(pos - world).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; best = p; }
            }
        }

        return best;
    }

    // ================================================================ measuring

    static void MeasureRoundabout(Scene scene, Vector3 centre,
                                  out float islandRadius, out float pavementRadius)
    {
        islandRadius = 0f;
        pavementRadius = 0f;

        var discs = new List<float>();

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                Bounds b = r.bounds;

                if (Flat(b.center - centre).magnitude > 12f) continue;

                float radius = Mathf.Max(b.size.x, b.size.z) * 0.5f;
                if (radius < 6f || radius > 90f) continue;
                if (b.size.y > radius * 0.5f) continue;

                float ratio = Mathf.Min(b.size.x, b.size.z) / Mathf.Max(b.size.x, b.size.z);
                if (ratio < 0.8f) continue;

                discs.Add(radius);
            }
        }

        if (discs.Count == 0) return;

        discs.Sort();
        pavementRadius = discs[discs.Count - 1];
        islandRadius = discs[0] < pavementRadius ? discs[0] : pavementRadius * 0.5f;
    }

    static Vector3 SolveCentre(List<List<Transform>> spokes, Vector3 fallback)
    {
        float a11 = 0, a12 = 0, a22 = 0, b1 = 0, b2 = 0;
        int used = 0;

        foreach (var s in spokes)
        {
            Vector3 p = Mean(s);
            Vector3 d = Flat(p - fallback);
            if (d.sqrMagnitude < 1f) continue;
            d.Normalize();
            used++;

            float m11 = 1f - d.x * d.x;
            float m12 = -d.x * d.z;
            float m22 = 1f - d.z * d.z;

            a11 += m11; a12 += m12; a22 += m22;
            b1 += m11 * p.x + m12 * p.z;
            b2 += m12 * p.x + m22 * p.z;
        }

        float det = a11 * a22 - a12 * a12;
        if (used < 2 || Mathf.Abs(det) < 1e-4f) return fallback;

        return new Vector3((b1 * a22 - b2 * a12) / det, fallback.y, (a11 * b2 - a12 * b1) / det);
    }

    static List<List<Transform>> GroupIntoSpokes(List<Transform> far, Vector3 hub)
    {
        var groups = new List<List<Transform>>();

        foreach (var w in far.OrderByDescending(w => Flat(w.position - hub).magnitude))
        {
            float bearing = Bearing(Flat(w.position - hub));

            var match = groups.FirstOrDefault(g =>
                Mathf.Abs(Mathf.DeltaAngle(bearing, Bearing(Flat(g[0].position - hub))))
                    < kSpokeMergeDegrees);

            if (match != null) match.Add(w);
            else groups.Add(new List<Transform> { w });
        }

        return groups;
    }

    // ================================================================ helpers

    static Vector3 Mean(List<Transform> pts)
    {
        Vector3 m = Vector3.zero;
        foreach (var t in pts) m += t.position;
        return m / Mathf.Max(1, pts.Count);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    static float Bearing(Vector3 flatDir)
    {
        float a = Mathf.Atan2(flatDir.x, flatDir.z) * Mathf.Rad2Deg;
        return a < 0 ? a + 360f : a;
    }

    static Vector3 Median(List<Vector3> pts)
    {
        var xs = pts.Select(p => p.x).OrderBy(v => v).ToList();
        var zs = pts.Select(p => p.z).OrderBy(v => v).ToList();
        return new Vector3(xs[xs.Count / 2], pts[0].y, zs[zs.Count / 2]);
    }

    static bool IsMarkerName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        n = n.Trim();
        if (!n.StartsWith("w1")) return false;
        string rest = n.Substring(2).Trim();
        return rest.Length == 0 || (rest.StartsWith("(") && rest.EndsWith(")"));
    }
}
