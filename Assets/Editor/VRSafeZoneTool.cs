using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Add Walkable Zone
///   Tools > Emergency VR > Report Walkable Zones
///
/// Creates a VRSafeZone box centred on the player rig and sized to the streets around it, then
/// selects it so you can drag and resize it in the Scene view with the handles below.
///
/// Add more for each street or square; overlapping boxes join into one walkable network, so an
/// L-shaped junction is simply two boxes that overlap in the corner.
/// </summary>
public static class VRSafeZoneTool
{
    [MenuItem("Tools/Emergency VR/Add Walkable Zone")]
    public static void AddZone()
    {
        var scene = SceneManager.GetActiveScene();

        Transform rig = FindRig(scene);

        var go = new GameObject("Walkable Zone");
        Undo.RegisterCreatedObjectUndo(go, "Add walkable zone");

        var zone = go.AddComponent<VRSafeZone>();

        if (rig != null)
        {
            go.transform.position = new Vector3(rig.position.x, rig.position.y, rig.position.z);
            zone.size = new Vector3(40f, 4f, 40f);
        }

        // Number them so several zones stay tellable apart in the Hierarchy.
        int count = 0;
        foreach (var root in scene.GetRootGameObjects())
            count += root.GetComponentsInChildren<VRSafeZone>(true).Length;
        go.name = $"Walkable Zone {count}";

        Selection.activeObject = go;
        SceneView.lastActiveSceneView?.FrameSelected();

        EditorSceneManager.MarkSceneDirty(scene);

        Debug.Log($"[VRSafeZone] Added '{go.name}'" +
                  (rig != null ? $" at the rig ({rig.name})." : " at the origin - no rig found.") +
                  " Drag the green handles in the Scene view to shape it over the road. Add more " +
                  "zones for more streets; overlapping boxes join up.", go);
    }

    [MenuItem("Tools/Emergency VR/Report Walkable Zones")]
    public static void Report()
    {
        var scene = SceneManager.GetActiveScene();
        var sb = new System.Text.StringBuilder("[VRSafeZone] zones in this scene\n");

        int n = 0;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var z in root.GetComponentsInChildren<VRSafeZone>(true))
            {
                var b = z.WorldBounds;
                sb.AppendLine($"  {z.name,-24} {(z.active ? "on " : "off")} " +
                              $"centre ({b.center.x:0.0}, {b.center.z:0.0})  " +
                              $"{b.size.x:0.0} x {b.size.z:0.0} m");
                n++;
            }
        }

        if (n == 0)
            sb.AppendLine("  none - the wearer can walk anywhere, including off the map.");

        Debug.Log(sb.ToString());
    }

    static Transform FindRig(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            var loco = root.GetComponentInChildren<PhoneVRLocomotion>(true);
            if (loco != null) return loco.transform;
        }

        foreach (var root in scene.GetRootGameObjects())
        {
            var cc = root.GetComponentInChildren<CharacterController>(true);
            if (cc != null) return cc.transform;
        }

        return null;
    }
}

/// <summary>
/// Scene-view handles for the box. Unity's own BoxBoundsHandle gives the same six faces you drag
/// on a BoxCollider, so shaping a zone feels like shaping a collider rather than typing numbers.
/// </summary>
[CustomEditor(typeof(VRSafeZone))]
public class VRSafeZoneEditor : Editor
{
    readonly UnityEditor.IMGUI.Controls.BoxBoundsHandle _handle =
        new UnityEditor.IMGUI.Controls.BoxBoundsHandle();

    void OnSceneGUI()
    {
        var zone = (VRSafeZone)target;

        _handle.center = zone.transform.position + zone.center;
        _handle.size = zone.size;
        _handle.SetColor(zone.gizmoColor);

        EditorGUI.BeginChangeCheck();
        _handle.DrawHandle();

        if (!EditorGUI.EndChangeCheck()) return;

        Undo.RecordObject(zone, "Resize walkable zone");
        zone.center = _handle.center - zone.transform.position;
        zone.size = _handle.size;
        EditorUtility.SetDirty(zone);
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Drag the handles in the Scene view to shape this over the road.\n\n" +
            "Only the footprint (X and Z) is enforced - height is left to gravity.\n\n" +
            "Overlapping zones join into one walkable area, so build junctions out of two boxes.",
            MessageType.Info);
    }
}
