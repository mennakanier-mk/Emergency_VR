using UnityEngine;

/// <summary>
/// A pedestrian reacting to the accident: after a short, distance-based reaction time they slow
/// to a stop, their legs settle with the feet together, and they turn to face the accident.
///
/// Added at the moment of impact by AccidentCar to every PedestrianWalker within its Bystander
/// Radius. Nearer people react first (they see it), further ones a little later (they hear it),
/// plus a small per-person variation so a crowd does not turn in unison.
/// </summary>
[DisallowMultipleComponent]
public class AccidentBystander : MonoBehaviour
{
    public Vector3 target;
    public float reactionDelay;

    [Tooltip("Seconds from walking pace to standing still.")]
    public float stopTime = 0.7f;

    [Tooltip("Turning speed while turning to look, degrees per second.")]
    public float turnSpeed = 110f;

    [Tooltip("Run to the accident (true) or stop where they are and look (false).")]
    public bool runToIt = true;

    [Tooltip("Running speed, m/s.")]
    public float runSpeed = 3.2f;

    [Tooltip("Where they stop, metres from the accident (each person gets their own spot on " +
             "a ring this far out, on their side of it).")]
    public float standOff = 3f;

    [Tooltip("Animation speed per m/s of travel - the walk clip played faster reads as a jog.")]
    public float animPerMetre = 0.75f;

    public float angleJitter;   // degrees, set by AlertAll so people spread round the ring

    RoadDriver _driver;
    Animator _anim;
    AnimationClip _clip;
    Transform _lFoot, _rFoot;
    float _stopPhase = -1f;     // normalized clip phase with the feet closest together
    float _t0, _startSpeed;
    int _stage;
    bool _anim_settle;                 // 0 waiting, 1 slowing, 2 settling legs, 3 standing

    /// <summary>Alert every walker near the accident.</summary>
    /// <summary>What they keep their eyes on: the boy (his hips), wherever he is carried.</summary>
    public static Transform watch;

    public static void AlertAll(Vector3 at, float radius, float runRadius = 25f, Transform victim = null)
    {
        watch = null;
        if (victim != null)
        {
            foreach (var t in victim.GetComponentsInChildren<Transform>())
                if (t.name.ToLowerInvariant().EndsWith("hips")) { watch = t; break; }
            if (watch == null) watch = victim;
        }
        int n = 0;
        foreach (var w in FindObjectsByType<PedestrianWalker>(FindObjectsSortMode.None))
        {
            if (!w.isActiveAndEnabled) continue;
            float d = Vector3.Distance(w.transform.position, at);
            if (d > radius) continue;

            var b = w.GetComponent<AccidentBystander>();
            if (b == null) b = w.gameObject.AddComponent<AccidentBystander>();

            // Seeded from position: the same person reacts the same way every run.
            float jitter = Mathf.Repeat(Mathf.Abs(w.transform.position.x * 12.9898f +
                                                  w.transform.position.z * 78.233f), 1f);
            b.target = at;
            b.reactionDelay = 0.25f + d * 0.02f + jitter * 0.5f;
            b.runToIt = d <= runRadius;
            b.angleJitter = (jitter - 0.5f) * 50f;
            b.standOff = 2.6f + jitter * 1.6f;
            n++;
        }
        Debug.Log($"[Bystanders] {n} people within {radius:0} m react to the accident.");
    }

    /// <summary>Everyone who stopped for the accident goes back to walking their route.</summary>
    public static void ResumeAll()
    {
        int n = 0;
        foreach (var b in FindObjectsByType<AccidentBystander>(FindObjectsSortMode.None))
        {
            if (!b.isActiveAndEnabled) continue;
            float jitter = Mathf.Repeat(Mathf.Abs(b.transform.position.x * 12.9898f + b.transform.position.z * 78.233f), 1f);
            b.WalkOn(0.3f + jitter * 2.2f);
            n++;
        }
        if (n > 0) Debug.Log($"[Bystanders] {n} people walk on.");
    }

    float _resumeAt = -1f;
    Vector3 _rejoin;

    /// <summary>After 'delay' seconds walk back to the nearest point of their route and carry on.</summary>
    public void WalkOn(float delay)
    {
        if (_driver == null) _driver = GetComponent<RoadDriver>();
        if (_driver == null) { enabled = false; return; }
        _resumeAt = Time.time + delay;
        _stage = 5;
    }

    /// <summary>
    /// A spot 'radius' from 'centre' in direction 'dir', but on the same level as the centre (the
    /// road): if it lands up on the kerb / pavement it is pulled in, and failing that turned round,
    /// so nobody ends up standing above everyone else.
    /// </summary>
    public static Vector3 OnRoadLevel(Vector3 centre, Vector3 dir, float radius, float minRadius = 2.4f)
    {
        float road = GroundAt(centre);
        dir.y = 0f; dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
        for (int turn = 0; turn < 8; turn++)
        {
            Vector3 d = Quaternion.AngleAxis(turn * (turn % 2 == 0 ? 25f : -25f), Vector3.up) * dir;
            for (float r = radius; r >= minRadius; r -= 0.4f)
            {
                Vector3 p = centre + d * r;
                if (GroundAt(p) < road + 0.12f) { p.y = centre.y; return p; }
            }
        }
        Vector3 q = centre + dir * minRadius; q.y = centre.y;
        return q;
    }

    static float GroundAt(Vector3 at)
    {
        var hits = Physics.RaycastAll(at + Vector3.up * 8f, Vector3.down, 30f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MinValue;
        foreach (var h in hits)
        {
            if (h.collider.GetComponentInParent<RoadDriver>() != null) continue;
            if (h.collider.GetComponentInParent<Animator>() != null) continue;      // people
            var rb = h.collider.attachedRigidbody; if (rb != null && !rb.isKinematic) continue;
            if (h.point.y > best && h.point.y < at.y + 4f) best = h.point.y;
        }
        return best > float.MinValue ? best : at.y;
    }

    /// <summary>
    /// Walk to another spot (e.g. out of the paramedic's way) and settle there looking at
    /// 'lookAt'. Works from any stage.
    /// </summary>
    public void MoveAside(Vector3 spot, Vector3 lookAt, float walkSpeed = 2.2f)
    {
        if (_driver == null) _driver = GetComponent<RoadDriver>();
        if (_driver == null) return;
        target = lookAt;
        _driver.lookTurnSpeed = turnSpeed;
        _driver.BeginFreeRoam(spot, walkSpeed, 0.15f, lookAt);
        _t0 = Time.time;
        reactionDelay = 0f;
        _stage = 4;
    }

    void Start()
    {
        if (_driver == null) _driver = GetComponent<RoadDriver>();
        _anim = GetComponentInChildren<Animator>();
        _t0 = Time.time;

        if (_anim != null && _anim.runtimeAnimatorController != null)
        {
            var clips = _anim.runtimeAnimatorController.animationClips;
            if (clips.Length > 0) _clip = clips[0];

            foreach (var t in _anim.GetComponentsInChildren<Transform>())
            {
                string n = t.name.ToLowerInvariant();
                if (n.EndsWith("leftfoot")) _lFoot = t;
                else if (n.EndsWith("rightfoot")) _rFoot = t;
            }
        }
    }

    void Update()
    {
        if (_driver == null) { enabled = false; return; }
        float now = Time.time - _t0;

        // Watching: keep turning to follow the boy - lying there, being lifted, carried to the
        // stretcher and into the ambulance.
        if (watch != null && _stage >= 1 && _stage <= 4)
        {
            target = watch.position;
            _driver.lookTarget = target;
        }

        switch (_stage)
        {
            case 0:
                if (now < reactionDelay) return;
                _startSpeed = _driver.CurrentSpeed;
                _driver.lookTurnSpeed = turnSpeed;
                _t0 = Time.time;

                if (runToIt)
                {
                    // A spot on a ring round the accident, on this person's side of it.
                    Vector3 from = transform.position - target; from.y = 0f;
                    if (from.sqrMagnitude < 0.01f) from = Vector3.forward;
                    from = Quaternion.AngleAxis(angleJitter, Vector3.up) * from.normalized;
                    _driver.BeginFreeRoam(OnRoadLevel(target, from, standOff), runSpeed, 0.15f, target);
                    _stage = 4;
                }
                else
                {
                    _driver.externalControl = true;
                    _driver.externalSpeed = _startSpeed;
                    _stage = 1;
                }
                break;

            case 5:     // waiting a moment, then off back to the pavement
                if (Time.time < _resumeAt) return;
                if (_driver.path == null) { _driver.ResumePath(); Finish(); return; }
                if (!_driver.freeRoam)
                {
                    // Stopped on the route itself (did not run over): just carry on.
                    _driver.ResumePath();
                    Finish();
                    return;
                }
                {
                    float d = _driver.NearestOnPath(transform.position);
                    _driver.path.Sample(d, out Vector3 q, out Vector3 dir);
                    _rejoin = q;
                    _driver.BeginFreeRoam(q, Mathf.Max(1.0f, _driver.speed), 0.25f, q + dir);
                    _driver.lookTurnSpeed = 200f;
                    _t0 = Time.time;
                    _stage = 6;
                }
                break;

            case 6:     // walking back
            {
                if (_anim != null)
                    _anim.speed = Mathf.Clamp(_driver.CurrentSpeed * animPerMetre / Mathf.Max(0.5f, _driver.speed * animPerMetre), 0.3f, 1.6f);
                Vector3 to = _rejoin - transform.position; to.y = 0f;
                if ((to.magnitude < 0.45f && _driver.CurrentSpeed < 0.6f) || Time.time - _t0 > 25f)
                {
                    _driver.ResumePath();
                    Finish();
                }
                break;
            }

            case 4:
                // Running in: legs follow the body's speed; once they have arrived, settle.
                if (_anim != null)
                    _anim.speed = Mathf.Clamp(_driver.CurrentSpeed * animPerMetre, 0.6f, 2.6f);
                if (_driver.CurrentSpeed < 0.05f && now > 0.5f)
                {
                    _stage = 2;
                }
                break;

            case 1:
            {
                float k = Mathf.Clamp01(now / Mathf.Max(0.05f, stopTime));
                _driver.externalSpeed = Mathf.Lerp(_startSpeed, 0f, k);
                if (_anim != null) _anim.speed = Mathf.Lerp(1f, 0.6f, k);   // legs slow with the body
                if (k >= 0.6f) { _driver.lookTarget = target; _driver.lookAtTarget = true; }
                if (k >= 1f) _stage = 2;
                break;
            }

            case 2:
                // Let the stride finish to the point where the feet are together, then hold.
                if (_anim != null && _anim.speed > 0.7f) _anim.speed = 0.7f;   // ease the last step
                if (_anim == null || _stopPhase < 0f || ReachedStopPhase())
                {
                    if (_anim != null) _anim.speed = 0f;
                    _stage = 3;
                }
                break;
        }
    }

    void Finish()
    {
        if (_anim != null) _anim.speed = 1f;
        _stage = 7;
        enabled = false;
    }

    void LateUpdate()
    {
        // Find the feet-together phase once, before the legs need it. Sampling poses the model
        // for a moment; the Animator writes over it again next frame.
        if ((_stage == 1 || _stage == 4) && _stopPhase < 0f && _clip != null && _lFoot != null && _rFoot != null)
        {
            float best = float.MaxValue;
            const int N = 40;
            for (int i = 0; i < N; i++)
            {
                float ph = i / (float)N;
                _clip.SampleAnimation(_anim.gameObject, ph * _clip.length);
                Vector3 d = _anim.transform.InverseTransformVector(_lFoot.position - _rFoot.position);
                float h = new Vector2(d.x, d.z).magnitude;
                if (h < best) { best = h; _stopPhase = ph; }
            }
            // Put this frame's pose back so nothing pops on screen.
            var info = _anim.GetCurrentAnimatorStateInfo(0);
            _clip.SampleAnimation(_anim.gameObject, Mathf.Repeat(info.normalizedTime, 1f) * _clip.length);
        }
    }

    bool ReachedStopPhase()
    {
        var info = _anim.GetCurrentAnimatorStateInfo(0);
        float ph = Mathf.Repeat(info.normalizedTime, 1f);
        float step = Time.deltaTime * Mathf.Max(0.1f, _anim.speed) / Mathf.Max(0.01f, info.length);
        float diff = Mathf.Repeat(ph - _stopPhase, 1f);
        return diff <= step * 1.5f || diff >= 1f - 0.02f;
    }
}
