using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for PedestrianWalker: says what to do, warns when the one required field is empty,
/// and adds the two buttons that save a trip to the menu bar.
///
/// The empty-path warning matters more than it looks. A person with no path simply stands
/// there, which is indistinguishable from a person whose walkway is misplaced, or one the step
/// detector has not noticed yet - so the component says which it is rather than leaving you to
/// guess from a still scene.
/// </summary>
[CustomEditor(typeof(PedestrianWalker))]
[CanEditMultipleObjects]
public class PedestrianWalkerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var walker = (PedestrianWalker)target;

        EditorGUILayout.Space();

        if (walker.path == null)
        {
            EditorGUILayout.HelpBox(
                "No path yet - this person will stand still.\n\n" +
                "Drag a Walkway object from the Hierarchy into the Path slot above. If there " +
                "isn't one, make it with Tools > Emergency VR > Add Walkway.",
                MessageType.Warning);

            if (GUILayout.Button("Use The Nearest Walkway"))
            {
                foreach (var t in targets)
                {
                    var w = (PedestrianWalker)t;
                    var found = NearestWalkway(w.transform.position);
                    if (found == null) continue;

                    Undo.RecordObject(w, "Assign walkway");
                    w.path = found;
                    w.SnapToNearest();
                    EditorUtility.SetDirty(w);
                }
            }

            return;
        }

        if (walker.path.kind != TrafficPath.PathKind.Pedestrians)
        {
            EditorGUILayout.HelpBox(
                $"'{walker.path.name}' is a vehicle path. People will walk down the middle of " +
                "the road on it. Use a Walkway instead, or change that path's Kind to " +
                "Pedestrians if it really is a footpath.",
                MessageType.Warning);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Snap To Path"))
            {
                foreach (var t in targets)
                {
                    var w = (PedestrianWalker)t;
                    Undo.RecordObject(w, "Snap to path");
                    w.SnapToNearest();
                    EditorUtility.SetDirty(w);
                }
            }

            if (GUILayout.Button("Face The Other Way"))
            {
                foreach (var t in targets)
                {
                    var w = (PedestrianWalker)t;
                    Undo.RecordObject(w, "Face the other way");
                    w.reverseDirection = !w.reverseDirection;
                    EditorUtility.SetDirty(w);
                }
            }
        }

        EditorGUILayout.HelpBox(
            "Drop this on a person, drag a Walkway into Path, press Play.\n\n" +
            "They start from where they stand, keep their distance from whoever is in front, " +
            "and keep the rotation you authored - Reverse Direction and Facing Yaw Offset turn " +
            "the model without touching the route.",
            MessageType.Info);
    }

    static TrafficPath NearestWalkway(Vector3 world)
    {
        TrafficPath best = null;
        float bestSqr = float.MaxValue;

        foreach (var p in Object.FindObjectsByType<TrafficPath>(FindObjectsInactive.Include,
                                                               FindObjectsSortMode.None))
        {
            if (p == null || p.kind != TrafficPath.PathKind.Pedestrians) continue;

            float len = p.Length;
            if (len <= 0.01f) continue;

            for (int i = 0; i < 48; i++)
            {
                p.Sample(len * i / 48f, out Vector3 pos, out _);

                Vector3 d = pos - world; d.y = 0f;
                if (d.sqrMagnitude < bestSqr) { bestSqr = d.sqrMagnitude; best = p; }
            }
        }

        return best;
    }
}
