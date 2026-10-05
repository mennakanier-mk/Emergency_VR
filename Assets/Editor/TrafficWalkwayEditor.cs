using UnityEditor;
using UnityEngine;

/// <summary>
/// Scene-view editing for a walkway: drag the corners, press + to add one, press - to remove one.
///
/// The + handles sit at the MIDPOINT of each stretch and just past each end, because that is
/// where you actually want a new corner - you see the path cutting across a flowerbed, you click
/// the + on that stretch, and you drag the new corner out of the way. Adding a point at the end
/// of a list and then hunting for it in the Scene view is the same operation done backwards.
///
/// The thick yellow ribbon is drawn from the same maths the people walk on, so if it is on the
/// pavement, they are on the pavement.
/// </summary>
[CustomEditor(typeof(TrafficWalkway))]
public class TrafficWalkwayEditor : Editor
{
    void OnSceneGUI()
    {
        var way = (TrafficWalkway)target;

        // The route itself.
        TrafficRoadEditor.DrawRibbon(way, way.gizmoColor, 4f);

        var centre = way.CentreLine();
        if (centre.Count < 2) return;

        // The centre line, faint, so the corners read as corners.
        Handles.color = new Color(way.gizmoColor.r, way.gizmoColor.g, way.gizmoColor.b, 0.4f);
        for (int i = 0; i < centre.Count - 1; i++)
            Handles.DrawDottedLine(centre[i], centre[i + 1], 4f);

        bool freeform = way.points != null && way.points.Count >= 2;

        // ---------------------------------------------------------------- move corners
        if (freeform)
        {
            for (int i = 0; i < way.points.Count; i++)
            {
                Vector3 world = way.transform.TransformPoint(way.points[i]);
                world.y = way.height;

                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(world, Quaternion.identity);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(way, "Move walkway corner");
                    way.points[i] = way.transform.InverseTransformPoint(
                        new Vector3(moved.x, way.height, moved.z));
                    way.ForceRebuild();
                    EditorUtility.SetDirty(way);
                }
            }
        }

        // ---------------------------------------------------------------- add corners
        Handles.color = Color.white;

        for (int i = 0; i < centre.Count - 1; i++)
        {
            Vector3 mid = (centre[i] + centre[i + 1]) * 0.5f;
            if (!PlusButton(mid, way.height)) continue;

            Undo.RecordObject(way, "Add walkway corner");
            EnsureFreeform(way, centre);
            way.points.Insert(i + 1, way.transform.InverseTransformPoint(mid));
            way.ForceRebuild();
            EditorUtility.SetDirty(way);
            return;                                   // list changed - redraw next frame
        }

        // ...and one just past each end, to extend the walkway.
        Vector3 startDir = (centre[0] - centre[1]).normalized;
        Vector3 endDir = (centre[centre.Count - 1] - centre[centre.Count - 2]).normalized;

        if (PlusButton(centre[0] + startDir * 6f, way.height))
        {
            Undo.RecordObject(way, "Extend walkway");
            EnsureFreeform(way, centre);
            way.points.Insert(0, way.transform.InverseTransformPoint(centre[0] + startDir * 6f));
            way.ForceRebuild();
            EditorUtility.SetDirty(way);
            return;
        }

        if (PlusButton(centre[centre.Count - 1] + endDir * 6f, way.height))
        {
            Undo.RecordObject(way, "Extend walkway");
            EnsureFreeform(way, centre);
            way.points.Add(way.transform.InverseTransformPoint(
                centre[centre.Count - 1] + endDir * 6f));
            way.ForceRebuild();
            EditorUtility.SetDirty(way);
            return;
        }

        // ---------------------------------------------------------------- remove corners
        if (freeform && way.points.Count > 2)
        {
            Handles.color = new Color(1f, 0.4f, 0.35f);

            for (int i = 0; i < way.points.Count; i++)
            {
                Vector3 world = way.transform.TransformPoint(way.points[i]);
                world.y = way.height;

                Vector3 at = world + Vector3.up * 2.5f;
                float size = HandleUtility.GetHandleSize(at) * 0.12f;

                if (!Handles.Button(at, Quaternion.identity, size, size, Handles.DotHandleCap))
                    continue;

                Undo.RecordObject(way, "Remove walkway corner");
                way.points.RemoveAt(i);
                way.ForceRebuild();
                EditorUtility.SetDirty(way);
                return;
            }
        }
    }

    /// <summary>
    /// A straight walkway keeps its points list empty so it needs no setup at all. The moment
    /// you add a corner it has to become a real list, seeded with the straight line it was, or
    /// the new corner would appear to teleport the path.
    /// </summary>
    static void EnsureFreeform(TrafficWalkway way, System.Collections.Generic.List<Vector3> centre)
    {
        if (way.points != null && way.points.Count >= 2) return;

        way.points = new System.Collections.Generic.List<Vector3>();
        foreach (var c in centre) way.points.Add(way.transform.InverseTransformPoint(c));
    }

    static bool PlusButton(Vector3 at, float height)
    {
        at.y = height + 1.5f;
        float size = HandleUtility.GetHandleSize(at) * 0.13f;
        return Handles.Button(at, Quaternion.identity, size, size, Handles.SphereHandleCap);
    }

    public override void OnInspectorGUI()
    {
        var way = (TrafficWalkway)target;

        DrawDefaultInspector();

        EditorGUILayout.Space();

        if (way.points != null && way.points.Count >= 2)
        {
            if (GUILayout.Button("Make It Straight Again (clears corners)"))
            {
                Undo.RecordObject(way, "Straighten walkway");
                way.points.Clear();
                way.ForceRebuild();
                EditorUtility.SetDirty(way);
            }
        }

        EditorGUILayout.HelpBox(
            "Move and rotate the object to place a straight walkway - that is all most of them " +
            "need.\n\n" +
            "To make it bend: press a white + in the Scene view to drop a corner there, then " +
            "drag the corner. + handles sit halfway along each stretch and just past each end, " +
            "so you can extend it as far as you like.\n\n" +
            "Red dots above a corner remove it. Closed Loop drops the turns at the ends, for a " +
            "circuit that already comes back on itself.",
            MessageType.Info);
    }
}
