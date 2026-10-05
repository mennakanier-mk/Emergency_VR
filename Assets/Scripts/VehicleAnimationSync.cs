using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stops a vehicle's own animation (spinning wheels, pedalling, wheel wobble) when the vehicle
/// stops, and runs it at a pace that follows the vehicle's speed.
///
/// Wheels turned by WheelSpin already stop by themselves - they measure how far they rolled.
/// But several models (moto, bicycles, some cars and trucks) spin their wheels inside their
/// animation clip, and a clip keeps playing whatever the vehicle does: stuck at a red light or
/// in a jam, the wheels went on turning. This ties that clip to the real movement.
///
/// Added automatically to every vehicle (RoadDriver that is not a pedestrian, and every
/// AnimatedVehicleMover) when a scene loads - nothing to set up. Animators that other scripts
/// already drive (passengers getting out, the ambulance crew) are left alone.
/// </summary>
[DefaultExecutionOrder(300)]
[DisallowMultipleComponent]
public class VehicleAnimationSync : MonoBehaviour
{
    [Tooltip("Below this speed (m/s) the vehicle counts as stopped and its animation freezes.")]
    public float stopSpeed = 0.15f;

    [Tooltip("Speed (m/s) at which the animation plays at its normal rate. 0 = the driver's " +
             "cruise speed.")]
    public float referenceSpeed = 0f;

    [Tooltip("How quickly the animation slows down / speeds up (per second). Lower = softer.")]
    public float response = 4f;

    [Range(1f, 3f)] public float maxRate = 1.5f;

    RoadDriver _driver;
    AnimatedVehicleMover _mover;
    readonly List<Animator> _anims = new List<Animator>();
    readonly List<float> _base = new List<float>();
    Vector3 _lastPos;
    float _speed, _rate = 1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoAttach()
    {
        foreach (var d in Object.FindObjectsByType<RoadDriver>(FindObjectsSortMode.None))
            if (!(d is PedestrianWalker) && d.GetComponent<VehicleAnimationSync>() == null)
                d.gameObject.AddComponent<VehicleAnimationSync>();

        foreach (var m in Object.FindObjectsByType<AnimatedVehicleMover>(FindObjectsSortMode.None))
            if (m.GetComponent<VehicleAnimationSync>() == null)
                m.gameObject.AddComponent<VehicleAnimationSync>();
    }

    void Start()
    {
        _driver = GetComponent<RoadDriver>();
        _mover = GetComponent<AnimatedVehicleMover>();
        _lastPos = transform.position;

        // The ambulance drives its own crew/door animation speed - do not fight it.
        if (GetComponent<AmbulanceResponse>() != null) { enabled = false; return; }

        foreach (var a in GetComponentsInChildren<Animator>(true))
            if (a != null) _anims.Add(a);
        if (_anims.Count == 0) enabled = false;
    }

    bool _haveBase;

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Base rates read on the first frame, after every Start (AnimatedVehicleMover sets its
        // multiplier there), so a deliberately slower/faster clip keeps its pace.
        if (!_haveBase)
        {
            _haveBase = true;
            foreach (var a in _anims) _base.Add(a != null && a.speed > 0.0001f ? a.speed : 1f);
        }

        // Real speed over the ground, whatever moved the vehicle.
        Vector3 p = transform.position;
        Vector3 d = p - _lastPos;
        _lastPos = p;
        d.y = 0f;
        float v = d.magnitude / dt;
        if (v > 60f) v = _speed;                         // a teleport, not driving
        _speed = Mathf.Lerp(_speed, v, 1f - Mathf.Exp(-10f * dt));

        float reference = referenceSpeed > 0.01f ? referenceSpeed
                        : _driver != null && _driver.speed > 0.5f ? _driver.speed
                        : 5f;

        float target = _speed < stopSpeed ? 0f : Mathf.Clamp(_speed / reference, 0.25f, maxRate);
        _rate = Mathf.MoveTowards(_rate, target, response * dt);

        for (int i = 0; i < _anims.Count; i++)
        {
            var a = _anims[i];
            if (a == null) continue;
            if (IsDrivenElsewhere(a)) continue;
            a.speed = _base[i] * _rate;
        }
    }

    /// <summary>People getting out of the car, the paramedic, a carried boy: their own scripts own them.</summary>
    static bool IsDrivenElsewhere(Animator a)
    {
        return a.GetComponent<CarExitPassenger>() != null
            || a.GetComponentInParent<ParamedicRig>() != null
            || a.GetComponentInParent<BoyCarry>() != null;
    }

    void OnDisable()
    {
        for (int i = 0; i < _anims.Count && i < _base.Count; i++)
            if (_anims[i] != null && !IsDrivenElsewhere(_anims[i])) _anims[i].speed = _base[i];
    }
}
