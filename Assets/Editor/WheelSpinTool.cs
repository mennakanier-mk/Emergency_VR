using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Add Wheel Spin To All Wheels
///   Tools > Emergency VR > Add Wheel Spin To Selection
///   Tools > Emergency VR > Remove All Wheel Spin
///
/// Puts a WheelSpin on every wheel in the scene and takes wheel rolling away from
/// VehicleController, so only one thing is writing each wheel's rotation.
///
/// That second part matters. VehicleController writes localRotation in LateUpdate and WheelSpin
/// writes it in LateUpdate too; leave both active and whichever runs last wins, which is a coin
/// toss decided by script execution order. Clearing the Front/Rear Wheels lists makes
/// VehicleController skip its wheel code entirely - it still drives the car, it just no longer
/// has an opinion about the wheels.
/// </summary>
public static class WheelSpinTool
{
    [MenuItem("Tools/Emergency VR/Add Wheel Spin To All Wheels")]
    public static void AddToAll()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[WheelSpin] Exit Play mode first.");
            return;
        }

        var scene = SceneManager.GetActiveScene();
        var wheels = new List<Transform>();

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!IsWheelName(t.name)) continue;

                // Take the highest object that still reads as a wheel, so we spin the wheel root
                // and not a mesh child inside it - spinning the child leaves the rim behind.
                if (t.parent != null && IsWheelName(t.parent.name)) continue;

                if (!wheels.Contains(t)) wheels.Add(t);
            }
        }

        if (wheels.Count == 0)
        {
            Debug.LogWarning("[WheelSpin] No objects named *wheel* / *tire* / *tyre* in this " +
                             "scene. Select the wheel objects and use 'Add Wheel Spin To " +
                             "Selection' instead.");
            return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Add wheel spin");

        int added = 0, already = 0;
        var log = new StringBuilder("[WheelSpin]\n");

        foreach (var w in wheels)
        {
            if (w.GetComponent<WheelSpin>() != null) { already++; continue; }
            Undo.AddComponent<WheelSpin>(w.gameObject);
            added++;
        }

        int silenced = SilenceVehicleControllers(scene, log);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        log.AppendLine($"  wheels found      {wheels.Count}");
        log.AppendLine($"  WheelSpin added   {added}");
        log.AppendLine($"  already had one   {already}");
        log.AppendLine($"  VehicleControllers stopped from touching wheels: {silenced}");
        log.AppendLine();
        log.AppendLine("  Press Play. Any wheel that still does not turn is a wheel that is not " +
                       "moving - tick Debug on it to see its speed.");

        Debug.Log(log.ToString());
    }

    [MenuItem("Tools/Emergency VR/Add Wheel Spin To Selection")]
    public static void AddToSelection()
    {
        var picked = Selection.transforms;
        if (picked == null || picked.Length == 0)
        {
            Debug.LogWarning("[WheelSpin] Select the wheel objects in the Hierarchy first.");
            return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Add wheel spin to selection");

        int added = 0;
        foreach (var t in picked)
        {
            if (t.GetComponent<WheelSpin>() != null) continue;
            Undo.AddComponent<WheelSpin>(t.gameObject);
            added++;
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"[WheelSpin] Added to {added} of {picked.Length} selected objects.");
    }

    [MenuItem("Tools/Emergency VR/Remove All Wheel Spin")]
    public static void RemoveAll()
    {
        var scene = SceneManager.GetActiveScene();

        int removed = 0;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var ws in root.GetComponentsInChildren<WheelSpin>(true))
            {
                Undo.DestroyObjectImmediate(ws);
                removed++;
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"[WheelSpin] Removed {removed} WheelSpin components.");
    }

    /// <summary>
    /// Empties each VehicleController's wheel lists so it stops writing wheel rotations. The car
    /// still drives; it just leaves the wheels to WheelSpin.
    /// </summary>
    static int SilenceVehicleControllers(Scene scene, StringBuilder log)
    {
        int n = 0;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var vc in root.GetComponentsInChildren<VehicleController>(true))
            {
                var so = new SerializedObject(vc);

                var front = so.FindProperty("frontWheels");
                var rear = so.FindProperty("rearWheels");
                var auto = so.FindProperty("autoDetectWheels");

                bool had = (front != null && front.arraySize > 0) ||
                           (rear != null && rear.arraySize > 0) ||
                           (auto != null && auto.boolValue);

                if (!had) continue;

                if (front != null) front.ClearArray();
                if (rear != null) rear.ClearArray();
                if (auto != null) auto.boolValue = false;   // or Start() refills them

                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(vc);
                n++;
            }
        }

        return n;
    }

    static bool IsWheelName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        string s = n.ToLowerInvariant();
        return s.Contains("wheel") || s.Contains("tire") || s.Contains("tyre");
    }
}
