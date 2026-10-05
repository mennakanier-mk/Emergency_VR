using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Scale Consistency > Report          (read-only)
///   Tools > Emergency VR > Scale Consistency > Apply To Selection
///   Tools > Emergency VR > Scale Consistency > Apply To All
///
/// Every model came in at its own scale (0.005, 1.5, 128, 155 ...), so a scale number says
/// nothing about how big a thing looks. This measures each one in metres and resizes it to a
/// real-world size for what it is:
///
///   adult 1.75 m tall · child 1.30 m tall
///   bicycle 1.75 m long · motorbike 2.1 m · scooter 1.85 m
///   car 4.5 m · taxi 4.6 m · van / police van 5.2 m · ambulance 5.6 m · fire engine 8.5 m
///   bus 11 m · truck 7.5 m · tuk-tuk 2.7 m
///
/// People are measured from their skinned mesh in its bind pose, so a seated or crouching pose
/// in the Scene view does not make them look short. Vehicles are measured by length without
/// the people riding in them. Resizing keeps the object's lowest point on the ground.
///
/// Anything that would need resizing by more than 3x either way is reported and left alone -
/// that is usually a wrong guess at what the object is, not a model at the wrong size.
/// Objects it cannot classify (horses, carts, props) are listed but never touched.
/// Every change is a normal Undo step.
/// </summary>
public static class ScaleConsistencyTool
{
    enum Kind { Unknown, Adult, Child, Bicycle, Motorbike, Scooter, Car, Taxi, Van, Ambulance,
                FireEngine, Bus, Truck, TukTuk }

    const string Root = "Tools/Emergency VR/Scale Consistency/";

    [MenuItem(Root + "Report")]
    static void Report() => Run(AllCandidates(), false);

    [MenuItem(Root + "Apply To Selection")]
    static void ApplySelection()
    {
        var list = new List<GameObject>();
        foreach (var g in Selection.gameObjects) list.Add(g);
        Run(list, true);
    }

    [MenuItem(Root + "Apply To Selection", true)]
    static bool ApplySelectionValidate() => Selection.gameObjects.Length > 0;

    [MenuItem(Root + "Apply To All")]
    static void ApplyAll()
    {
        if (!EditorUtility.DisplayDialog("Scale Consistency",
            "Resize every person, bicycle, motorbike and vehicle in the scene to real-world size?\n\n" +
            "Run Report first to see what will change. This is undoable.", "Apply", "Cancel"))
            return;
        Run(AllCandidates(), true);
    }

    // ------------------------------------------------------------------ main

    static void Run(List<GameObject> objects, bool apply)
    {
        if (apply && EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Scale] Exit Play mode first.");
            return;
        }

        // Vehicles first, then two-wheelers, then people: a driver parented under a car is
        // resized with the car, and then corrected on his own.
        objects.Sort((a, b) => Order(Classify(a)).CompareTo(Order(Classify(b))));

        float world = WorldScale();
        var log = new StringBuilder($"[Scale] {(apply ? "APPLY" : "REPORT")} - {objects.Count} objects, " +
                                    $"street scale x{world:0.00} (from the cars)\n");
        int changed = 0, skipped = 0;

        if (apply) Undo.SetCurrentGroupName("Scale Consistency");

        foreach (var go in objects)
        {
            if (go == null) continue;
            Kind k = Classify(go);
            if (k == Kind.Unknown)
            {
                log.AppendLine($"  ?  {go.name,-28} not recognised - left alone");
                continue;
            }

            bool person = k == Kind.Adult || k == Kind.Child;
            float target = TargetSize(k) * world;
            float measured = person ? MeasurePersonHeight(go) : MeasureVehicleLength(go);

            if (measured <= 0.001f)
            {
                log.AppendLine($"  !  {go.name,-28} {k}: nothing to measure");
                continue;
            }

            float factor = target / measured;
            string what = person ? "tall" : "long";

            if (factor > 3f || factor < 1f / 3f)
            {
                log.AppendLine($"  !! {go.name,-28} {k}: {measured:0.00} m {what}, would need x{factor:0.00} " +
                               "- too far off, check it by hand");
                skipped++;
                continue;
            }

            if (Mathf.Abs(factor - 1f) < 0.04f)
            {
                log.AppendLine($"  ok {go.name,-28} {k}: {measured:0.00} m {what}");
                continue;
            }

            log.AppendLine($"  -> {go.name,-28} {k}: {measured:0.00} m -> {target:0.00} m {what} (x{factor:0.00})");

            if (apply)
            {
                Resize(go.transform, factor);
                changed++;
            }
        }

        if (apply && changed > 0)
        {
            var scene = SceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
        }

        log.AppendLine($"\n  {(apply ? "resized" : "would resize")}: {(apply ? changed : -1)}   " +
                       $"too far off (left alone): {skipped}");
        string text = log.ToString().Replace("would resize: -1", "see -> lines");
        Debug.Log(text);
        try { System.IO.File.WriteAllText("Logs/ScaleReport.txt", text); } catch { }
    }

    static void Resize(Transform t, float factor)
    {
        float groundBefore = LowestPoint(t);

        Undo.RecordObject(t, "Scale Consistency");
        t.localScale *= factor;     // keeps any mirror (negative) axis as it is

        float groundAfter = LowestPoint(t);
        if (!float.IsInfinity(groundBefore) && !float.IsInfinity(groundAfter))
            t.position += Vector3.up * (groundBefore - groundAfter);

        PrefabUtility.RecordPrefabInstancePropertyModifications(t);
    }

    /// <summary>
    /// The street was not built in metres: the ordinary cars on it are ~1.7x real size and fit
    /// the lanes and buildings. Rather than shrink every car (and leave the city oversized),
    /// everything else is sized to match the cars: the median ratio of car length to a real car.
    /// </summary>
    static float WorldScale()
    {
        var ratios = new List<float>();
        foreach (var go in AllCandidates())
        {
            if (go == null || Classify(go) != Kind.Car) continue;
            float m = MeasureVehicleLength(go);
            if (m > 0.01f) ratios.Add(m / TargetSize(Kind.Car));
        }
        if (ratios.Count == 0) return 1f;
        ratios.Sort();
        return ratios[ratios.Count / 2];
    }

    // ------------------------------------------------------------------ candidates

    static List<GameObject> AllCandidates()
    {
        var set = new HashSet<GameObject>();
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (Excluded(root.name) && root.GetComponentInChildren<RoadDriver>(true) == null) continue;

            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string n = mb.GetType().Name;
                if (n == "RoadDriver" || n == "PedestrianWalker" || n == "VehicleController" ||
                    n == "AnimatedVehicleMover" || n == "AccidentCar" || n == "GroundFollowAnimated")
                    set.Add(mb.gameObject);
            }

            // Animated characters with no mover (standing people, the driver in the car).
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
            {
                if (a == null || Excluded(a.name)) continue;
                var top = PrefabUtility.GetNearestPrefabInstanceRoot(a.gameObject) ?? a.gameObject;
                if (top.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) set.Add(top);
            }
        }
        return new List<GameObject>(set);
    }

    static bool Excluded(string name)
    {
        string n = name.ToLowerInvariant();
        return n.Contains("xr ") || n.Contains("xr origin") || n.Contains("hand") ||
               n.Contains("list item") || n.Contains("environment") || n.Contains("camera") ||
               n.Contains("eventsystem") || n.Contains("light");
    }

    // ------------------------------------------------------------------ classification

    static Kind Classify(GameObject go)
    {
        string n = go.name.ToLowerInvariant();

        if (n.Contains("bicycle") || n.Contains("bike")) return Kind.Bicycle;
        if (n.Contains("scooter")) return Kind.Scooter;
        if (n.Contains("moto")) return Kind.Motorbike;
        if (n.Contains("hourse") || n.Contains("horse") || n.Contains("donkey") ||
            n.Contains("carriage") || n.Contains("cart")) return Kind.Unknown;

        // A character on its own (only skinned meshes, no vehicle body) is a person, whatever
        // the name says - "try_car_exit_man" is a man, not a car.
        if (OnlySkinned(go))
        {
            if (n.Contains("boy") || n.Contains("girl") || n.Contains("child") || n.Contains("kid"))
                return Kind.Child;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                string c = t.name.ToLowerInvariant();
                if (c == "boy" || c.StartsWith("boy_") || c.Contains("child") || c.Contains("kid"))
                    return Kind.Child;
            }
            return Kind.Adult;
        }

        if (n.Contains("fire") && (n.StartsWith("veh") || n.Contains("truck") || n.Contains("engine")))
            return Kind.FireEngine;
        if (n.Contains("amb")) return Kind.Ambulance;
        if (n.Contains("bus")) return Kind.Bus;
        if (n.Contains("tuk")) return Kind.TukTuk;
        if (n.Contains("van")) return Kind.Van;
        if (n.Contains("truck") || n.Contains("lorry")) return Kind.Truck;
        if (n.Contains("taxi")) return Kind.Taxi;

        bool hasSkin = go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        bool isVehicle = n.StartsWith("veh") || n.Contains("car") || n.Contains("sedan") ||
                         n.Contains("wagon") || go.GetComponent("VehicleController") != null ||
                         go.GetComponent("AccidentCar") != null;

        if (isVehicle) return Kind.Car;

        if (hasSkin)
        {
            if (n.Contains("boy") || n.Contains("girl") || n.Contains("child") || n.Contains("kid"))
                return Kind.Child;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                string c = t.name.ToLowerInvariant();
                if (c == "boy" || c.StartsWith("boy_") || c.Contains("child") || c.Contains("kid"))
                    return Kind.Child;
            }
            return Kind.Adult;
        }

        return Kind.Unknown;
    }

    /// <summary>
    /// True when everything this object draws is a skinned character (props it holds, like a
    /// ball, are small mesh renderers and do not count).
    /// </summary>
    static bool OnlySkinned(GameObject go)
    {
        float skinned = 0f, rigid = 0f;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r is ParticleSystemRenderer) continue;
            float v = r.bounds.size.x * r.bounds.size.y * r.bounds.size.z;
            if (r is SkinnedMeshRenderer) skinned += v; else rigid += v;
        }
        return skinned > 0f && rigid < skinned * 0.5f;
    }

    static int Order(Kind k)
    {
        switch (k)
        {
            case Kind.Adult: case Kind.Child: return 2;
            case Kind.Bicycle: case Kind.Motorbike: case Kind.Scooter: return 1;
            default: return 0;
        }
    }

    static float TargetSize(Kind k)
    {
        switch (k)
        {
            case Kind.Adult: return 1.75f;
            case Kind.Child: return 1.30f;
            case Kind.Bicycle: return 1.75f;
            case Kind.Motorbike: return 2.10f;
            case Kind.Scooter: return 1.85f;
            case Kind.Car: return 4.50f;
            case Kind.Taxi: return 4.60f;
            case Kind.Van: return 5.20f;
            case Kind.Ambulance: return 5.60f;
            case Kind.FireEngine: return 8.50f;
            case Kind.Bus: return 11.0f;
            case Kind.Truck: return 7.50f;
            case Kind.TukTuk: return 2.70f;
        }
        return 0f;
    }

    // ------------------------------------------------------------------ measuring

    /// <summary>
    /// Height from the skinned mesh's bind pose: the largest dimension of the mesh, in world
    /// units. A standing / A-pose body's largest dimension is its height; in a T-pose it is the
    /// arm span, which is about the same. Unaffected by the pose shown in the Scene view.
    /// </summary>
    static float MeasurePersonHeight(GameObject go)
    {
        float best = 0f;
        foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh == null) continue;
            Vector3 s = smr.sharedMesh.bounds.size;
            Vector3 ls = smr.transform.lossyScale;
            float k = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            float h = Mathf.Max(s.x, Mathf.Max(s.y, s.z)) * k;
            if (h > best) best = h;
        }
        return best;
    }

    /// <summary>Longest horizontal side of the vehicle, without anyone riding in it.</summary>
    static float MeasureVehicleLength(GameObject go)
    {
        Bounds b = default; bool any = false;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            if (r is SkinnedMeshRenderer) continue;                 // riders / drivers
            var a = r.GetComponentInParent<Animator>();
            if (a != null && a.gameObject != go &&
                a.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) continue;
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        if (!any) return 0f;
        return Mathf.Max(b.size.x, b.size.z);
    }

    static float LowestPoint(Transform t)
    {
        float low = float.PositiveInfinity;
        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r is ParticleSystemRenderer) continue;
            if (r.bounds.min.y < low) low = r.bounds.min.y;
        }
        return low;
    }
}
