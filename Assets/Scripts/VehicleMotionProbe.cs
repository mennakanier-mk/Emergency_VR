using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Answers one question in Play mode: are the wheels stuck because they are not being rotated,
/// or because the car they belong to is not moving?
///
/// Reading the code cannot separate those two. VehicleController rolls the wheels by the distance
/// the car covered this frame, so a car that stands still has wheels that correctly stand still,
/// and the symptom is identical to a broken wheel list.
///
/// Add it with Tools > Emergency VR > Add Vehicle Motion Probe, press Play, and read the single
/// table it prints after sampleSeconds. Delete the object afterwards - it does nothing else.
///
/// Reading only, from outside: world position of each vehicle, and localRotation of each wheel.
/// No access to VehicleController's private state, so nothing here can mask the real behaviour.
/// </summary>
public class VehicleMotionProbe : MonoBehaviour
{
    [Tooltip("How long to watch before printing. Long enough that a slow car still shows up.")]
    public float sampleSeconds = 4f;

    [Tooltip("Vehicles to report individually. The rest are summarised as counts.")]
    public int detailRows = 8;

    class Sample
    {
        public Transform vehicle;
        public Vector3 startPos;
        public List<Transform> wheels = new List<Transform>();
        public List<Quaternion> startRot = new List<Quaternion>();
    }

    readonly List<Sample> _samples = new List<Sample>();
    bool _done;

    void Start()
    {
        foreach (var vc in FindObjectsByType<VehicleController>(FindObjectsSortMode.None))
        {
            var s = new Sample { vehicle = vc.transform, startPos = vc.transform.position };

            foreach (var t in vc.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (!n.Contains("wheel") && !n.Contains("tire") && !n.Contains("tyre")) continue;
                if (s.wheels.Contains(t)) continue;

                s.wheels.Add(t);
                s.startRot.Add(t.localRotation);
            }

            _samples.Add(s);
        }

        Debug.Log($"[VehicleMotionProbe] watching {_samples.Count} vehicles for {sampleSeconds}s...", this);
    }

    void Update()
    {
        if (_done || Time.timeSinceLevelLoad < sampleSeconds) return;
        _done = true;

        var sb = new StringBuilder();
        sb.AppendLine($"[VehicleMotionProbe] after {sampleSeconds}s");
        sb.AppendLine();
        sb.AppendLine("  vehicle                        moved(m)   wheels  max wheel turn(deg)");

        int movedAndRolling = 0, movedNotRolling = 0, notMoved = 0, noWheels = 0;
        int printed = 0;

        foreach (var s in _samples)
        {
            if (s.vehicle == null) continue;

            float moved = Vector3.Distance(s.vehicle.position, s.startPos);

            float maxTurn = 0f;
            for (int i = 0; i < s.wheels.Count; i++)
            {
                if (s.wheels[i] == null) continue;
                float a = Quaternion.Angle(s.startRot[i], s.wheels[i].localRotation);
                if (a > maxTurn) maxTurn = a;
            }

            bool didMove = moved > 0.05f;
            bool didRoll = maxTurn > 1f;

            if (s.wheels.Count == 0) noWheels++;
            else if (!didMove) notMoved++;
            else if (didRoll) movedAndRolling++;
            else movedNotRolling++;

            if (printed < detailRows)
            {
                sb.AppendLine($"  {s.vehicle.name,-30} {moved,8:0.00}   {s.wheels.Count,5}   {maxTurn,8:0.0}");
                printed++;
            }
        }

        sb.AppendLine();
        sb.AppendLine($"  moving AND wheels turning : {movedAndRolling}");
        sb.AppendLine($"  moving but wheels frozen  : {movedNotRolling}");
        sb.AppendLine($"  not moving at all         : {notMoved}");
        sb.AppendLine($"  no wheel objects found    : {noWheels}");
        sb.AppendLine();

        if (notMoved > 0 && movedNotRolling == 0)
            sb.AppendLine("  => The wheels are fine. The cars are not driving; fix the movement, " +
                          "not the wheels.");
        else if (movedNotRolling > 0)
            sb.AppendLine("  => The cars drive but the wheels are not being rotated. Check the " +
                          "Front/Rear Wheels lists and Wheel Radius on those vehicles.");
        else if (noWheels > 0)
            sb.AppendLine("  => Some vehicles have no wheel objects under them at all.");
        else
            sb.AppendLine("  => Everything is moving and rolling.");

        Debug.Log(sb.ToString(), this);
    }
}
