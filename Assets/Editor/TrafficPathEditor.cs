using UnityEditor;
using UnityEngine;

/// <summary>
/// Draws the driving line as a thick, readable ribbon in the Scene view.
///
/// Gizmos.DrawLine is one pixel wide and washes out against tarmac, which made "is the lane on
/// the road?" a question you had to squint at. Handles.DrawAAPolyLine takes a thickness, so the
/// line is as obvious as the paint on the street - and since it is drawn from the same Sample()
/// the cars drive on, what you see is exactly where they will go.
///
/// Also puts a move handle on each end of the street, so its length can be dragged rather than
/// typed.
/// </summary>
[CustomEditor(typeof(TrafficRoad))]
public class TrafficRoadEditor : Editor
{
    void OnSceneGUI()
    {
        var road = (TrafficRoad)target;
        DrawRibbon(road, road.gizmoColor, 5f);

        Vector3 f = Flat(road.transform.forward);
        if (f.sqrMagnitude < 0.0001f) f = Vector3.forward;
        f.Normalize();

        Vector3 c = road.transform.position;
        c.y = road.height;

        // Drag either end to set the length.
        EditorGUI.BeginChangeCheck();

        Handles.color = road.gizmoColor;
        Vector3 aOld = c + f * road.length * 0.5f;
        Vector3 bOld = c - f * road.length * 0.5f;

        Vector3 a = Handles.Slider(aOld, f, HandleUtility.GetHandleSize(aOld) * 0.5f,
                                   Handles.ConeHandleCap, 0f);
        Vector3 b = Handles.Slider(bOld, -f, HandleUtility.GetHandleSize(bOld) * 0.5f,
                                   Handles.ConeHandleCap, 0f);

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(road, "Resize street");

            // Keep the untouched end still: the centre moves by half of whatever changed.
            float lenA = Vector3.Dot(a - bOld, f);
            float lenB = Vector3.Dot(aOld - b, f);

            bool movedA = Mathf.Abs(lenA - road.length) > Mathf.Abs(lenB - road.length);
            float newLen = Mathf.Max(10f, movedA ? lenA : lenB);

            Vector3 fixedEnd = movedA ? bOld : aOld;
            float sign = movedA ? 1f : -1f;

            road.length = newLen;
            road.transform.position = fixedEnd + f * (sign * newLen * 0.5f);

            EditorUtility.SetDirty(road);
        }
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Move and rotate this object until the two thick lines sit on the tarmac - that is " +
            "exactly where the cars drive.\n\n" +
            "Lane Offset is half the gap between the two directions, and also the radius of the " +
            "turn at each end. Widen it until each line sits in the middle of its own " +
            "carriageway.\n\n" +
            "Drag the cone handles at either end to change the length.",
            MessageType.Info);
    }

    public static void DrawRibbon(TrafficPath path, Color color, float thickness)
    {
        float len = path.Length;
        if (len <= 0.01f) return;

        int samples = 160;
        var pts = new Vector3[samples + 1];

        for (int i = 0; i <= samples; i++)
        {
            path.Sample(len * i / samples, out Vector3 p, out _);
            pts[i] = p;
        }

        Handles.color = color;
        Handles.DrawAAPolyLine(thickness, pts);

        // Direction of travel, a third of the way along so it is not hidden under a car.
        path.Sample(len * 0.33f, out Vector3 at, out Vector3 dir);
        Handles.ConeHandleCap(0, at + dir * 2f, Quaternion.LookRotation(dir), 3f, EventType.Repaint);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
}

[CustomEditor(typeof(TrafficRing))]
public class TrafficRingEditor : Editor
{
    void OnSceneGUI()
    {
        var ring = (TrafficRing)target;
        TrafficRoadEditor.DrawRibbon(ring, ring.gizmoColor, 5f);

        Vector3 c = ring.transform.position;
        c.y = ring.height;

        EditorGUI.BeginChangeCheck();
        Handles.color = ring.gizmoColor;
        float r = Handles.RadiusHandle(Quaternion.identity, c, ring.radius);

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(ring, "Resize roundabout");
            ring.radius = Mathf.Max(2f, r);
            EditorUtility.SetDirty(ring);
        }
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Put this object at the centre of the island and drag the circle handle until the " +
            "thick line sits in the middle of the tarmac ring.\n\n" +
            "Cars on the roundabout go round it and never leave, so it cannot jam.",
            MessageType.Info);
    }
}
