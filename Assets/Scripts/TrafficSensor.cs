using UnityEngine;

/// <summary>
/// Stops a car driving into the one in front.
///
/// Goes on the vehicle root, next to VehicleController, and writes VehicleController.speedScale
/// every frame. It never moves anything itself - the controller still does all the driving, this
/// only tells it how fast it is allowed to go right now.
///
/// Why a cast and not physics collisions: the vehicles are moved by writing transform.position,
/// so PhysX never resolves a contact between them - two cars simply overlap and carry on. A
/// collider on a car that is moved by transform is a REPORTING device, not a barrier. So the car
/// has to look ahead and slow down on its own, which is also what real traffic does and what
/// makes a queue form instead of a pile-up.
///
/// A box cast, not a ray: a ray down the centre line misses a car that is half a lane over, and
/// misses it in exactly the situation where you are about to clip its rear corner.
///
/// Two distances, not one:
///   below stopDistance   - speedScale 0, the car holds position.
///   between stop and slow- speedScale ramps, so it eases in rather than slamming to a halt.
///   beyond slowDistance  - full speed.
/// </summary>
[DefaultExecutionOrder(-10)]        // before VehicleController reads speedScale
[DisallowMultipleComponent]
public class TrafficSensor : MonoBehaviour
{
    [Header("Looking ahead")]
    [Tooltip("Come to a stop when something is this close in front.")]
    public float stopDistance = 5f;

    [Tooltip("Start easing off at this distance. Must be larger than stopDistance.")]
    public float slowDistance = 14f;

    [Tooltip("Half-width of the corridor the car watches. Roughly half a lane.")]
    public float halfWidth = 1.2f;

    [Tooltip("Half-height of that corridor.")]
    public float halfHeight = 1.0f;

    [Tooltip("How far forward the cast starts, so the car does not detect its own nose.")]
    public float forwardOffset = 2.5f;

    [Tooltip("Height above the pivot to cast from.")]
    public float castHeight = 1.0f;

    [Header("What to watch")]
    /// <summary>
    /// Vehicles ONLY.
    ///
    /// This started as Everything and that is what left half the traffic standing still: the
    /// first thing a box cast finds down a street is usually the street, or the building on the
    /// bend, and the car dutifully brakes for the road it is driving on. Once one car stops, the
    /// one behind it stops too, and the jam spreads backwards for ever.
    ///
    /// Build Traffic Network puts every vehicle on the "Vehicle" layer and sets this to it.
    /// </summary>
    [Tooltip("Set by Build Traffic Network to the Vehicle layer. Everything here means the car " +
             "brakes for the road under it.")]
    public LayerMask obstacleMask = 0;

    [Header("Getting unstuck")]
    [Tooltip("If the car has been fully stopped this long, let it creep. Without this, two cars " +
             "that meet nose to nose at a junction stay there for the rest of the session.")]
    public float deadlockSeconds = 2.5f;

    [Tooltip("Speed fraction to creep at once deadlocked.")]
    [Range(0f, 1f)] public float creepScale = 0.4f;

    [Header("Debug")]
    public bool drawGizmo = false;

    // ---------------------------------------------------------------- state

    VehicleController _controller;
    Transform _tr;
    float _stoppedFor;
    float _lastDistance = Mathf.Infinity;

    void Awake()
    {
        _tr = transform;
        _controller = GetComponent<VehicleController>();

        if (_controller == null)
        {
            Debug.LogWarning($"[TrafficSensor] '{name}': no VehicleController on this object, so " +
                             "there is nothing to slow down. Removing myself from the update loop.",
                             this);
            enabled = false;
        }

        if (slowDistance <= stopDistance) slowDistance = stopDistance + 1f;
    }

    void Update()
    {
        Vector3 forward = GroundForward();
        Vector3 origin = _tr.position + Vector3.up * castHeight + forward * forwardOffset;

        float distance = Mathf.Infinity;

        // No mask means nothing has set us up yet. Braking for everything is far worse than
        // braking for nothing, so do nothing until Build Traffic Network assigns the layer.
        if (obstacleMask.value == 0)
        {
            _controller.speedScale = 1f;
            return;
        }

        var hits = Physics.BoxCastAll(
            origin,
            new Vector3(halfWidth, halfHeight, 0.05f),
            forward,
            Quaternion.LookRotation(forward, Vector3.up),
            slowDistance,
            obstacleMask,
            QueryTriggerInteraction.Ignore);

        foreach (var h in hits)
        {
            // Our own body, our own wheels, and anything else parented under this vehicle.
            if (h.transform.IsChildOf(_tr)) continue;

            // BoxCastAll reports distance 0 for colliders already overlapping the start box,
            // which would read as "something is touching my nose" for the road under us.
            if (h.distance <= 0.0001f) continue;

            if (h.distance < distance) distance = h.distance;
        }

        _lastDistance = distance;

        float scale;
        if (distance >= slowDistance) scale = 1f;
        else if (distance <= stopDistance) scale = 0f;
        else scale = Mathf.Clamp01((distance - stopDistance) / (slowDistance - stopDistance));

        // Deadlock escape. Two cars nose to nose both read "blocked" forever otherwise.
        if (scale <= 0.001f)
        {
            _stoppedFor += Time.deltaTime;
            if (_stoppedFor > deadlockSeconds) scale = creepScale;
        }
        else
        {
            _stoppedFor = 0f;
        }

        _controller.speedScale = scale;
    }

    Vector3 GroundForward()
    {
        // Same rule VehicleController uses: these models import with a -90 X rotation, so
        // transform.forward points at the sky and transform.up is the real heading.
        Vector3 f = Vector3.ProjectOnPlane(_tr.forward, Vector3.up);
        if (f.sqrMagnitude > 0.01f) f = f.normalized;
        else
        {
            f = Vector3.ProjectOnPlane(_tr.up, Vector3.up);
            f = f.sqrMagnitude > 0.01f ? f.normalized : Vector3.forward;
        }

        if (_controller != null && _controller.reverseDirection) f = -f;
        return f;
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmo) return;

        Vector3 forward = Application.isPlaying ? GroundForward()
                                                : Vector3.ProjectOnPlane(transform.up, Vector3.up).normalized;
        Vector3 origin = transform.position + Vector3.up * castHeight + forward * forwardOffset;

        Gizmos.color = _lastDistance <= stopDistance ? Color.red
                     : _lastDistance <= slowDistance ? Color.yellow : Color.green;

        Gizmos.DrawLine(origin, origin + forward * slowDistance);
        Gizmos.DrawWireCube(origin + forward * stopDistance,
                            new Vector3(halfWidth * 2f, halfHeight * 2f, 0.2f));
    }
}
