using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Makes a baked Blender animation follow the real ground: up onto the kerb, down onto the road.
///
/// A clip animated in Blender is authored on a flat floor, so every frame is played at that one
/// height - the boy runs off the pavement and keeps going at pavement height, floating above
/// the road; the ball rolls into the street in mid-air.
///
/// Every frame, after the Animator has posed the model, this looks at the ground under each
/// tracked part (the character's Hips, and any free-moving prop like the ball) and lifts or
/// drops that part by how far the ground there is above or below the floor the clip was
/// authored on. The authored floor is the height of this object's own pivot - so place the
/// object with its pivot on the surface where the animation starts (the pavement).
///
/// Nothing else in the clip changes: arms, legs, timing and the fall all play as animated.
///
/// Setup: Add Component > Ground Follow Animated on the object that has the Animator. With
/// Auto Find on, it tracks the Hips bone plus every non-skinned mesh child that is a direct
/// child of this object (the ball). Add anything else to Extra Targets.
/// </summary>
[DefaultExecutionOrder(1000)]   // after anything else that poses the model in LateUpdate
[DisallowMultipleComponent]
public class GroundFollowAnimated : MonoBehaviour
{
    [Tooltip("Track the Hips bone and loose props (direct-child meshes) automatically.")]
    public bool autoFind = true;

    [Tooltip("More parts to keep on the ground, e.g. a second prop.")]
    public List<Transform> extraTargets = new List<Transform>();

    [Header("Ground probe")]
    [Tooltip("Start the downward ray this far above the authored floor.")]
    public float probeHeight = 2f;

    [Tooltip("How far down to look.")]
    public float probeDepth = 10f;

    [Tooltip("Layers counted as ground.")]
    public LayerMask groundLayers = ~0;

    [Header("Feel")]
    [Tooltip("How fast a part settles onto a new height (per second). High = a sharp step " +
             "off the kerb, low = a smooth ramp.")]
    [Range(1f, 40f)] public float settleSpeed = 14f;

    [Tooltip("Ignore height changes bigger than this - a wall, car roof or a hole in the " +
             "colliders, not a kerb.")]
    public float maxStep = 0.6f;

    [Header("Debug")]
    public bool drawGizmos = false;

    class Tracked
    {
        public Transform t;
        public float offset;
        public bool primed;
    }

    readonly List<Tracked> _tracked = new List<Tracked>();

    void Start()
    {
        var set = new HashSet<Transform>();

        if (autoFind)
        {
            var hips = FindHips();
            if (hips != null) set.Add(hips);

            foreach (Transform child in transform)
            {
                var r = child.GetComponent<Renderer>();
                if (r != null && !(r is SkinnedMeshRenderer)) set.Add(child);
            }
        }

        foreach (var t in extraTargets)
            if (t != null) set.Add(t);

        foreach (var t in set)
            _tracked.Add(new Tracked { t = t });

        if (_tracked.Count == 0)
            Debug.LogWarning($"[GroundFollow] '{name}': nothing to track - add Extra Targets.", this);
        else
            Debug.Log($"[GroundFollow] '{name}': tracking {string.Join(", ", _tracked.ConvertAll(x => x.t.name))}", this);
    }

    void LateUpdate()
    {
        float floor = transform.position.y;     // the flat floor the clip was authored on
        float dt = Time.deltaTime;
        float k = 1f - Mathf.Exp(-settleSpeed * dt);

        foreach (var tr in _tracked)
        {
            if (tr.t == null) continue;

            Vector3 p = tr.t.position;           // as posed by the Animator this frame

            if (TryGround(new Vector3(p.x, floor, p.z), out float ground))
            {
                float target = ground - floor;
                if (Mathf.Abs(target - tr.offset) <= maxStep || !tr.primed)
                {
                    tr.offset = tr.primed ? Mathf.Lerp(tr.offset, target, k) : target;
                    tr.primed = true;
                }
            }

            tr.t.position = p + Vector3.up * tr.offset;
        }
    }

    bool TryGround(Vector3 onFloor, out float height)
    {
        Vector3 origin = onFloor + Vector3.up * probeHeight;
        var hits = Physics.RaycastAll(origin, Vector3.down, probeHeight + probeDepth,
                                      groundLayers, QueryTriggerInteraction.Ignore);

        height = float.NegativeInfinity;
        foreach (var h in hits)
        {
            if (h.transform == transform || h.transform.IsChildOf(transform)) continue;
            if (IsMoving(h.collider)) continue;
            if (h.point.y > height) height = h.point.y;
        }
        return !float.IsNegativeInfinity(height);
    }

    /// <summary>
    /// Traffic is not ground. A car driving past (or over) the boy would otherwise be stood on -
    /// its bonnet is low enough to pass the Max Step check.
    /// </summary>
    static bool IsMoving(Collider c)
    {
        if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) return true;
        return c.GetComponentInParent<RoadDriver>() != null
            || c.GetComponentInParent<VehicleController>() != null
            || c.GetComponentInParent<AnimatedVehicleMover>() != null
            || c.GetComponentInParent<PedestrianWalker>() != null;
    }

    Transform FindHips()
    {
        var anim = GetComponentInChildren<Animator>();
        if (anim != null && anim.isHuman)
        {
            var h = anim.GetBoneTransform(HumanBodyBones.Hips);
            if (h != null) return h;
        }

        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (t.name.ToLowerInvariant().EndsWith("hips")) return t;

        return null;
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        Gizmos.color = Color.cyan;
        foreach (var tr in _tracked)
        {
            if (tr.t == null) continue;
            Vector3 p = tr.t.position;
            Gizmos.DrawLine(p, p + Vector3.down * 3f);
        }
    }
}
