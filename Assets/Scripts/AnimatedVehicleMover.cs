using UnityEngine;

/// <summary>
/// Mover for animated objects (horse-drawn carriages, walking characters).
/// Moves the parent transform along roads or waypoints and never touches the Animator,
/// the animation clips, or the authored tilt/roll of the model.
///
/// Fixed in this revision:
///  - A single waypoint froze the object: on arrival the index wrapped back onto the same
///    point and every later frame hit the threshold check and returned before moving.
///    One waypoint now shuttles between that point and the start position.
///  - transform.Rotate() accumulated on top of the authored rotation, so a Blender import at
///    X = -90 slowly lost the orientation you set. Heading is now a single yaw value applied
///    on top of a cached initial rotation: rotation = AngleAxis(yaw, up) * initialRotation.
///    The X = -90 and the Z you dialled in survive exactly.
///  - Forward direction was guessed from transform.forward, which is vertical on an X = -90
///    import, so it silently fell through to transform.up. You can now pick the local axis
///    explicitly and see it as a magenta arrow in the Scene view.
///  - Waypoint arrival no longer costs a frame of movement.
/// </summary>
public class AnimatedVehicleMover : MonoBehaviour
{
    public enum MoveMode
    {
        StraightRoad,   // Drives straight along the resolved forward axis
        WaypointsPath   // Follows waypoints (curves and U-turns)
    }

    /// <summary>Which local axis actually points out of the model's nose.</summary>
    public enum ForwardAxis
    {
        Auto,           // transform.forward, falling back to transform.up (legacy behaviour)
        LocalZ,
        LocalNegZ,
        LocalY,         // usual answer for a Blender FBX imported at X = -90
        LocalNegY,
        LocalX,
        LocalNegX
    }

    [Header("Movement Settings")]
    public MoveMode movementMode = MoveMode.StraightRoad;

    [Tooltip("Movement speed along the road in meters per second.")]
    public float moveSpeed = 3.0f;

    [Tooltip("Rotation turning speed when navigating curves.")]
    public float turnSpeed = 3.0f;

    [Tooltip("Check this if the object moves out of its back instead of its front.")]
    public bool reverseDirection = false;

    [Header("Orientation (does not touch your authored rotation)")]
    [Tooltip("Local axis that points out of the model's nose. Auto = transform.forward, then " +
             "transform.up. Watch the magenta arrow in the Scene view and flip until it points " +
             "down the street.")]
    public ForwardAxis forwardAxis = ForwardAxis.Auto;

    [Tooltip("Extra yaw added on top of Forward Axis, in degrees. The four horizontal axes sit " +
             "90 degrees apart, so when none of them lines up with the nose - normally because " +
             "the real model is rotated inside this parent - dial the remainder in here. Right " +
             "click the component header for Snap Forward To First Waypoint to set it for you.")]
    [Range(-180f, 180f)]
    public float forwardYawOffset = 0f;

    [Tooltip("Keeps the tilt and roll you set in the Inspector. Only heading (yaw around world " +
             "up) is driven by this script. Leave ON for Blender imports rotated X = -90.")]
    public bool preserveAuthoredRotation = true;

    [Tooltip("OFF means the object never rotates at all - it just slides along the heading you " +
             "authored. Use this when the rotation must stay frozen exactly as-is.")]
    public bool allowTurning = true;

    [Header("Waypoints (For Curves & U-Turns)")]
    [Tooltip("With exactly one waypoint the object shuttles between it and its start position.")]
    public Transform[] waypoints;

    [Tooltip("Distance threshold to switch to the next waypoint.")]
    public float waypointThreshold = 1.5f;

    [Header("Distance & Loop Settings")]
    public float maxDriveDistance = 150.0f;
    public bool loopMovement = true;

    [Header("Ground")]
    [Tooltip("Locks Y to the start height. Turn off and use Stick To Ground on sloped roads.")]
    public bool lockHeightToStart = true;

    [Tooltip("Raycasts down to follow the road surface instead of a fixed height.")]
    public bool stickToGround = false;

    [Tooltip("Layers treated as road when Stick To Ground is on.")]
    public LayerMask groundMask = ~0;

    [Tooltip("Vertical offset kept above the surface hit.")]
    public float groundOffset = 0f;

    [Header("Animator Control")]
    [Tooltip("Auto-detected if left empty. This script only sets Animator.speed - it never " +
             "changes clips, states or root motion.")]
    public Animator animator;

    [Tooltip("Animation playback speed multiplier.")]
    public float animationSpeedMultiplier = 1.0f;

    [Header("Debug")]
    public bool debugMovement = false;

    // ---------------------------------------------------------------- runtime state

    private int currentIndex = 0;
    private Vector3 startPosition;
    private Quaternion initialRotation;
    private float yaw = 0f;
    private bool returningToStart = false;

    void Start()
    {
        startPosition = transform.position;
        initialRotation = transform.rotation;
        yaw = 0f;

        if (animator == null)
        {
            animator = GetComponent<Animator>();
            if (animator == null) animator = GetComponentInChildren<Animator>();
        }
        if (animator != null) animator.speed = animationSpeedMultiplier;

        if (debugMovement)
        {
            Debug.Log($"[AnimatedVehicleMover] '{name}': forward = {GetGroundForward()}, " +
                      $"waypoints = {PathCount}, mode = {movementMode}.", this);
        }
    }

    void Update()
    {
        if (movementMode == MoveMode.WaypointsPath && PathCount > 0) UpdateWaypointsDrive();
        else UpdateStraightDrive();
    }

    // ---------------------------------------------------------------- orientation

    /// <summary>The local axis, in world space, projected flat onto the ground plane.</summary>
    public Vector3 GetGroundForward()
    {
        Vector3 candidate;

        switch (forwardAxis)
        {
            case ForwardAxis.LocalZ:    candidate = transform.forward; break;
            case ForwardAxis.LocalNegZ: candidate = -transform.forward; break;
            case ForwardAxis.LocalY:    candidate = transform.up; break;
            case ForwardAxis.LocalNegY: candidate = -transform.up; break;
            case ForwardAxis.LocalX:    candidate = transform.right; break;
            case ForwardAxis.LocalNegX: candidate = -transform.right; break;

            default: // Auto
                candidate = transform.forward;
                if (Vector3.ProjectOnPlane(candidate, Vector3.up).sqrMagnitude <= 0.01f)
                    candidate = transform.up;
                break;
        }

        Vector3 flat = Vector3.ProjectOnPlane(candidate, Vector3.up);
        if (flat.sqrMagnitude > 0.0001f) return flat.normalized;

        // The chosen axis is vertical: fall back to anything horizontal rather than freezing.
        flat = Vector3.ProjectOnPlane(transform.up, Vector3.up);
        if (flat.sqrMagnitude > 0.0001f) return flat.normalized;

        flat = Vector3.ProjectOnPlane(transform.right, Vector3.up);
        if (flat.sqrMagnitude > 0.0001f) return flat.normalized;

        return Vector3.forward;
    }

    private Vector3 DriveDirection()
    {
        Vector3 d = GetGroundForward();
        return reverseDirection ? -d : d;
    }

    /// <summary>
    /// Heading is a single yaw on top of the rotation you authored, so the X = -90 import tilt
    /// and your Z never drift.
    /// </summary>
    private void ApplyYaw(float deltaDegrees)
    {
        if (!allowTurning || Mathf.Approximately(deltaDegrees, 0f)) return;

        if (preserveAuthoredRotation)
        {
            yaw += deltaDegrees;
            transform.rotation = Quaternion.AngleAxis(yaw, Vector3.up) * initialRotation;
        }
        else
        {
            transform.Rotate(Vector3.up, deltaDegrees, Space.World);
        }
    }

    // ---------------------------------------------------------------- movement

    private void MoveAlong(Vector3 direction)
    {
        Vector3 next = transform.position + direction * moveSpeed * Time.deltaTime;

        if (stickToGround &&
            Physics.Raycast(next + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 20f,
                            groundMask, QueryTriggerInteraction.Ignore))
        {
            next.y = hit.point.y + groundOffset;
        }
        else if (lockHeightToStart)
        {
            next.y = startPosition.y;
        }

        transform.position = next;
    }

    private void UpdateStraightDrive()
    {
        Vector3 dir = DriveDirection();
        MoveAlong(dir);

        Vector3 flatStart = new Vector3(startPosition.x, 0f, startPosition.z);
        Vector3 flatNow = new Vector3(transform.position.x, 0f, transform.position.z);

        if (loopMovement && Vector3.Distance(flatStart, flatNow) >= maxDriveDistance)
        {
            transform.position = startPosition;
            if (preserveAuthoredRotation && allowTurning)
            {
                yaw = 0f;
                transform.rotation = initialRotation;
            }
        }
    }

    private void UpdateWaypointsDrive()
    {
        // Advance past every point already inside the threshold, in the same frame, so arriving
        // never costs a frame of movement and a single waypoint cannot lock the object in place.
        int guard = PathCount + 2;
        while (guard-- > 0)
        {
            Vector3 target = GetPathPoint(currentIndex);
            Vector3 toTarget = target - transform.position;
            toTarget.y = 0f;

            if (toTarget.magnitude > waypointThreshold)
            {
                SteerAndMove(toTarget.normalized);
                return;
            }

            if (!AdvanceIndex()) break;
        }

        // End of a non-looping path: hold position, keep the authored rotation.
        if (debugMovement) Debug.Log($"[AnimatedVehicleMover] '{name}': path finished.", this);
    }

    private void SteerAndMove(Vector3 desiredDir)
    {
        Vector3 forward = DriveDirection();

        float angleError = Vector3.SignedAngle(forward, desiredDir, Vector3.up);
        float maxStep = turnSpeed * 40f * Time.deltaTime;
        ApplyYaw(Mathf.Clamp(angleError, -maxStep, maxStep));

        // Re-read after the yaw so movement follows the new heading this same frame.
        MoveAlong(DriveDirection());

        if (debugMovement && Time.frameCount % 60 == 0)
        {
            Debug.Log($"[AnimatedVehicleMover] '{name}': heading error {angleError:F1}deg, " +
                      $"target index {currentIndex}/{PathCount}.", this);
        }
    }

    private bool AdvanceIndex()
    {
        currentIndex++;
        if (currentIndex < PathCount) return true;

        if (loopMovement)
        {
            currentIndex = 0;
            returningToStart = false;
            return true;
        }

        currentIndex = PathCount - 1;
        return false;
    }

    // ---------------------------------------------------------------- path

    /// <summary>
    /// Real waypoints, plus a virtual return-to-start leg when only one waypoint is assigned.
    /// Without this a single waypoint is a dead end the object parks on forever.
    /// </summary>
    private int PathCount
    {
        get
        {
            int n = CountValidWaypoints();
            if (n == 0) return 0;
            if (n == 1 && loopMovement) return 2;
            return n;
        }
    }

    private Vector3 GetPathPoint(int index)
    {
        int n = CountValidWaypoints();
        if (n == 0) return transform.position;

        if (n == 1 && loopMovement)
        {
            returningToStart = index == 1;
            return returningToStart ? startPosition : FirstValidWaypoint().position;
        }

        index = Mathf.Clamp(index, 0, n - 1);

        int seen = 0;
        for (int i = 0; i < waypoints.Length; i++)
        {
            if (waypoints[i] == null) continue;
            if (seen == index) return waypoints[i].position;
            seen++;
        }

        return transform.position;
    }

    private int CountValidWaypoints()
    {
        if (waypoints == null) return 0;
        int c = 0;
        for (int i = 0; i < waypoints.Length; i++) if (waypoints[i] != null) c++;
        return c;
    }

    private Transform FirstValidWaypoint()
    {
        if (waypoints == null) return null;
        for (int i = 0; i < waypoints.Length; i++) if (waypoints[i] != null) return waypoints[i];
        return null;
    }

    // ---------------------------------------------------------------- gizmos

    void OnDrawGizmos()
    {
        // Magenta arrow = the direction this object will actually drive. Flip Forward Axis
        // (or Reverse Direction) until it points down the street.
        Vector3 dir = Application.isPlaying ? DriveDirection() : PreviewDriveDirection();
        Vector3 origin = transform.position + Vector3.up * 0.5f;

        Gizmos.color = Color.magenta;
        Gizmos.DrawLine(origin, origin + dir * 3f);
        Gizmos.DrawSphere(origin + dir * 3f, 0.18f);

        if (waypoints == null || waypoints.Length == 0) return;

        Gizmos.color = Color.yellow;
        for (int i = 0; i < waypoints.Length; i++)
        {
            if (waypoints[i] == null) continue;

            Gizmos.DrawWireSphere(waypoints[i].position, waypointThreshold);
            Gizmos.DrawSphere(waypoints[i].position, 0.4f);

            if (i < waypoints.Length - 1 && waypoints[i + 1] != null)
                Gizmos.DrawLine(waypoints[i].position, waypoints[i + 1].position);
            else if (loopMovement && waypoints[0] != null && waypoints.Length > 1)
                Gizmos.DrawLine(waypoints[i].position, waypoints[0].position);
        }

        // The virtual return leg drawn for the single-waypoint case.
        if (CountValidWaypoints() == 1 && loopMovement)
        {
            Gizmos.color = new Color(1f, 0.6f, 0f);
            Gizmos.DrawLine(FirstValidWaypoint().position, transform.position);
        }
    }

    private Vector3 PreviewDriveDirection()
    {
        Vector3 d = GetGroundForward();
        return reverseDirection ? -d : d;
    }
}
