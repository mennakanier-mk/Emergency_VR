using UnityEngine;

/// <summary>
/// A person sitting in a car whose single clip is "sit ... get out ... walk". Holds them in the
/// seated pose until the car's door opens, plays the getting-out part (a little faster), then
/// steers the walk towards the victim and stops a step short of him with the feet together.
///
/// Added automatically by AccidentCar to every animated character parented under the car.
/// Timings are for try_car_exit_man: seated until Blender frame ~172 (7.0 s into the clip),
/// standing upright by frame ~256 (10.6 s), walking after that. The walk in the clip heads away
/// from the car, so once he is up, the whole character is turned about his hips until the walk
/// points at the victim.
/// </summary>
[DisallowMultipleComponent]
public class CarExitPassenger : MonoBehaviour
{
    [Tooltip("Seconds into the clip where the person starts getting out.")]
    public float exitStartTime = 7.0f;

    [Tooltip("Seconds into the clip to hold while seated.")]
    public float seatedPoseTime = 0f;

    [Tooltip("Seconds into the clip where they are upright and start walking.")]
    public float walkStartTime = 10.6f;

    [Tooltip("Playback speed of the getting-out and walk. 1 = as animated.")]
    [Range(0.5f, 2.5f)] public float playbackSpeed = 1.4f;

    [Tooltip("Steer the walk: round the open door, then to a spot in front of the car. Off: " +
             "the clip plays as animated.")]
    public bool steer = true;

    [Tooltip("How close to the final spot counts as arrived, metres.")]
    public float stopDistance = 0.35f;

    [Tooltip("How fast he turns towards the victim, degrees per second.")]
    public float turnSpeed = 140f;

    Animator _anim;
    AnimationClip _clip;
    Transform _hips, _lFoot, _rFoot, _victim, _victimHips;
    int _state;
    float _length;
    bool _held, _released, _stopped;
    float _releaseAt = -1f;

    Vector3 _walkDir;           // world direction the clip is walking, measured once
    Vector3 _measureFrom;
    float _measureUntil = -1f;
    float _stopClipTime = -1f;

    [Tooltip("Playback speed of getting back in (the get-out part, played backwards).")]
    [Range(0.5f, 2.5f)] public float getInSpeed = 1.2f;

    AccidentCar _car;

    // Going back: walk to where the walk began, turn round, play the getting-out backwards.
    Vector3 _seatLocalPos; Quaternion _seatLocalRot; bool _seatKnown;
    Vector3 _walkHips0, _walkDir0; bool _walkStartKnown;
    int _ret;                   // 0 none, 1 turn to go, 2 walk, 3 turn round, 4 line up, 5 sit back in, 6 seated
    Vector3[] _retRoute; int _retLeg;
    float _retT, _clipT, _retClipStop = -1f;
    bool _compPending; Vector3 _compHips;
    Vector3 _lineFromPos; Quaternion _lineFromRot;

    /// <summary>Back in his seat.</summary>
    public bool Seated => _ret == 6 || (!_released && _held);

    /// <summary>Walk back to the car and get in.</summary>
    public void ReturnToCar()
    {
        if (_ret != 0 || !_released || !_seatKnown || !_walkStartKnown || _hips == null) { _ret = 6; return; }
        Vector3 a = Vector3.zero;
        bool haveA = _car != null && _car.GetExitRoute(out a, out _, out _);
        // Last point a little out from where his walk began, so he arrives walking towards the car.
        Vector3 approach = _walkHips0 + _walkDir0 * 0.6f;
        _retRoute = haveA ? new[] { a, approach, _walkHips0 } : new[] { approach, _walkHips0 };
        _retLeg = 0;
        _stopped = false; _facing = false;
        _ret = 1;
    }
    Vector3[] _route;
    int _leg;
    Vector3 _lookAt;
    bool _facing;

    public void Bind(AccidentCar car, float delay, Transform victim)
    {
        _victim = victim;
        _car = car;
        car.DoorOpening += () => _releaseAt = Time.time + Mathf.Max(0f, delay);
    }

    void Start()
    {
        _anim = GetComponent<Animator>();
        if (_anim == null) { enabled = false; return; }
        _seatLocalPos = transform.localPosition; _seatLocalRot = transform.localRotation; _seatKnown = true;

        var clips = _anim.runtimeAnimatorController != null
            ? _anim.runtimeAnimatorController.animationClips : null;
        if (clips != null && clips.Length > 0) _clip = clips[0];

        foreach (var t in GetComponentsInChildren<Transform>())
        {
            string n = t.name.ToLowerInvariant();
            if (n.EndsWith("hips")) _hips = t;
            else if (n.EndsWith("leftfoot")) _lFoot = t;
            else if (n.EndsWith("rightfoot")) _rFoot = t;
        }

        if (_victim != null)
            foreach (var t in _victim.GetComponentsInChildren<Transform>())
                if (t.name.ToLowerInvariant().EndsWith("hips")) { _victimHips = t; break; }
    }

    float ClipTime => _anim.GetCurrentAnimatorStateInfo(0).normalizedTime * _length;

    void Update()
    {
        if (_anim == null) return;

        if (!_held)
        {
            var info = _anim.GetCurrentAnimatorStateInfo(0);
            _state = info.fullPathHash;
            _length = Mathf.Max(0.01f, info.length);
            _anim.Play(_state, 0, Mathf.Clamp01(seatedPoseTime / _length));
            _anim.speed = 0f;
            _held = true;
            return;
        }

        if (!_released)
        {
            if (_releaseAt >= 0f && Time.time >= _releaseAt)
            {
                _released = true;
                _anim.Play(_state, 0, Mathf.Clamp01(exitStartTime / _length));
                _anim.speed = playbackSpeed;
            }
            return;
        }

        if (_ret != 0) { ReturnUpdate(); return; }

        if (_stopped || !steer || _hips == null) return;

        float t = ClipTime;
        if (t < walkStartTime) return;
        if (!_walkStartKnown) { _walkHips0 = _hips.position; _walkStartKnown = true; }

        // 1. Measure which way the clip walks: hips travel over a short window.
        if (_walkDir == Vector3.zero)
        {
            if (_measureUntil < 0f) { _measureFrom = _hips.position; _measureUntil = t + 0.4f; return; }
            if (t < _measureUntil) return;
            Vector3 d = _hips.position - _measureFrom; d.y = 0f;
            _walkDir = d.sqrMagnitude > 1e-4f ? d.normalized : transform.forward;
            _walkDir0 = _walkDir;
        }
    }

    // ---------------------------------------------------------------- getting back in

    /// <summary>Jump the clip to another time without the hips jumping: the shift is taken out in LateUpdate.</summary>
    void JumpClip(float t)
    {
        _compPending = true; _compHips = _hips.position;
        _clipT = t;
        _anim.Play(_state, 0, Mathf.Clamp01(t / _length));
    }

    void ReturnUpdate()
    {
        float dt = Time.deltaTime;
        switch (_ret)
        {
            case 1:     // turn on the spot towards the way back (standing frame held)
            {
                _anim.speed = 0f;
                Vector3 goal = _retRoute[0];
                TurnTowards(goal, turnSpeed);
                Vector3 to = goal - _hips.position; to.y = 0f;
                if (to.sqrMagnitude < 0.01f || Vector3.Angle(_walkDir, to) < 6f)
                {
                    // Into the walk part of the clip (a step in, so he is already moving).
                    JumpClip(walkStartTime + 0.4f);
                    _anim.speed = playbackSpeed;
                    _retClipStop = -1f;
                    _ret = 2;
                }
                break;
            }
            case 2:     // walk the route back
            {
                float t = ClipTime;
                if (t >= _length - 0.1f) { JumpClip(walkStartTime + 0.4f); _anim.speed = playbackSpeed; }
                break;
            }
            case 4:     // line up with where he was when the walk began, upright frame
            {
                _retT += dt / 0.45f;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_retT));
                transform.localPosition = Vector3.Lerp(_lineFromPos, _seatLocalPos, k);
                transform.localRotation = Quaternion.Slerp(_lineFromRot, _seatLocalRot, k);
                if (_retT >= 1f) { _clipT = walkStartTime; _ret = 5; }
                break;
            }
            case 5:     // the getting-out part, backwards, until seated
            {
                _anim.speed = 0f;
                _clipT = Mathf.Max(exitStartTime, _clipT - dt * getInSpeed);
                _anim.Play(_state, 0, Mathf.Clamp01(_clipT / _length));
                if (_clipT <= exitStartTime + 1e-3f)
                {
                    _anim.Play(_state, 0, Mathf.Clamp01(seatedPoseTime / _length));
                    _ret = 6;
                }
                break;
            }
        }
    }

    void ReturnLate()
    {
        if (_compPending)
        {
            _compPending = false;
            Vector3 d = _compHips - _hips.position; d.y = 0f;
            transform.position += d;
        }

        if (_ret == 2)
        {
            Vector3 goal = _retRoute[_retLeg];
            Vector3 to = goal - _hips.position; to.y = 0f;
            float dist = to.magnitude;
            bool last = _retLeg == _retRoute.Length - 1;
            if (!last && dist < 0.6f) { _retLeg++; return; }
            TurnTowards(goal, turnSpeed);
            if (last && _retClipStop < 0f && dist <= stopDistance + 0.5f) _retClipStop = FeetTogetherTimeAfter(ClipTime);
            if (_retClipStop >= 0f && ClipTime >= _retClipStop)
            {
                _anim.speed = 0f;
                _ret = 3;
            }
        }
        else if (_ret == 3)
        {
            // Turn round to face the way the walk first went (his back to the seat).
            float delta = Vector3.SignedAngle(_walkDir, _walkDir0, Vector3.up);
            float step = Mathf.Clamp(delta, -turnSpeed * Time.deltaTime, turnSpeed * Time.deltaTime);
            transform.RotateAround(_hips.position, Vector3.up, step);
            _walkDir = Quaternion.AngleAxis(step, Vector3.up) * _walkDir;
            if (Mathf.Abs(delta) < 2f)
            {
                // Upright frame of the clip, hips kept where they are, then ease into the seat pose.
                JumpClip(walkStartTime);
                _anim.speed = 0f;
                _ret = 40;      // compensate next LateUpdate, then line up
            }
        }
        else if (_ret == 40 && !_compPending)
        {
            _lineFromPos = transform.localPosition; _lineFromRot = transform.localRotation;
            _retT = 0f;
            _ret = 4;
        }
    }

    void LateUpdate()
    {
        if (_hips == null) return;
        if (_ret != 0) { ReturnLate(); return; }

        // After stopping: turn on the spot to look at the boy.
        if (_stopped)
        {
            if (AccidentBystander.watch != null) _lookAt = AccidentBystander.watch.position;   // follow the boy
            if (_facing) TurnTowards(_lookAt, turnSpeed * 0.6f);
            return;
        }

        if (!_released || _walkDir == Vector3.zero) return;

        if (_route == null)
        {
            if (_car == null || !_car.GetExitRoute(out Vector3 a, out Vector3 b, out _lookAt))
            {
                Vector3 v = (_victimHips != null ? _victimHips : _victim).position;
                _route = new[] { v };
                _lookAt = v;
            }
            else _route = new[] { a, b };
            _leg = 0;
        }

        Vector3 goal = _route[_leg];
        Vector3 to = goal - _hips.position; to.y = 0f;
        float dist = to.magnitude;
        bool last = _leg == _route.Length - 1;

        if (!last && dist < 0.6f) { _leg++; return; }

        TurnTowards(goal, turnSpeed);

        // Final spot: stop on a frame with the feet together, then face the boy.
        if (last && _stopClipTime < 0f && dist <= stopDistance + 0.6f)
            _stopClipTime = FeetTogetherTimeAfter(ClipTime);

        if (_stopClipTime >= 0f && ClipTime >= _stopClipTime)
        {
            _anim.speed = 0f;
            _stopped = true;
            _facing = true;
        }
    }

    /// <summary>Turn the whole character about his hips so his walk heads at a point.</summary>
    void TurnTowards(Vector3 point, float degPerSec)
    {
        Vector3 to = point - _hips.position; to.y = 0f;
        if (to.sqrMagnitude < 0.0025f) return;

        // While walking the clip's own direction is what has to line up; standing, his facing.
        Vector3 cur = _stopped ? FacingDir() : _walkDir;
        float delta = Vector3.SignedAngle(cur, to, Vector3.up);
        float step = Mathf.Clamp(delta, -degPerSec * Time.deltaTime, degPerSec * Time.deltaTime);
        if (Mathf.Abs(step) < 0.001f) return;

        transform.RotateAround(_hips.position, Vector3.up, step);
        _walkDir = Quaternion.AngleAxis(step, Vector3.up) * _walkDir;
    }

    /// <summary>Where his body points: the walk direction he last had.</summary>
    Vector3 FacingDir() => _walkDir;

    /// <summary>Clip time in the next ~0.8 s with the feet closest together.</summary>
    float FeetTogetherTimeAfter(float from)
    {
        if (_clip == null || _lFoot == null || _rFoot == null) return from;

        float best = float.MaxValue, bestT = from;
        for (int i = 1; i <= 16; i++)
        {
            float tt = Mathf.Min(from + i * 0.05f, _clip.length);
            _clip.SampleAnimation(gameObject, tt);
            Vector3 d = transform.InverseTransformVector(_lFoot.position - _rFoot.position);
            float h = new Vector2(d.x, d.z).magnitude;
            if (h < best) { best = h; bestT = tt; }
        }
        _clip.SampleAnimation(gameObject, from);    // put this frame's pose back
        return bestT;
    }
}
