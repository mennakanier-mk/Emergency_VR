using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A path for people on foot. Straight out of the box, bendy when you need it.
///
/// Place it, point it down the pavement, and it is a straight walkway - that alone covers most
/// of the map. When a pavement turns a corner, goes round a fountain or cuts across a square,
/// add points and drag them: the path follows whatever shape you draw, so it can go absolutely
/// anywhere without ever leaving the pavement.
///
/// Shaped as a loop like the roads are, for the same reason: people walk up one side and back
/// down the other rather than meeting head on, and a loop means nobody ever reaches "the end"
/// and stops. The turn at each end is a half-circle the width of the path.
///
/// Set Closed Loop when the shape already comes back to where it started - a circuit round a
/// square - and the end turns are dropped.
///
/// Everything else (spacing, who is in front, the driver that moves people along it) is shared
/// with the roads, so a pedestrian is just a slower thing on a narrower path.
/// </summary>
[ExecuteAlways]
public class TrafficWalkway : TrafficPath
{
    [Header("Shape")]
    [Tooltip("Corner points, relative to this object. Leave empty for a straight walkway - the " +
             "editor adds them when you press the + handles in the Scene view.")]
    public List<Vector3> points = new List<Vector3>();

    [Tooltip("Length of the straight walkway used when there are fewer than two points.")]
    public float length = 60f;

    [Tooltip("Half the width of the walkway. People going one way use one side, people coming " +
             "back use the other.")]
    public float laneOffset = 1.2f;

    [Tooltip("Tick when the shape already returns to its start, such as a circuit round a " +
             "square. The end turns are then not needed.")]
    public bool closedLoop = false;

    [Tooltip("Height the people are placed at - the pavement surface.")]
    public float height = 0f;

    [Range(3, 9)] public int turnPoints = 5;

    public Color gizmoColor = new Color(1f, 0.85f, 0.2f);

    // ---------------------------------------------------------------- cache

    // The loop is rebuilt into a flat polyline with running distances, and Sample() just walks
    // it. That is what lets one piece of code handle a straight line and a six-corner path
    // through a market with no special cases.
    readonly List<Vector3> _loop = new List<Vector3>();
    readonly List<float> _cum = new List<float>();
    int _stamp;

    public override float Length
    {
        get { Rebuild(); return _cum.Count > 0 ? _cum[_cum.Count - 1] : 0f; }
    }

    public override void Sample(float s, out Vector3 position, out Vector3 forward)
    {
        Rebuild();

        float total = _cum.Count > 0 ? _cum[_cum.Count - 1] : 0f;

        if (total <= 0.001f || _loop.Count < 2)
        {
            position = transform.position;
            forward = Vector3.forward;
            return;
        }

        s = Mathf.Repeat(s, total);

        // Walk the running totals to find the segment. Few enough points that a scan beats
        // anything cleverer.
        int i = 0;
        while (i < _cum.Count - 2 && _cum[i + 1] < s) i++;

        float segLen = Mathf.Max(0.0001f, _cum[i + 1] - _cum[i]);
        float t = Mathf.Clamp01((s - _cum[i]) / segLen);

        Vector3 a = _loop[i];
        Vector3 b = _loop[(i + 1) % _loop.Count];

        position = Vector3.Lerp(a, b, t);
        position.y = height;

        forward = b - a;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
    }

    /// <summary>Distance along the loop nearest a world point, so somebody joins where they stand.</summary>
    public float NearestDistance(Vector3 world)
    {
        Rebuild();

        float best = 0f, bestSqr = float.MaxValue;
        float total = Length;
        if (total <= 0.001f) return 0f;

        int steps = Mathf.Clamp(Mathf.RoundToInt(total / 2f), 32, 512);

        for (int i = 0; i < steps; i++)
        {
            float s = total * i / steps;
            Sample(s, out Vector3 p, out _);

            Vector3 d = p - world; d.y = 0f;
            if (d.sqrMagnitude < bestSqr) { bestSqr = d.sqrMagnitude; best = s; }
        }

        return best;
    }

    // ---------------------------------------------------------------- building

    /// <summary>The centre line in world space, straight if there are not enough points.</summary>
    public List<Vector3> CentreLine()
    {
        var list = new List<Vector3>();

        if (points != null && points.Count >= 2)
        {
            foreach (var p in points)
            {
                Vector3 w = transform.TransformPoint(p);
                w.y = height;
                list.Add(w);
            }
            return list;
        }

        Vector3 f = transform.forward; f.y = 0f;
        f = f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;

        Vector3 c = transform.position; c.y = height;
        float half = Mathf.Max(2f, length) * 0.5f;

        list.Add(c - f * half);
        list.Add(c + f * half);
        return list;
    }

    int Stamp()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + transform.position.GetHashCode();
            h = h * 31 + transform.rotation.GetHashCode();
            h = h * 31 + laneOffset.GetHashCode();
            h = h * 31 + length.GetHashCode();
            h = h * 31 + height.GetHashCode();
            h = h * 31 + closedLoop.GetHashCode();
            h = h * 31 + turnPoints;
            h = h * 31 + (points == null ? 0 : points.Count);

            if (points != null)
                for (int i = 0; i < points.Count; i++) h = h * 31 + points[i].GetHashCode();

            return h;
        }
    }

    void Rebuild(bool force = false)
    {
        int stamp = Stamp();
        if (!force && stamp == _stamp && _loop.Count > 0) return;
        _stamp = stamp;

        _loop.Clear();
        _cum.Clear();

        var centre = CentreLine();
        if (centre.Count < 2) return;

        var normals = Normals(centre);

        if (closedLoop)
        {
            // Already a circuit: one lap on one side is the whole path.
            for (int i = 0; i < centre.Count; i++)
                _loop.Add(centre[i] + normals[i] * laneOffset);
        }
        else
        {
            for (int i = 0; i < centre.Count; i++)
                _loop.Add(centre[i] + normals[i] * laneOffset);

            AddTurn(centre[centre.Count - 1],
                    Direction(centre[centre.Count - 2], centre[centre.Count - 1]),
                    normals[normals.Count - 1]);

            for (int i = centre.Count - 1; i >= 0; i--)
                _loop.Add(centre[i] - normals[i] * laneOffset);

            AddTurn(centre[0], Direction(centre[1], centre[0]), -normals[0]);
        }

        // Running distances, with the closing segment included so the loop wraps cleanly.
        _cum.Add(0f);
        for (int i = 1; i <= _loop.Count; i++)
        {
            Vector3 a = _loop[i - 1];
            Vector3 b = _loop[i % _loop.Count];
            _cum.Add(_cum[i - 1] + Vector3.Distance(a, b));
        }
    }

    /// <summary>
    /// Sideways direction at each corner. At a bend it is the average of the two segments'
    /// normals, which is what keeps the two sides of the path parallel round a corner instead
    /// of pinching together on the inside.
    /// </summary>
    List<Vector3> Normals(List<Vector3> centre)
    {
        var normals = new List<Vector3>(centre.Count);

        for (int i = 0; i < centre.Count; i++)
        {
            Vector3 n = Vector3.zero;

            if (i > 0) n += Perp(Direction(centre[i - 1], centre[i]));
            if (i < centre.Count - 1) n += Perp(Direction(centre[i], centre[i + 1]));

            n.y = 0f;
            normals.Add(n.sqrMagnitude > 0.0001f ? n.normalized : Vector3.right);
        }

        return normals;
    }

    void AddTurn(Vector3 pivot, Vector3 heading, Vector3 side)
    {
        float r = Mathf.Max(laneOffset, 0.3f);
        int n = Mathf.Max(3, turnPoints);

        for (int i = 1; i < n; i++)
        {
            float a = Mathf.PI * i / n;
            Vector3 p = pivot + side * (Mathf.Cos(a) * r) + heading * (Mathf.Sin(a) * r);
            p.y = height;
            _loop.Add(p);
        }
    }

    static Vector3 Direction(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from; d.y = 0f;
        return d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.forward;
    }

    static Vector3 Perp(Vector3 forward)
    {
        return new Vector3(forward.z, 0f, -forward.x);
    }

    [ContextMenu("Rebuild Path")]
    public void ForceRebuild() { Rebuild(true); }

    void OnValidate() { Rebuild(true); }

    void OnDrawGizmos()
    {
        DrawPath(gizmoColor, 128);
    }
}
