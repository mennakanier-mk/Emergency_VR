using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The boy from the moment the paramedic picks him up: carried in his arms, laid on the
/// stretcher, and wheeled into the ambulance on it.
///
/// His own clip ends lying on the road, and holding that stiff pose in the paramedic's arms
/// looks like carrying a plank. So when he is lifted his Animator is switched off and his pose
/// is BUILT every frame instead:
///   - in the arms: cradle carry - back on one forearm, knees over the other, hips sagging
///     between them, head rolled back, outer arm and lower legs hanging limp. The hanging parts
///     swing with the paramedic's steps (a damped spring on "down" that the carry's own
///     acceleration pushes around), so he moves like a body, not a statue;
///   - on the stretcher: flat on his back along it, arms at his sides, legs straight, riding
///     with the stretcher wherever it goes.
/// The changes blend: road pose -> arms over 0.45 s, arms -> stretcher while he is put down.
///
/// All bone maths is done in the boy's root space (RootSpace), because accident_scene is
/// mirrored with a negative scale.
/// </summary>
[DefaultExecutionOrder(1200)]
public class BoyCarry : MonoBehaviour
{
    public Animator rigAnimator;          // the paramedic rig (em)
    public Transform paramedicHips, leftHand, rightHand;
    public Transform stretcherTop;
    [Tooltip("The paramedic's bag and mask: once they are put on the stretcher they are slid down to " +
             "the foot end, clear of the boy's legs.")]
    public Transform kit, mask;
    [Tooltip("Clip second after which the bag is on the stretcher.")]
    public float kitOnStretcherTime = 51.5f;

    [Header("Timing (paramedic clip seconds)")]
    public float grabTime = 43.30f;
    public float grabBlend = 0.45f;
    public float releaseStart = 46.55f;
    public float releaseEnd = 47.15f;

    [Header("Feel")]
    [Tooltip("How far his hips sag below the paramedic's arms, in body heights.")]
    public float sag = 0.10f;
    [Tooltip("How much the hanging head and limbs swing with the carry.")]
    public float swing = 0.6f;
    public float springStiffness = 55f, springDamping = 8f;

    Transform _root, _hips;
    readonly List<Transform> _bones = new List<Transform>();
    readonly Dictionary<Transform, int> _index = new Dictionary<Transform, int>();
    Quaternion[] _rest;                    // root-space rest (bind) rotations
    Vector3 _restUp, _restFront;
    float _H, _headToHips;

    Transform _spine2, _neck, _head, _headTop;
    Transform[] _arm = new Transform[2], _fore = new Transform[2], _hand = new Transform[2], _sh = new Transform[2];
    Transform[] _upLeg = new Transform[2], _leg = new Transform[2], _foot = new Transform[2];

    bool _grabbed, _releaseChosen;
    Quaternion[] _road, _a, _b, _out;
    Vector3 _roadHips, _aHips, _bHips;
    Transform _headHand, _kneeHand;
    float _headEndSign = 1f;

    Vector3 _hang = Vector3.down, _hangVel, _prevHips, _prevVel, _acc;
    bool _havePrev;

    public bool Active => _grabbed;

    void Start()
    {
        _root = transform;
        _hips = RootSpace.Find(_root, "hips");
        var smr = GetComponentInChildren<SkinnedMeshRenderer>();
        if (_hips == null || smr == null || rigAnimator == null) { enabled = false; return; }
        foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.updateWhenOffscreen = true;

        foreach (var t in _hips.GetComponentsInChildren<Transform>(true)) { _index[t] = _bones.Count; _bones.Add(t); }

        _spine2 = RootSpace.Find(_root, "spine2");
        _neck = RootSpace.Find(_root, "neck");
        _head = RootSpace.Find(_root, ":head");
        _headTop = RootSpace.Find(_root, "headtop_end");
        string[] s = { "left", "right" };
        for (int i = 0; i < 2; i++)
        {
            _sh[i] = RootSpace.Find(_root, s[i] + "shoulder");
            _arm[i] = RootSpace.Find(_root, s[i] + "arm");
            _fore[i] = RootSpace.Find(_root, s[i] + "forearm");
            _hand[i] = RootSpace.Find(_root, s[i] + "hand");
            _upLeg[i] = RootSpace.Find(_root, s[i] + "upleg");
            _leg[i] = RootSpace.Find(_root, s[i] + "leg");
            _foot[i] = RootSpace.Find(_root, s[i] + "foot");
        }

        BuildRest(smr);

        int n = _bones.Count;
        _road = new Quaternion[n]; _a = new Quaternion[n]; _b = new Quaternion[n]; _out = new Quaternion[n];
    }

    /// <summary>The bind pose from the skinned mesh, in root space.</summary>
    void BuildRest(SkinnedMeshRenderer smr)
    {
        _rest = new Quaternion[_bones.Count];
        var restPos = new Vector3[_bones.Count];
        Matrix4x4 meshToRoot = _root.worldToLocalMatrix * smr.transform.localToWorldMatrix;
        var bp = smr.sharedMesh.bindposes;
        var sb = smr.bones;

        for (int i = 0; i < _bones.Count; i++) { _rest[i] = RootSpace.Rot(_root, _bones[i]); restPos[i] = RootSpace.Pos(_root, _bones[i]); }
        for (int k = 0; k < sb.Length && k < bp.Length; k++)
        {
            if (sb[k] == null || !_index.TryGetValue(sb[k], out int i)) continue;
            Matrix4x4 m = meshToRoot * bp[k].inverse;
            _rest[i] = m.rotation;
            restPos[i] = m.GetColumn(3);
        }
        // Bones that are not skinned (end bones) keep their offset from the parent: derive them.
        for (int i = 0; i < _bones.Count; i++)
        {
            var t = _bones[i];
            bool skinned = System.Array.IndexOf(sb, t) >= 0;
            if (skinned || t.parent == null || !_index.TryGetValue(t.parent, out int p)) continue;
            _rest[i] = _rest[p] * t.localRotation;
            restPos[i] = restPos[p] + _rest[p] * Vector3.Scale(t.localPosition, Vector3.one);
        }

        Vector3 P(Transform t) => t != null && _index.TryGetValue(t, out int i) ? restPos[i] : Vector3.zero;
        _restUp = (P(_neck) - P(_hips)).normalized;
        Vector3 right = P(_upLeg[1]) - P(_upLeg[0]);
        right = (right - _restUp * Vector3.Dot(right, _restUp)).normalized;
        _restFront = Vector3.Cross(right, _restUp).normalized;

        float rootScale = Mathf.Abs(_root.lossyScale.y);
        float tall = (P(_headTop) - P(_foot[0])).magnitude;
        _H = Mathf.Max(0.5f, tall * rootScale * 1.08f);
        _headToHips = (P(_headTop) - P(_hips)).magnitude * rootScale;
    }

    float ClipTime
    {
        get
        {
            var st = rigAnimator.GetCurrentAnimatorStateInfo(0);
            return Mathf.Min(st.normalizedTime, 1f) * st.length;
        }
    }

    void LateUpdate()
    {
        if (rigAnimator == null || !rigAnimator.isActiveAndEnabled) return;
        float t = ClipTime;
        if (!_grabbed)
        {
            if (rigAnimator.speed <= 0f || t < grabTime) return;
            Grab();
        }

        float wGrab = RootSpace.Smooth01((t - grabTime) / Mathf.Max(0.01f, grabBlend));
        float wRel = RootSpace.Smooth01((t - releaseStart) / Mathf.Max(0.01f, releaseEnd - releaseStart));

        if (!_releaseChosen && t >= releaseStart) ChooseHeadEnd();

        int n = _bones.Count;
        if (wRel < 1f) { Cradle(); Store(_a, out _aHips); }
        if (wRel > 0f && stretcherTop != null) { OnStretcher(); Store(_b, out _bHips); }

        Vector3 hips;
        for (int i = 0; i < n; i++)
        {
            Quaternion q = Quaternion.Slerp(_road[i], _a[i], wGrab);
            if (wRel > 0f) q = Quaternion.Slerp(q, _b[i], wRel);
            _out[i] = q;
        }
        hips = Vector3.Lerp(_roadHips, _aHips, wGrab);
        if (wRel > 0f) hips = Vector3.Lerp(hips, _bHips, wRel);

        Apply(_out, hips);

        if (t >= kitOnStretcherTime && wRel >= 1f) KeepKitOffLegs();
    }

    float _kitShift;

    /// <summary>The clip drops the bag where his legs are; slide it to the foot end beyond his feet.</summary>
    void KeepKitOffLegs()
    {
        if (kit == null || stretcherTop == null) return;
        StretcherFrame(out Vector3 c, out Vector3 a, out Vector3 n, out float halfLen);
        a *= _headEndSign;

        float feet = float.MaxValue;
        foreach (var f in _foot) if (f != null) feet = Mathf.Min(feet, Vector3.Dot(f.position - c, a));
        if (feet == float.MaxValue) return;

        Vector3 k = kit.position - c;
        bool onStretcher = Vector3.Dot(k, n) > -0.3f * _H && Mathf.Abs(Vector3.Dot(k, a)) < halfLen + 0.3f;
        float kh = 0.25f * _H;
        var kr = kit.GetComponent<Renderer>();
        if (kr != null) { Vector3 e = kr.bounds.extents; kh = Mathf.Abs(a.x) * e.x + Mathf.Abs(a.y) * e.y + Mathf.Abs(a.z) * e.z; }

        float want = onStretcher ? Mathf.Max(0f, Vector3.Dot(k, a) - (feet - kh - 0.04f * _H)) : 0f;
        _kitShift = Mathf.MoveTowards(_kitShift, want, Time.deltaTime * 2f);
        if (_kitShift <= 0f) return;

        Vector3 move = -a * _kitShift;
        if (mask != null && (mask.position - kit.position).magnitude < 0.5f * _H) mask.position += move;
        kit.position += move;
    }

    void Grab()
    {
        _grabbed = true;
        Store(_road, out _roadHips);
        _a = (Quaternion[])_road.Clone(); _aHips = _roadHips;
        _b = (Quaternion[])_road.Clone(); _bHips = _roadHips;

        // His own systems stop here; the pose is ours from now on.
        var anim = GetComponent<Animator>(); if (anim != null) anim.enabled = false;
        var run = GetComponent<RunExtender>(); if (run != null) run.enabled = false;
        var gf = GetComponent<GroundFollowAnimated>(); if (gf != null) gf.enabled = false;

        Vector3 headPos = _head != null ? _head.position : _hips.position;
        bool leftIsHead = (leftHand.position - headPos).sqrMagnitude < (rightHand.position - headPos).sqrMagnitude;
        _headHand = leftIsHead ? leftHand : rightHand;
        _kneeHand = leftIsHead ? rightHand : leftHand;
        _havePrev = false;
    }

    void ChooseHeadEnd()
    {
        _releaseChosen = true;
        if (stretcherTop == null) return;
        StretcherFrame(out Vector3 c, out Vector3 a, out _, out _);
        Vector3 head = _head != null ? _head.position : _hips.position;
        _headEndSign = Vector3.Dot(head - c, a) >= 0f ? 1f : -1f;
    }

    // ------------------------------------------------------------------ poses

    void Cradle()
    {
        Vector3 up = Vector3.up;
        Vector3 A = _headHand.position + up * 0.03f * _H;
        Vector3 B = _kneeHand.position + up * 0.03f * _H;
        Vector3 mid = (A + B) * 0.5f;
        Vector3 carrierFwd = mid - paramedicHips.position; carrierFwd.y = 0f;
        carrierFwd = carrierFwd.sqrMagnitude > 1e-4f ? carrierFwd.normalized : Vector3.forward;

        Vector3 hipsT = Vector3.Lerp(A, B, 0.62f) - up * sag * _H + carrierFwd * 0.03f * _H;
        Vector3 D = (A + up * 0.10f * _H - hipsT).normalized;          // up his body
        Vector3 F = (up - carrierFwd * 0.5f);                            // chest up, turned to the carrier
        F = (F - D * Vector3.Dot(F, D)).normalized;

        UpdateHang(hipsT);

        Rigid(D, F, hipsT);

        Vector3 across = (B - A).normalized;
        Aim(_neck, _head, Vector3.Slerp(D, _hang, 0.35f));
        Aim(_head, _headTop, Vector3.Slerp(D, _hang, 0.60f));

        for (int i = 0; i < 2; i++)
        {
            Aim(_upLeg[i], _leg[i], (B + up * 0.05f * _H - _upLeg[i].position).normalized);
            Aim(_leg[i], _foot[i], (_hang * 0.9f + across * 0.25f).normalized);
        }

        // Arms: the one away from the paramedic hangs; the one against him rests on the belly.
        for (int i = 0; i < 2; i++)
        {
            if (_arm[i] == null) continue;
            bool inner = Vector3.Dot(_arm[i].position - hipsT, carrierFwd) < Vector3.Dot(_arm[1 - i].position - hipsT, carrierFwd);
            if (inner)
            {
                Aim(_arm[i], _fore[i], (-D * 0.7f + F * 0.35f - carrierFwd * 0.15f).normalized);
                Aim(_fore[i], _hand[i], (carrierFwd * 0.8f + F * 0.3f - D * 0.1f).normalized);
            }
            else
            {
                Aim(_arm[i], _fore[i], (_hang * 0.85f - D * 0.15f + carrierFwd * 0.2f).normalized);
                Aim(_fore[i], _hand[i], (_hang + F * 0.12f).normalized);
            }
        }
    }

    void OnStretcher()
    {
        StretcherFrame(out Vector3 c, out Vector3 a, out Vector3 n, out float halfLen);
        a *= _headEndSign;
        Vector3 surface = c + n * StretcherHalfThickness();
        Vector3 headEnd = surface + a * halfLen;
        Vector3 hipsT = headEnd - a * (0.05f * _H + _headToHips) + n * 0.06f * _H;

        Rigid(a, n, hipsT);

        Aim(_neck, _head, a);
        Aim(_head, _headTop, a);
        for (int i = 0; i < 2; i++)
        {
            Vector3 lat = Lateral(_arm[i] != null ? _arm[i].position : hipsT, hipsT, a, n);
            Aim(_arm[i], _fore[i], (-a + lat * 0.18f).normalized);
            Aim(_fore[i], _hand[i], (-a + lat * 0.06f).normalized);
            Vector3 latL = Lateral(_upLeg[i] != null ? _upLeg[i].position : hipsT, hipsT, a, n);
            Aim(_upLeg[i], _leg[i], (-a + latL * 0.04f).normalized);
            Aim(_leg[i], _foot[i], -a);
        }
    }

    static Vector3 Lateral(Vector3 p, Vector3 centre, Vector3 a, Vector3 n)
    {
        Vector3 d = p - centre;
        d -= a * Vector3.Dot(d, a) + n * Vector3.Dot(d, n);
        return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.zero;
    }

    void StretcherFrame(out Vector3 centre, out Vector3 along, out Vector3 normal, out float halfLen)
    {
        var mf = stretcherTop.GetComponent<MeshFilter>();
        Bounds b = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
        Vector3 e = b.extents;
        int la = e.x >= e.y && e.x >= e.z ? 0 : (e.y >= e.z ? 1 : 2);
        int ln = e.x <= e.y && e.x <= e.z ? 0 : (e.y <= e.z ? 1 : 2);
        Vector3 axA = Vector3.zero; axA[la] = e[la];
        Vector3 axN = Vector3.zero; axN[ln] = e[ln];
        centre = stretcherTop.TransformPoint(b.center);
        Vector3 wa = stretcherTop.TransformVector(axA);
        Vector3 wn = stretcherTop.TransformVector(axN);
        halfLen = wa.magnitude;
        along = wa.normalized;
        normal = wn.normalized; if (normal.y < 0f) normal = -normal;
        _halfThick = wn.magnitude;
    }

    float _halfThick;
    float StretcherHalfThickness() => _halfThick;

    void UpdateHang(Vector3 hipsT)
    {
        float dt = Mathf.Max(1e-4f, Time.deltaTime);
        if (!_havePrev) { _prevHips = hipsT; _prevVel = Vector3.zero; _havePrev = true; }
        Vector3 vel = (hipsT - _prevHips) / dt;
        Vector3 acc = (vel - _prevVel) / dt;
        _prevHips = hipsT; _prevVel = vel;
        _acc = Vector3.Lerp(_acc, acc, 1f - Mathf.Exp(-8f * dt));

        float g = 9.81f * Mathf.Max(1f, _H / 1.3f);                      // gravity at this world scale
        Vector3 target = (Vector3.down * g - _acc * swing).normalized;
        _hangVel += (target - _hang) * springStiffness * dt - _hangVel * springDamping * dt;
        _hang = (_hang + _hangVel * dt).normalized;
    }

    // ------------------------------------------------------------------ helpers

    void Rigid(Vector3 upW, Vector3 frontW, Vector3 hipsW)
    {
        Vector3 up = RootSpace.Dir(_root, upW), front = RootSpace.Dir(_root, frontW);
        Quaternion q = Quaternion.LookRotation(front, up) * Quaternion.Inverse(Quaternion.LookRotation(_restFront, _restUp));
        for (int i = 0; i < _bones.Count; i++) _out[i] = q * _rest[i];
        Apply(_out, hipsW);
    }

    void Aim(Transform bone, Transform child, Vector3 dirW)
    {
        if (bone == null || child == null) return;
        RootSpace.Aim(_root, bone, child, RootSpace.Dir(_root, dirW));
    }

    void Store(Quaternion[] into, out Vector3 hips)
    {
        for (int i = 0; i < _bones.Count; i++) into[i] = RootSpace.Rot(_root, _bones[i]);
        hips = _hips.position;
    }

    void Apply(Quaternion[] rs, Vector3 hipsW)
    {
        // Parent first (the list is in hierarchy order), each relative to the parent just written.
        Quaternion parentOfHips = _hips.parent == null || _hips.parent == _root ? Quaternion.identity : RootSpace.Rot(_root, _hips.parent);
        for (int i = 0; i < _bones.Count; i++)
        {
            var t = _bones[i];
            Quaternion parent = i == 0 ? parentOfHips
                              : (_index.TryGetValue(t.parent, out int p) ? rs[p] : RootSpace.Rot(_root, t.parent));
            t.localRotation = Quaternion.Inverse(parent) * rs[i];
        }
        _hips.position = hipsW;
    }
}
