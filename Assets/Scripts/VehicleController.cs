using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Universal Vehicle Controller for VR and 3D scenes.
/// Attach to the EMPTY PARENT of a vehicle (e.g. VEH_amb, VEH_white2).
///
/// Fixed in this revision:
///  - movementDelta was always Vector3.zero (lastPosition was written after the move and read
///    before the next move, so the subtraction cancelled out). Wheels never rolled.
///  - currentSteerAngle was computed but never applied to any wheel.
///  - Wheel spin now runs in LateUpdate, so it also works when an Animator, a NavMeshAgent or
///    another script moves the body.
///  - Roll axis is auto-detected per wheel from mesh bounds (the axle is the thinnest local
///    axis), and the spin direction is signed per wheel, so mirrored pivots no longer spin
///    backwards.
///  - Steering and rolling are composed from a cached initial localRotation instead of
///    accumulating Rotate() calls, which used to fight each other and drift.
/// </summary>
public class VehicleController : MonoBehaviour
{
    public enum NavigationType
    {
        AutoDrive,      // Automatic: drives forward along the road
        PathWaypoints,  // Recommended for U-turns: follows points to turn & change direction
        NavMesh,        // Uses Unity NavMesh on roads
        LinearDrive     // Drives straight down road lanes
    }

    public enum Axis
    {
        X_Axis,
        Y_Axis,
        Z_Axis
    }

    [Header("Navigation Settings")]
    [Tooltip("Choose PathWaypoints for U-turns and curves, or AutoDrive for straight roads.")]
    public NavigationType navigationMode = NavigationType.PathWaypoints;

    [Header("Speed & Direction Settings")]
    [Tooltip("Vehicle movement speed in meters per second.")]
    public float moveSpeed = 6.0f;

    /// <summary>
    /// 0..1, written every frame by TrafficSensor. This is how the car slows for the one in
    /// front instead of driving through it. Nothing else touches it, and with no TrafficSensor
    /// attached it stays at 1 and the car behaves exactly as before.
    /// </summary>
    [System.NonSerialized] public float speedScale = 1f;

    /// <summary>Speed actually used this frame.</summary>
    public float CurrentSpeed { get { return moveSpeed * Mathf.Clamp01(speedScale); } }

    [Tooltip("Smooth turning speed when navigating curves and U-turns.")]
    public float rotationSpeed = 3.0f;

    [Tooltip("Maximum steering angle (degrees) for the front wheels when turning.")]
    public float maxSteerAngle = 30.0f;

    [Tooltip("Check this if you want the car to drive in reverse direction.")]
    public bool reverseDirection = false;

    [Header("Waypoints (For U-Turns & Curved Roads)")]
    public Transform[] waypoints;

    [Tooltip("Distance threshold to trigger turning to the next waypoint.")]
    public float waypointThreshold = 2.0f;

    [Header("Drive Distance & Loop Settings")]
    public float maxDriveDistance = 150.0f;
    public bool loopDestination = true;

    [Header("Optional Target Destinations")]
    public Transform destinationTarget;

    [Header("Wheel Settings")]
    [Tooltip("Wheel radius in meters. Standard car tire is ~0.35 (35 cm).")]
    public float wheelRadius = 0.35f;

    [Tooltip("Detect each wheel's axle from its mesh bounds. Leave on unless a wheel spins wrong.")]
    public bool autoDetectRollAxis = true;

    [Tooltip("Fallback rolling axis used when auto-detection is off or fails.")]
    public Axis wheelRollAxis = Axis.X_Axis;

    [Tooltip("Auto-detect wheels from child GameObjects (falls back to siblings).")]
    public bool autoDetectWheels = true;

    [Tooltip("How fast the front wheels swing to the target steer angle, degrees per second.")]
    public float steerLerpSpeed = 180f;

    [Header("Manual Wheel Assignments (Optional)")]
    public List<Transform> frontWheels = new List<Transform>();
    public List<Transform> rearWheels = new List<Transform>();

    [Header("Debug")]
    [Tooltip("Logs wheel count and per-frame roll so you can see whether the body is moving at all.")]
    public bool debugWheels = false;

    // ---------------------------------------------------------------- runtime state

    class WheelRef
    {
        public Transform t;
        public Quaternion initialLocalRot;
        public Vector3 rollAxisLocal;   // axle, in the wheel's own local space
        public float rollSign;          // +1 / -1 so left and right spin the same way
        public bool steerable;
        public float rollAngle;
    }

    readonly List<WheelRef> _wheels = new List<WheelRef>();

    private NavMeshAgent navAgent;
    private int currentWaypointIndex = 0;
    private float currentSteerAngle = 0f;
    private float appliedSteerAngle = 0f;
    private Vector3 startPosition;
    private Vector3 _posBeforeMove;
    private float _frameDistance;

    // ---------------------------------------------------------------- lifecycle

    void Start()
    {
        startPosition = transform.position;
        _posBeforeMove = transform.position;

        if (autoDetectWheels && CountValid(frontWheels) + CountValid(rearWheels) == 0)
            AutoDetectWheels();

        AutoCorrectWheelRadius();
        BuildWheelRefs();

        // An empty wheel list is the NORMAL state now: WheelSpin sits on each wheel and rolls it
        // from the wheel's own movement, so this component drives the car and nothing else. The
        // warning only means something if this component was still asked to find wheels itself.
        if (_wheels.Count == 0 && autoDetectWheels)
        {
            Debug.LogWarning($"[VehicleController] '{name}': no wheels found. Expected children whose name " +
                             "contains wheel / tire / tyre (e.g. VEHWHEEL_*). Assign them manually in " +
                             "Front Wheels / Rear Wheels, or move the wheel objects under this parent.", this);
        }
        else if (_wheels.Count > 0 && debugWheels)
        {
            Debug.Log($"[VehicleController] '{name}': {_wheels.Count} wheels " +
                      $"({CountValid(frontWheels)} front / {CountValid(rearWheels)} rear), radius {wheelRadius:0.00}m.", this);
        }

        navAgent = GetComponent<NavMeshAgent>();
        if (navAgent != null)
        {
            navAgent.speed = moveSpeed;
            navAgent.angularSpeed = rotationSpeed * 60f;
            navAgent.acceleration = 6f;
            navAgent.stoppingDistance = 1.0f;
        }
    }

    void Update()
    {
        // Snapshot BEFORE moving. This is the bug fix: the delta has to span this frame's move,
        // not the gap between two already-identical positions.
        _posBeforeMove = transform.position;

        switch (navigationMode)
        {
            case NavigationType.PathWaypoints: UpdateWaypoints(); break;
            case NavigationType.AutoDrive:     UpdateAutoDrive(); break;
            case NavigationType.NavMesh:       UpdateNavMesh();   break;
            case NavigationType.LinearDrive:   UpdateLinear();    break;
        }
    }

    void LateUpdate()
    {
        // LateUpdate so an Animator, NavMeshAgent or external mover is already applied.
        Vector3 movementDelta = transform.position - _posBeforeMove;

        Vector3 groundForward = GetGroundForward();
        if (reverseDirection) groundForward = -groundForward;

        _frameDistance = Vector3.Dot(movementDelta, groundForward);

        appliedSteerAngle = Mathf.MoveTowards(appliedSteerAngle, currentSteerAngle,
                                              steerLerpSpeed * Time.deltaTime);

        UpdateWheels(_frameDistance);

        _posBeforeMove = transform.position;
    }

    // ---------------------------------------------------------------- wheels

    private void UpdateWheels(float forwardDistance)
    {
        if (_wheels.Count == 0) return;

        float activeRadius = Mathf.Max(wheelRadius, 0.05f);
        float rollDegreesDelta = (forwardDistance / (2f * Mathf.PI * activeRadius)) * 360f;

        for (int i = 0; i < _wheels.Count; i++)
        {
            var w = _wheels[i];
            if (w.t == null) continue;

            w.rollAngle += rollDegreesDelta * w.rollSign;
            if (w.rollAngle > 360f || w.rollAngle < -360f) w.rollAngle %= 360f;

            Quaternion roll = Quaternion.AngleAxis(w.rollAngle, w.rollAxisLocal);

            if (w.steerable && Mathf.Abs(appliedSteerAngle) > 0.01f)
            {
                // World up expressed in the wheel's own (initial) local frame.
                Vector3 upLocal = w.t.parent != null
                    ? w.t.parent.InverseTransformDirection(Vector3.up)
                    : Vector3.up;
                upLocal = Quaternion.Inverse(w.initialLocalRot) * upLocal;

                Quaternion steer = Quaternion.AngleAxis(appliedSteerAngle, upLocal.normalized);
                w.t.localRotation = w.initialLocalRot * steer * roll;
            }
            else
            {
                w.t.localRotation = w.initialLocalRot * roll;
            }
        }

        if (debugWheels && Time.frameCount % 60 == 0)
        {
            Debug.Log($"[VehicleController] '{name}': moved {forwardDistance:F4} m this frame, " +
                      $"roll {rollDegreesDelta:F2}deg, steer {appliedSteerAngle:F1}deg.", this);
        }
    }

    private void BuildWheelRefs()
    {
        _wheels.Clear();
        AddWheelRefs(frontWheels, steerable: true);
        AddWheelRefs(rearWheels, steerable: false);
    }

    private void AddWheelRefs(List<Transform> list, bool steerable)
    {
        if (list == null) return;

        Vector3 bodyRight = Vector3.Cross(Vector3.up, GetGroundForward()).normalized;
        if (bodyRight.sqrMagnitude < 0.001f) bodyRight = Vector3.right;

        foreach (var t in list)
        {
            if (t == null) continue;

            var w = new WheelRef
            {
                t = t,
                initialLocalRot = t.localRotation,
                steerable = steerable,
                rollAngle = 0f,
            };

            w.rollAxisLocal = autoDetectRollAxis ? DetectAxleAxis(t) : FallbackAxis();

            Vector3 axleWorld = t.TransformDirection(w.rollAxisLocal);
            w.rollSign = Vector3.Dot(axleWorld.normalized, bodyRight) >= 0f ? 1f : -1f;

            _wheels.Add(w);
        }
    }

    private Vector3 FallbackAxis()
    {
        switch (wheelRollAxis)
        {
            case Axis.Y_Axis: return Vector3.up;
            case Axis.Z_Axis: return Vector3.forward;
            default:          return Vector3.right;
        }
    }

    /// <summary>
    /// A wheel is a disc: its thinnest local dimension is the axle. Measured from the mesh so it
    /// survives Blender's -90 X import rotation and any pivot the artist left on the object.
    /// </summary>
    private Vector3 DetectAxleAxis(Transform wheel)
    {
        MeshFilter mf = wheel.GetComponent<MeshFilter>();
        if (mf == null) mf = wheel.GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return FallbackAxis();

        Vector3 size = mf.sharedMesh.bounds.size;
        Vector3 axisInMeshLocal;
        if (size.x <= size.y && size.x <= size.z)      axisInMeshLocal = Vector3.right;
        else if (size.y <= size.x && size.y <= size.z) axisInMeshLocal = Vector3.up;
        else                                           axisInMeshLocal = Vector3.forward;

        // Mesh-local -> world -> wheel-local, then snap to the nearest cardinal axis.
        Vector3 world = mf.transform.TransformDirection(axisInMeshLocal);
        Vector3 local = wheel.InverseTransformDirection(world).normalized;

        float ax = Mathf.Abs(local.x), ay = Mathf.Abs(local.y), az = Mathf.Abs(local.z);
        if (ax >= ay && ax >= az) return Vector3.right;
        if (ay >= ax && ay >= az) return Vector3.up;
        return Vector3.forward;
    }

    private void AutoCorrectWheelRadius()
    {
        if (wheelRadius >= 0.05f && wheelRadius <= 1.5f) return;

        wheelRadius = 0.35f;
        Transform probe = FirstValid(frontWheels) ?? FirstValid(rearWheels);
        if (probe == null) return;

        var r = probe.GetComponentInChildren<Renderer>();
        if (r == null) return;

        // Radius = half the largest cross-section, not bounds.extents.y (wrong for rotated pivots).
        Vector3 e = r.bounds.extents;
        float estimated = Mathf.Max(e.x, Mathf.Max(e.y, e.z));
        if (estimated > 0.1f && estimated < 1.0f) wheelRadius = estimated;
    }

    [ContextMenu("Re-detect Wheels")]
    public void RedetectWheels()
    {
        AutoDetectWheels();
        AutoCorrectWheelRadius();
        BuildWheelRefs();
        Debug.Log($"[VehicleController] '{name}': detected {CountValid(frontWheels)} front / " +
                  $"{CountValid(rearWheels)} rear wheels.", this);
    }

    private void AutoDetectWheels()
    {
        frontWheels.Clear();
        rearWheels.Clear();

        var candidates = new List<Transform>();
        CollectWheels(transform, candidates);

        // Some vehicles were exported with the wheels as siblings of the body rather than
        // children of the VEH_ parent. Fall back to the parent's other children.
        if (candidates.Count == 0 && transform.parent != null)
        {
            foreach (Transform sibling in transform.parent)
            {
                if (sibling == transform) continue;
                CollectWheels(sibling, candidates);
                if (IsWheelName(sibling.name) && !candidates.Contains(sibling))
                    candidates.Add(sibling);
            }
        }

        if (candidates.Count == 0) return;

        // Split front / rear along the vehicle's own forward axis, in world space, so the
        // Blender -90 X import rotation does not scramble the order.
        Vector3 forward = GetGroundForward();
        Vector3 center = Vector3.zero;
        foreach (var c in candidates) center += c.position;
        center /= candidates.Count;

        candidates.Sort((a, b) =>
            Vector3.Dot(b.position - center, forward).CompareTo(Vector3.Dot(a.position - center, forward)));

        int half = candidates.Count / 2;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (i < half) frontWheels.Add(candidates[i]);
            else          rearWheels.Add(candidates[i]);
        }
    }

    private void CollectWheels(Transform root, List<Transform> into)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            Transform t = r.transform;

            // Walk up to the highest ancestor that still reads as a wheel, so we rotate the
            // wheel root and not the mesh child (Object_60 etc.).
            Transform pick = null;
            Transform cursor = t;
            while (cursor != null && cursor != root.parent)
            {
                if (IsWheelName(cursor.name)) pick = cursor;
                cursor = cursor.parent;
            }

            if (pick != null && !into.Contains(pick)) into.Add(pick);
        }
    }

    private static bool IsWheelName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        string s = n.ToLowerInvariant();
        return s.Contains("wheel") || s.Contains("tire") || s.Contains("tyre");
    }

    private static int CountValid(List<Transform> list)
    {
        if (list == null) return 0;
        int c = 0;
        foreach (var t in list) if (t != null) c++;
        return c;
    }

    private static Transform FirstValid(List<Transform> list)
    {
        if (list == null) return null;
        foreach (var t in list) if (t != null) return t;
        return null;
    }

    // ---------------------------------------------------------------- navigation

    public Vector3 GetGroundForward()
    {
        Vector3 proj = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (proj.sqrMagnitude > 0.01f) return proj.normalized;

        // Blender FBX import (-90 X): transform.up points forward on the ground.
        proj = Vector3.ProjectOnPlane(transform.up, Vector3.up);
        if (proj.sqrMagnitude > 0.01f) return proj.normalized;

        return Vector3.forward;
    }

    private void UpdateWaypoints()
    {
        if (waypoints == null || waypoints.Length == 0)
        {
            UpdateLinear();
            return;
        }

        Transform currentTarget = waypoints[currentWaypointIndex];
        if (currentTarget == null)
        {
            currentWaypointIndex = (currentWaypointIndex + 1) % waypoints.Length;
            return;
        }

        Vector3 targetDirection = currentTarget.position - transform.position;
        targetDirection.y = 0;

        if (targetDirection.magnitude <= waypointThreshold)
        {
            currentWaypointIndex++;
            if (currentWaypointIndex >= waypoints.Length)
                currentWaypointIndex = loopDestination ? 0 : waypoints.Length - 1;
            return;
        }

        Vector3 groundForward = GetGroundForward();
        if (reverseDirection) groundForward = -groundForward;

        float angleError = Vector3.SignedAngle(groundForward, targetDirection.normalized, Vector3.up);
        currentSteerAngle = Mathf.Clamp(angleError, -maxSteerAngle, maxSteerAngle);

        float maxStep = rotationSpeed * 40f * Time.deltaTime;
        transform.Rotate(Vector3.up, Mathf.Clamp(angleError, -maxStep, maxStep), Space.World);

        Vector3 nextPos = transform.position + groundForward * CurrentSpeed * Time.deltaTime;
        nextPos.y = startPosition.y;
        transform.position = nextPos;
    }

    private void UpdateAutoDrive() { DriveWithAgentOrLinear(); }
    private void UpdateNavMesh()   { DriveWithAgentOrLinear(); }

    private void DriveWithAgentOrLinear()
    {
        if (navAgent == null || !navAgent.enabled || !navAgent.isOnNavMesh)
        {
            UpdateLinear();
            return;
        }

        Vector3 targetPos = destinationTarget != null
            ? destinationTarget.position
            : startPosition + GetGroundForward() * maxDriveDistance;

        if ((navAgent.destination - targetPos).sqrMagnitude > 0.01f)
            navAgent.SetDestination(targetPos);

        if (loopDestination && !navAgent.pathPending && navAgent.remainingDistance <= navAgent.stoppingDistance)
        {
            navAgent.Warp(startPosition);
            navAgent.SetDestination(targetPos);
        }

        // Steer the front wheels from the agent's actual turn rate.
        navAgent.speed = CurrentSpeed;

        Vector3 desired = navAgent.desiredVelocity;
        if (desired.sqrMagnitude > 0.01f)
        {
            float err = Vector3.SignedAngle(GetGroundForward(), desired.normalized, Vector3.up);
            currentSteerAngle = Mathf.Clamp(err, -maxSteerAngle, maxSteerAngle);
        }
        else
        {
            currentSteerAngle = 0f;
        }
    }

    private void UpdateLinear()
    {
        currentSteerAngle = 0f;

        Vector3 groundForward = GetGroundForward();
        if (reverseDirection) groundForward = -groundForward;

        Vector3 nextPos = transform.position + groundForward * CurrentSpeed * Time.deltaTime;
        nextPos.y = startPosition.y;
        transform.position = nextPos;

        Vector3 flatStart = new Vector3(startPosition.x, 0, startPosition.z);
        Vector3 flatCurrent = new Vector3(transform.position.x, 0, transform.position.z);
        if (loopDestination && Vector3.Distance(flatStart, flatCurrent) >= maxDriveDistance)
        {
            transform.position = startPosition;
            _posBeforeMove = startPosition; // do not let the teleport spin the wheels
        }
    }

    // ---------------------------------------------------------------- gizmos

    void OnDrawGizmos()
    {
        if (waypoints == null || waypoints.Length == 0) return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < waypoints.Length; i++)
        {
            if (waypoints[i] == null) continue;

            Gizmos.DrawWireSphere(waypoints[i].position, waypointThreshold);
            Gizmos.DrawSphere(waypoints[i].position, 0.4f);

            if (i < waypoints.Length - 1 && waypoints[i + 1] != null)
                Gizmos.DrawLine(waypoints[i].position, waypoints[i + 1].position);
            else if (loopDestination && waypoints[0] != null)
                Gizmos.DrawLine(waypoints[i].position, waypoints[0].position);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        DrawWheelGizmos(frontWheels);
        Gizmos.color = Color.yellow;
        DrawWheelGizmos(rearWheels);
    }

    private void DrawWheelGizmos(List<Transform> list)
    {
        if (list == null) return;
        foreach (var t in list)
        {
            if (t == null) continue;
            Gizmos.DrawWireSphere(t.position, Mathf.Max(wheelRadius, 0.05f));
        }
    }
}
