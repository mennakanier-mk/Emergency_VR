using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A closed loop that vehicles drive along, described by maths instead of by waypoints.
///
/// Every waypoint-based attempt here failed the same way: points generated from measured
/// geometry landed off the tarmac, cars overshot the tight ones and never came back, and fixing
/// one street meant regenerating two hundred objects. The points were never the goal - they were
/// a clumsy way of writing down a line.
///
/// So the line is written down directly. A road is a position, a heading, a length and a lane
/// offset - four numbers you can see and drag in the Scene view. A car's position is then
/// Sample(distance travelled), which is exactly on the road by construction. There is nothing to
/// overshoot, nothing to miss, and a street is corrected by nudging one object rather than
/// rebuilding anything.
///
/// Spacing works the same way. Every car on a path knows its own distance along it, so "how far
/// is the car in front" is a subtraction, not a physics cast. That removes the whole class of
/// bug where a car braked for a building, for the road under it, or for a car on a different
/// street - which is what left half the traffic standing still.
/// </summary>
public abstract class TrafficPath : MonoBehaviour
{
    public enum PathKind
    {
        Vehicles,      // roads and the roundabout
        Pedestrians    // pavements and crossings
    }

    [Tooltip("Who uses this path. Keeps cars off the pavement and people off the road when " +
             "routes are assigned automatically.")]
    public PathKind kind = PathKind.Vehicles;

    [Tooltip("Users keep at least this far apart along the path, in metres.")]
    public float minGap = 7f;

    [Tooltip("They start easing off this far behind the one in front.")]
    public float slowGap = 16f;

    readonly List<RoadDriver> _drivers = new List<RoadDriver>();

    /// <summary>Total distance once round the loop.</summary>
    public abstract float Length { get; }

    /// <summary>Position and heading at distance s along the loop. s may be any value.</summary>
    public abstract void Sample(float s, out Vector3 position, out Vector3 forward);

    public void Register(RoadDriver d) { if (!_drivers.Contains(d)) _drivers.Add(d); }
    public void Unregister(RoadDriver d) { _drivers.Remove(d); }

    public IReadOnlyList<RoadDriver> Drivers => _drivers;

    // Road closed at these distances: traffic stops before them as if a car stood there.
    readonly List<float> _blocks = new List<float>();
    public void AddBlock(float distance) { _blocks.Add(Mathf.Repeat(distance, Mathf.Max(0.01f, Length))); }
    public void ClearBlocks() { _blocks.Clear(); }

    /// <summary>
    /// Distance to the nearest vehicle ahead on this same path, or infinity if the path is
    /// clear. Wrapping with Repeat is what makes the last car see the first one round the loop.
    /// </summary>
    public float GapAhead(RoadDriver self)
    {
        float best = Mathf.Infinity;
        float len = Length;
        if (len <= 0.01f) return best;

        for (int i = 0; i < _drivers.Count; i++)
        {
            var other = _drivers[i];
            if (other == null || other == self) continue;

            float gap = Mathf.Repeat(other.Distance - self.Distance, len);
            if (gap <= 0.001f) continue;

            // minGap / slowGap are tuned for ordinary cars (about 4.4 m bumper to bumper of
            // half-lengths). A fire engine or bus is much longer, so take off whatever the two
            // vehicles exceed that by - otherwise the long one drives into the one in front.
            gap -= Mathf.Max(0f, self.HalfLength + other.HalfLength - 4.4f);
            if (gap < 0.001f) gap = 0.001f;

            if (gap < best) best = gap;
        }

        for (int i = 0; i < _blocks.Count; i++)
        {
            float gap = Mathf.Repeat(_blocks[i] - self.Distance, len) - self.HalfLength + 2.2f;
            if (gap > 0.001f && gap < best) best = gap;
        }

        return best;
    }

    /// <summary>Ground plane only - these streets are flat and the models import tilted.</summary>
    protected static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    protected Vector3 GroundForward()
    {
        Vector3 f = Flat(transform.forward);
        return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
    }

    protected static Vector3 RightOf(Vector3 forward)
    {
        return new Vector3(forward.z, 0f, -forward.x).normalized;
    }

    // ---------------------------------------------------------------- gizmos

    protected void DrawPath(Color color, int samples)
    {
        float len = Length;
        if (len <= 0.01f) return;

        Gizmos.color = color;

        Vector3 prev;
        Sample(0f, out prev, out _);

        for (int i = 1; i <= samples; i++)
        {
            Sample(len * i / samples, out Vector3 p, out _);
            Gizmos.DrawLine(prev, p);
            prev = p;
        }

        // An arrow at the start, so the direction of travel is obvious.
        Sample(0f, out Vector3 a, out Vector3 dir);
        Gizmos.DrawLine(a, a + dir * 6f);
        Gizmos.DrawLine(a + dir * 6f, a + dir * 3f + RightOf(dir) * 2f);
        Gizmos.DrawLine(a + dir * 6f, a + dir * 3f - RightOf(dir) * 2f);
    }
}
