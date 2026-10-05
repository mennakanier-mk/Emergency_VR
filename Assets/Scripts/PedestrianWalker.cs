using UnityEngine;

/// <summary>
/// Put this on a person and drag a walkway into Path. That is the whole setup.
///
///   1. Select the person in the Hierarchy.
///   2. Add Component > Pedestrian Walker.
///   3. Drag a "Walkway" object from the Hierarchy into the Path slot.
///   4. Press Play.
///
/// They start from wherever they are standing, so you place people by eye and they join the
/// pavement at the nearest point rather than teleporting to the start of it.
///
/// Everything the cars use is shared - the path maths, the queueing, the facing - with walking
/// defaults instead of driving ones: slower, much closer together, and a little variation in
/// pace so a crowd does not march in lockstep.
///
/// If the model has a walk animation, drop its Animator in and name the float parameter; it is
/// fed the speed each frame, so the legs move at the pace the body does and stop when it stops.
/// </summary>
[AddComponentMenu("Emergency VR/Pedestrian Walker")]
public class PedestrianWalker : RoadDriver
{
    [Header("Walking")]
    [Tooltip("Comfortable walking pace in metres per second. 1.2-1.4 is a person strolling.")]
    public float walkSpeed = 1.3f;

    [Tooltip("Random spread either side of that speed, as a fraction. A little variation is " +
             "what stops a group of people looking like one object copied several times.")]
    [Range(0f, 0.6f)] public float speedVariation = 0.2f;

    [Header("Animation (optional)")]
    [Tooltip("Leave empty to find one on this object or its children.")]
    public Animator animator;

    [Tooltip("Float parameter on the Animator that drives the walk cycle. Cleared if the name " +
             "does not exist, so a wrong name cannot spam the console.")]
    public string speedParameter = "Speed";

    [Tooltip("Multiplies the speed before it reaches the Animator, for controllers whose walk " +
             "blend expects a 0-1 value rather than metres per second.")]
    public float animatorSpeedScale = 1f;

    [Tooltip("Optional bool parameter set while moving.")]
    public string walkingParameter = "";

    int _speedHash, _walkingHash;
    bool _hasSpeed, _hasWalking;

    void Reset()
    {
        // Sensible the moment it is added, so the only thing left to do is drag the path in.
        speed = 1.3f;
        smoothing = 0.4f;
        snapOnStart = true;
    }

    void Awake()
    {
        // Each person gets their own pace, seeded from their position so it is the same every
        // run - a crowd that reshuffles itself on every Play is impossible to art-direct.
        float seed = Mathf.Abs(transform.position.x * 73.1f + transform.position.z * 19.7f);
        float offset = (Mathf.Repeat(seed, 1f) * 2f - 1f) * speedVariation;

        speed = Mathf.Max(0.2f, walkSpeed * (1f + offset));

        if (animator == null) animator = GetComponentInChildren<Animator>();
        CacheParameters();
    }

    void CacheParameters()
    {
        _hasSpeed = false;
        _hasWalking = false;

        if (animator == null || animator.runtimeAnimatorController == null) return;

        foreach (var p in animator.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Float &&
                p.name == speedParameter)
            {
                _speedHash = p.nameHash;
                _hasSpeed = true;
            }

            if (p.type == AnimatorControllerParameterType.Bool &&
                !string.IsNullOrEmpty(walkingParameter) && p.name == walkingParameter)
            {
                _walkingHash = p.nameHash;
                _hasWalking = true;
            }
        }
    }

    protected override void AfterMove(float currentSpeed)
    {
        if (animator == null) return;

        if (_hasSpeed) animator.SetFloat(_speedHash, currentSpeed * animatorSpeedScale);
        if (_hasWalking) animator.SetBool(_walkingHash, currentSpeed > 0.05f);
    }

    [ContextMenu("Snap To Path Now")]
    public void SnapNow()
    {
        SnapToNearest();
    }
}
