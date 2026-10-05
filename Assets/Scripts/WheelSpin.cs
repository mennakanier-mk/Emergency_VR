using UnityEngine;

/// <summary>
/// Spins one wheel. Put it on the wheel object itself and it needs nothing else.
///
/// Every previous attempt routed through the car: VehicleController held lists of wheels, worked
/// out which were front and which were rear, guessed a radius for the whole vehicle, and rolled
/// them by the distance the CAR had travelled. Every one of those steps was a place to be wrong,
/// and with 36 vehicles, three naming schemes and a flattened import hierarchy, some of them
/// were wrong on every vehicle.
///
/// This measures the only thing that actually matters, and measures it locally: how far THIS
/// wheel moved through the world since the last frame. That works no matter what moved it -
/// VehicleController, a NavMeshAgent, an Animator, a parent transform, a cart being towed, or
/// you dragging it in the Scene view during Play. There are no lists to fill in and nothing to
/// detect.
///
/// The maths: a wheel of radius r rolling on the ground with velocity v turns at |v| / r radians
/// per second, about the axis perpendicular to both the ground normal and v. That axis is the
/// wheel's own axle, so the only thing to work out per wheel is which of its local axes the
/// axle is - and a wheel is a disc, so the axle is its thinnest direction.
///
/// If a wheel does not turn, it is because it is not moving. Check the car, not this.
/// </summary>
[DefaultExecutionOrder(200)]   // after anything that moves the vehicle
[DisallowMultipleComponent]
public class WheelSpin : MonoBehaviour
{
    public enum AxleAxis { Auto, LocalX, LocalY, LocalZ }

    [Header("Wheel")]
    [Tooltip("Which local axis the wheel turns around. Auto reads it from the mesh: the axle is " +
             "the direction the wheel is thinnest in.")]
    public AxleAxis axle = AxleAxis.Auto;

    [Tooltip("Wheel radius in metres. 0 measures it from the mesh, which is almost always right " +
             "and is immune to an import scale you forgot about.")]
    public float radius = 0f;

    [Tooltip("Turn up if the wheels look like they are sliding, down if they look like they are " +
             "spinning out. 1 is physically correct.")]
    [Range(0.1f, 5f)] public float speedMultiplier = 1f;

    [Tooltip("Reverses the spin. Only needed if a wheel turns backwards.")]
    public bool invert = false;

    [Header("Debug")]
    [Tooltip("Logs this wheel's distance and turn rate once a second.")]
    public bool debug = false;

    // ---------------------------------------------------------------- state

    Vector3 _axleLocal = Vector3.right;
    Quaternion _initialLocalRot;
    Vector3 _lastPos;
    float _angle;
    float _radius = 0.35f;
    bool _ready;

    void Start()
    {
        _initialLocalRot = transform.localRotation;
        _lastPos = transform.position;

        _axleLocal = axle == AxleAxis.Auto ? DetectAxle() : FixedAxis();
        _radius = radius > 0.001f ? radius : MeasureRadius();

        _ready = true;

        if (debug)
            Debug.Log($"[WheelSpin] '{name}': axle {_axleLocal}, radius {_radius:0.000} m.", this);
    }

    void LateUpdate()
    {
        if (!_ready) return;

        Vector3 now = transform.position;
        Vector3 move = now - _lastPos;
        _lastPos = now;

        // Vertical travel is suspension and slopes, not rolling.
        move.y = 0f;

        float distance = move.magnitude;
        if (distance < 0.000001f) return;

        // A loop that teleports the car back to its start would otherwise spin the wheel by the
        // whole length of the street in one frame.
        if (distance > 5f) return;

        Vector3 axleWorld = transform.TransformDirection(_axleLocal).normalized;

        // Rolling axis for this direction of travel: perpendicular to both the ground normal and
        // the direction of motion. Compared against the axle, it tells us which way round to spin.
        Vector3 rollAxis = Vector3.Cross(Vector3.up, move / distance);

        float sign = Vector3.Dot(rollAxis, axleWorld) >= 0f ? 1f : -1f;
        if (invert) sign = -sign;

        _angle += (distance / Mathf.Max(0.02f, _radius)) * Mathf.Rad2Deg * sign * speedMultiplier;
        if (_angle > 360f || _angle < -360f) _angle %= 360f;

        // Rebuilt from the authored rotation every frame rather than accumulated onto the
        // transform, so the pose the artist set is never drifted away from.
        transform.localRotation = _initialLocalRot * Quaternion.AngleAxis(_angle, _axleLocal);

        if (debug && Time.frameCount % 60 == 0)
        {
            Debug.Log($"[WheelSpin] '{name}': {distance / Time.deltaTime:0.0} m/s, " +
                      $"angle {_angle:0}deg", this);
        }
    }

    // ---------------------------------------------------------------- setup

    Vector3 FixedAxis()
    {
        switch (axle)
        {
            case AxleAxis.LocalY: return Vector3.up;
            case AxleAxis.LocalZ: return Vector3.forward;
            default:              return Vector3.right;
        }
    }

    /// <summary>
    /// A wheel is a disc, so its thinnest local dimension is the axle. Read from the mesh rather
    /// than assumed, because a Blender export arrives with a -90 X rotation baked into the object
    /// and the axle is then whichever axis the artist happened to leave it on.
    /// </summary>
    Vector3 DetectAxle()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null) mf = GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return Vector3.right;

        Vector3 size = mf.sharedMesh.bounds.size;

        Vector3 inMeshLocal;
        if (size.x <= size.y && size.x <= size.z)      inMeshLocal = Vector3.right;
        else if (size.y <= size.x && size.y <= size.z) inMeshLocal = Vector3.up;
        else                                           inMeshLocal = Vector3.forward;

        // Mesh local -> world -> this wheel's local, then snapped to the nearest cardinal axis.
        Vector3 world = mf.transform.TransformDirection(inMeshLocal);
        Vector3 local = transform.InverseTransformDirection(world).normalized;

        float ax = Mathf.Abs(local.x), ay = Mathf.Abs(local.y), az = Mathf.Abs(local.z);
        if (ax >= ay && ax >= az) return Vector3.right;
        if (ay >= ax && ay >= az) return Vector3.up;
        return Vector3.forward;
    }

    /// <summary>
    /// Half the widest cross-section, in world units so the import scale is already included.
    /// Never extents.y - on a wheel whose pivot came in rotated that is the axle half-width, and
    /// a radius eight times too small is the difference between a wheel that spins and one that
    /// looks welded in place.
    /// </summary>
    float MeasureRadius()
    {
        var r = GetComponent<Renderer>();
        if (r == null) r = GetComponentInChildren<Renderer>();
        if (r == null) return 0.35f;

        Vector3 e = r.bounds.extents;
        float measured = Mathf.Max(e.x, Mathf.Max(e.y, e.z));

        return (measured > 0.05f && measured < 3f) ? measured : 0.35f;
    }

    [ContextMenu("Re-measure Wheel")]
    public void Remeasure()
    {
        _axleLocal = axle == AxleAxis.Auto ? DetectAxle() : FixedAxis();
        _radius = radius > 0.001f ? radius : MeasureRadius();
        Debug.Log($"[WheelSpin] '{name}': axle {_axleLocal}, radius {_radius:0.000} m.", this);
    }

    void OnDrawGizmosSelected()
    {
        Vector3 a = transform.TransformDirection(
            Application.isPlaying ? _axleLocal
                                  : (axle == AxleAxis.Auto ? DetectAxle() : FixedAxis()));

        float r = Application.isPlaying ? _radius
                                        : (radius > 0.001f ? radius : MeasureRadius());

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position - a * r, transform.position + a * r);

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, r);
    }
}
