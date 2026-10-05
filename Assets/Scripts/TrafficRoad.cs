using UnityEngine;

/// <summary>
/// One straight street, driven as a closed loop: out along the right-hand lane, a half-circle at
/// the far end, back along the other lane, a half-circle at the near end.
///
/// Put the object in the middle of the street, point its blue Z arrow down the street with the
/// normal Rotate tool, and set Length and Lane Offset. The green gizmo is exactly where the cars
/// will drive - if the gizmo is on the tarmac, the cars are on the tarmac. That is the whole
/// reason for doing it this way instead of generating points: what you see IS the path, so
/// aligning a street is a drag rather than a rebuild.
///
/// The end turns are half-circles of radius Lane Offset, which is the real geometry of a U-turn
/// between two lanes - it lands the car in the opposite lane facing back the way it came,
/// without ever leaving the width of the road.
/// </summary>
[ExecuteAlways]
public class TrafficRoad : TrafficPath
{
    [Tooltip("Length of the straight section, centred on this object.")]
    public float length = 120f;

    [Tooltip("Half the gap between the two lanes. Also the radius of the turn at each end, so " +
             "the U-turn stays inside the width of the road.")]
    public float laneOffset = 4f;

    [Tooltip("Height the vehicles are placed at. Usually the road surface.")]
    public float height = 0f;

    public Color gizmoColor = new Color(0.25f, 1f, 0.4f);

    float TurnRadius => Mathf.Max(laneOffset, 1f);
    float ArcLength => Mathf.PI * TurnRadius;

    public override float Length => 2f * Mathf.Max(length, 1f) + 2f * ArcLength;

    public override void Sample(float s, out Vector3 position, out Vector3 forward)
    {
        float straight = Mathf.Max(length, 1f);
        float r = TurnRadius;
        float arc = ArcLength;
        float total = 2f * straight + 2f * arc;

        s = Mathf.Repeat(s, total);

        Vector3 centre = transform.position;
        centre.y = height;

        Vector3 f = GroundForward();
        Vector3 right = RightOf(f);
        float half = straight * 0.5f;

        if (s < straight)
        {
            // Outbound, keeping right.
            float t = -half + s;
            position = centre + f * t + right * r;
            forward = f;
        }
        else if (s < straight + arc)
        {
            // Half-circle at the far end, swung around the centre line.
            float a = (s - straight) / r;                       // 0 .. pi
            Vector3 pivot = centre + f * half;
            position = pivot + right * (Mathf.Cos(a) * r) + f * (Mathf.Sin(a) * r);
            forward = (-right * Mathf.Sin(a) + f * Mathf.Cos(a)).normalized;
        }
        else if (s < 2f * straight + arc)
        {
            // Inbound, now on the other side, which is the right of the reversed heading.
            float t = half - (s - straight - arc);
            position = centre + f * t - right * r;
            forward = -f;
        }
        else
        {
            // Half-circle at the near end.
            float a = (s - 2f * straight - arc) / r;
            Vector3 pivot = centre - f * half;
            position = pivot - right * (Mathf.Cos(a) * r) - f * (Mathf.Sin(a) * r);
            forward = (right * Mathf.Sin(a) - f * Mathf.Cos(a)).normalized;
        }

        position.y = height;
    }

    /// <summary>Distance along the loop nearest to a world point. Used to drop a car onto the
    /// street where it already stands, instead of teleporting it to the start.</summary>
    public float NearestDistance(Vector3 world)
    {
        float best = 0f, bestSqr = float.MaxValue;
        int steps = 128;
        float len = Length;

        for (int i = 0; i < steps; i++)
        {
            float s = len * i / steps;
            Sample(s, out Vector3 p, out _);
            float d = Flat(p - world).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = s; }
        }

        return best;
    }

    void OnDrawGizmos()
    {
        DrawPath(gizmoColor, 96);

        // The centre line, so it is obvious whether the object sits in the middle of the street.
        Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.4f);
        Vector3 c = transform.position; c.y = height;
        Vector3 f = GroundForward();
        Gizmos.DrawLine(c - f * length * 0.5f, c + f * length * 0.5f);
    }
}
