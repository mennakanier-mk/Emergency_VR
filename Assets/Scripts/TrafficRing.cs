using UnityEngine;

/// <summary>
/// The roundabout lane: a circle, driven round and round.
///
/// Put the object at the centre of the flowerbed and set Radius so the gizmo sits in the middle
/// of the tarmac. Two numbers, and what you see is where the cars go.
///
/// Anticlockwise by default because Egypt drives on the right.
/// </summary>
[ExecuteAlways]
public class TrafficRing : TrafficPath
{
    [Tooltip("Radius of the lane the cars drive on - the middle of the tarmac, not the island.")]
    public float radius = 20f;

    [Tooltip("Egypt drives on the right, so roundabouts run anticlockwise.")]
    public bool clockwise = false;

    [Tooltip("Height the vehicles are placed at.")]
    public float height = 0f;

    public Color gizmoColor = new Color(0.25f, 0.75f, 1f);

    [Tooltip("At Play, space the cars evenly round the circle (keeping their order) and fit the " +
             "following gaps to how many there are, so a full roundabout keeps flowing instead of " +
             "creeping or locking up.")]
    public bool keepFlowing = true;

    int _spreadSession = -1;
    static int _session;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void NewSession() { _session++; }

    void Update() { if (Application.isPlaying) EnsureSpread(); }

    /// <summary>Called by the ring's cars on their first frame too, so it runs even if this
    /// object's own Update does not.</summary>
    public void EnsureSpread()
    {
        if (!keepFlowing || _spreadSession == _session) return;
        _spreadSession = _session;     // once per Play: every RoadDriver.Start has run and registered

        var list = new System.Collections.Generic.List<RoadDriver>();
        foreach (var d in Drivers) if (d != null && !d.freeRoam) list.Add(d);
        int n = list.Count;
        if (n == 0) { _spreadSession = -1; return; }      // nobody registered yet: try again next frame

        float len = Length, step = len / n;
        list.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        float start = list[0].Distance;
        for (int i = 0; i < n; i++) list[i].Teleport(start + step * i);

        // Gaps are centre to centre; leave each car room to run near full speed at even spacing.
        float maxHalf = 2.2f;
        foreach (var d in list) maxHalf = Mathf.Max(maxHalf, d.HalfLength);
        float excess = Mathf.Max(0f, maxHalf * 2f - 4.4f);
        float usable = step - excess;
        if (minGap > usable * 0.7f) minGap = Mathf.Max(4.6f, usable * 0.7f);
        if (slowGap > usable * 0.95f) slowGap = Mathf.Max(minGap + 0.5f, usable * 0.95f);
        Debug.Log($"[TrafficRing] '{name}': {n} cars spaced {step:0.0} m apart, gaps {minGap:0.0}/{slowGap:0.0}.", this);
    }

    float R => Mathf.Max(radius, 1f);

    public override float Length => 2f * Mathf.PI * R;

    public override void Sample(float s, out Vector3 position, out Vector3 forward)
    {
        float r = R;
        float sign = clockwise ? -1f : 1f;
        float a = sign * s / r;

        Vector3 centre = transform.position;
        centre.y = height;

        position = centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
        position.y = height;

        forward = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)) * sign;
        forward.Normalize();
    }

    public float NearestDistance(Vector3 world)
    {
        Vector3 d = Flat(world - transform.position);
        if (d.sqrMagnitude < 0.0001f) return 0f;

        float a = Mathf.Atan2(d.z, d.x);
        float sign = clockwise ? -1f : 1f;

        return Mathf.Repeat(sign * a * R, Length);
    }

    void OnDrawGizmos()
    {
        DrawPath(gizmoColor, 64);
    }
}
