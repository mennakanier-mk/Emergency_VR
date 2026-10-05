using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The ambulance's part of the accident:
///
///  1. From the start it drives as ordinary traffic in the accident's lane, some way behind the
///     car that hits the boy. After the impact the lane jams, and it ends up stuck in the queue.
///  2. Lights on. The other carriageway is closed beyond the accident (oncoming traffic stops
///     there), it waits until the stretch it needs is empty, then pulls out across the centre
///     and drives up the wrong side, against the traffic, past the crash.
///  3. It pulls in to a stop worked out so the paramedic's clip lands on the real boy: the point
///     where the clip does the chest compressions is put on the boy's chest. It may angle in
///     (rear towards the boy) to stay on its side and clear of the crashed car and its door.
///  4. Rear door opens, the paramedic clip (em.fbx) plays: out, stretcher out, first aid, lift,
///     stretcher, back in. Bystanders standing where he has to work step aside first.
///  5. At the end of the clip the door closes.
///
/// The rig (the child holding Armature + STR_ROOT + KIT) is re-seated at runtime: sized like the
/// other adults, the stretcher inside against the rear doors, mirrored when the boy is on the
/// other side. The clip assumes a lower floor than this ambulance has, so whatever is inside the
/// vehicle (him, the stretcher, the bag) is lifted to the real floor and lowered as it comes out.
///
/// Added automatically to the RoadDriver vehicle that has a child named STR_ROOT.
/// </summary>
[DefaultExecutionOrder(60)]       // after AccidentCar (40) and RoadDriver (50)
public class AmbulanceResponse : MonoBehaviour
{
    public Transform rig;
    public Transform victim;
    public AccidentCar accidentCar;

    [Header("Before the accident")]
    [Tooltip("How far behind the accident car it starts, metres along the lane.")]
    public float startBehind = 30f;
    [Tooltip("Normal driving speed, m/s (0 = keep the RoadDriver's).")]
    public float trafficSpeed = 0f;

    [Header("Going round the jam")]
    [Tooltip("Seconds after the impact before it may pull out.")]
    public float minWaitAfterImpact = 2.5f;
    [Tooltip("Pull out once it has stood still this long, or is this close to the crash.")]
    public float stuckTime = 0.8f, closeToCrash = 28f;
    [Tooltip("Speed on the wrong side, m/s.")]
    public float wrongWaySpeed = 8f;
    public float accel = 3f, decel = 2.6f;
    [Tooltip("How quickly the acceleration itself may change, m/s^3 - lower is gentler.")]
    public float jerk = 3f;
    [Tooltip("Metres ahead/behind used to point the vehicle along its route - longer turns more smoothly.")]
    public float steerSpan = 2.2f;
    [Tooltip("Metres it takes to cross onto the other side.")]
    public float mergeLength = 22f;
    [Tooltip("How far past its stop the oncoming traffic is held, metres.")]
    public float closeAhead = 14f;

    [Header("Leaving")]
    [Tooltip("Drive on once the patient is in and the doors are shut.")]
    public bool leaveAfterwards = true;
    [Tooltip("Seconds after the doors shut before it pulls away.")]
    public float leaveDelay = 1.0f;
    [Tooltip("Where it rejoins its own lane, metres past the boy.")]
    public float rejoinPast = 22f;
    [Tooltip("Speed while pulling back onto its own side, m/s.")]
    public float leaveSpeed = 7f;

    [Header("Where it stops")]
    [Tooltip("Largest angle it may stop at relative to the road, degrees.")]
    public float maxAngle = 35f;
    [Tooltip("How far its centre may drift from the other side's lane line before that costs, metres.")]
    public float maxLaneOffset = 1.5f;

    [Header("Paramedic")]
    [Tooltip("Where on the boy the CPR goes: 0 = hips, 0.5 = stomach, 1 = chest. The ambulance " +
             "stops so the paramedic kneels there.")]
    [Range(0f, 1f)] public float cprSpot = 0.15f;
    [Tooltip("World height of the paramedic, to match the other adults (Scale Consistency uses 2.97).")]
    public float paramedicHeight = 2.97f;

    [Header("Door and lights")]
    public float doorOpenAngle = 105f;
    public float doorTime = 1.2f;
    public float startDelay = 0.4f;
    public bool flashLights = true;

    public bool log = true;

    // em.fbx local space (rig metres), measured from the clip.
    static readonly Vector3 kStretcherIn = new Vector3(2.72f, 0.63f, 2.40f);
    static readonly Vector3 kStretcherGround = new Vector3(2.72f, 0f, 5.00f);
    static readonly Vector3 kKneel = new Vector3(1.00f, 0f, 5.60f);
    // Where em.fbx puts the armature object inside the rig (Blender (1.658,0,0), rot X 90 -> Unity).
    // The clip does not animate this object, so a hand edit in the scene would stay and leave the
    // paramedic metres away from his own stretcher.
    static readonly Vector3 kArmatureLocal = new Vector3(-1.658f, 0f, 0f);
    const float kStretcherLen = 2.11f, kStretcherWidth = 0.62f, kFloor = 0.63f, kRigHeight = 1.756f;

    enum State { Init, Traffic, Waiting, WrongWay, Parked, Working, Closing, Done, Leaving, Gone }
    State _state = State.Init;

    RoadDriver _driver, _accDriver;
    Animator _rigAnim;
    int _rigState;
    float _clipLen = 64f;
    Transform _interior, _hinge, _armature, _strRoot, _kit, _mask, _rigHips;
    Light[] _lights;
    Vector3 _fLocal;
    float _halfLen = 4.7f, _halfWid = 2.3f, _bodyAlong, _bodyLat;
    float _floorAboveRoad = 1.2f, _rearAlong, _floorLift, _s = 1.7f;
    bool _mirror, _rigPlaced;

    float _impactAt = -1f, _stateAt, _stillFor;
    TrafficPath _lane; Vector3 _laneDir, _laneOrigin, _travel;
    Vector3 _stopPos, _stopFwd;

    readonly List<Vector3> _route = new List<Vector3>();
    readonly List<float> _routeLen = new List<float>();
    float _arc, _v, _a, _yOffset, _yNow;
    bool _routeParks = true;
    float _routeEndSpeed, _rejoinD, _routeTop;
    TrafficPath _ownPath;

    /// <summary>Doors shut with the patient inside.</summary>
    public bool PatientLoaded => _state == State.Done || _state == State.Leaving || _state == State.Gone;
    /// <summary>Back in its own lane and driving on.</summary>
    public bool HasLeft => _state == State.Gone;
    float _dMerge, _dBlock;

    Vector3 _hingeLocalPos; Quaternion _hingeLocalRot; float _doorSign = 1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoInstall()
    {
        if (FindFirstObjectByType<AmbulanceResponse>() != null) return;
        foreach (var d in FindObjectsByType<RoadDriver>(FindObjectsSortMode.None))
            foreach (var t in d.GetComponentsInChildren<Transform>(true))
                if (t.name == "STR_ROOT") { d.gameObject.AddComponent<AmbulanceResponse>(); return; }
    }

    // ================================================================ setup

    void Start()
    {
        _driver = GetComponent<RoadDriver>();
        if (rig == null)
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.Find("STR_ROOT") != null) { rig = t; break; }
        if (accidentCar == null) accidentCar = FindFirstObjectByType<AccidentCar>();
        if (_driver == null || rig == null || accidentCar == null)
        {
            Debug.LogWarning($"[Ambulance] '{name}': needs a RoadDriver, a rig child and an AccidentCar in the scene.", this);
            enabled = false; return;
        }
        _accDriver = accidentCar.GetComponent<RoadDriver>();
        if (victim == null) victim = accidentCar.victim;
        if (victim == null) { var r = FindFirstObjectByType<RunExtender>(); if (r != null) victim = r.transform; }
        accidentCar.Impact += _ => { if (_impactAt < 0f) _impactAt = Time.time; };

        _rigAnim = rig.GetComponent<Animator>();
        // He walks metres away from where the mesh's bounds were baked: without this Unity culls
        // him as "off screen" while he is standing in plain view.
        foreach (var smr in rig.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.updateWhenOffscreen = true;
        if (_rigAnim != null) _rigAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        foreach (var t in rig.GetComponentsInChildren<Transform>(true))
        {
            if (_armature == null && t.name.StartsWith("Armature")) _armature = t;
            if (t.name == "STR_ROOT") _strRoot = t;
            if (t.name == "KIT") _kit = t;
            if (t.name == "FA_mask") _mask = t;
        }
        _rigHips = RootSpace.Find(rig, "hips");
        OxygenMaskModel.Apply(_mask);
        if (_armature != null && (_armature.localPosition - kArmatureLocal).sqrMagnitude > 1e-4f)
        {
            if (log) Debug.Log($"[Ambulance] '{_armature.name}' had been moved inside the rig ({_armature.localPosition:F2}) - put back to {kArmatureLocal:F2}.", this);
            _armature.localPosition = kArmatureLocal;
            _armature.localRotation = Quaternion.identity;
        }
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToUpperInvariant();
            if (_interior == null && n.Contains("INTERIOR")) _interior = t;
            if (_hinge == null && n.Contains("HINGE")) _hinge = t;
        }
        _lights = GetComponentsInChildren<Light>(true);

        MeasureBody();
        if (_hinge != null) { _hingeLocalPos = _hinge.localPosition; _hingeLocalRot = _hinge.localRotation; PickDoorSign(); }

        // The paramedic waits seated: first frame of his clip, frozen.
        if (_rigAnim != null)
        {
            _rigState = _rigAnim.GetCurrentAnimatorStateInfo(0).fullPathHash;
            _rigAnim.Play(_rigState, 0, 0f);
            _rigAnim.speed = 0f;
            var clips = _rigAnim.runtimeAnimatorController != null ? _rigAnim.runtimeAnimatorController.animationClips : null;
            if (clips != null && clips.Length > 0) _clipLen = clips[0].length;
        }
        PlaceRig(false);
        SetLights(false);
    }

    /// <summary>
    /// Edit mode: seat the rig exactly where Play will put it (inside, sized, first frame of the
    /// clip), so nothing jumps when Play starts. Called by AmbulanceEditorSetup.
    /// </summary>
    public void PlaceRigInEditor()
    {
        _driver = GetComponent<RoadDriver>();
        if (rig == null)
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.Find("STR_ROOT") != null) { rig = t; break; }
        if (rig == null) return;

        foreach (var t in rig.GetComponentsInChildren<Transform>(true))
        {
            if (_armature == null && t.name.StartsWith("Armature")) _armature = t;
            if (t.name == "STR_ROOT") _strRoot = t;
            if (t.name == "KIT") _kit = t;
            if (t.name == "FA_mask") _mask = t;
        }
        _interior = null; _hinge = null;
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToUpperInvariant();
            if (_interior == null && n.Contains("INTERIOR")) _interior = t;
            if (_hinge == null && n.Contains("HINGE")) _hinge = t;
        }
        if (_armature != null) { _armature.localPosition = kArmatureLocal; _armature.localRotation = Quaternion.identity; }

        var anim = rig.GetComponent<Animator>();
        var clips = anim != null && anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.animationClips : null;
        if (clips != null && clips.Length > 0) clips[0].SampleAnimation(rig.gameObject, 0f);

        Physics.SyncTransforms();
        MeasureBody();
        PlaceRig(false);
    }

    Vector3 Fwd => Flat(transform.TransformDirection(_fLocal)).normalized;
    Vector3 Right => Vector3.Cross(Vector3.up, Fwd);
    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    void MeasureBody()
    {
        Bounds body = default; bool any = false;
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            if (r.transform.IsChildOf(rig) || (_interior != null && r.transform.IsChildOf(_interior))) continue;
            if (r is ParticleSystemRenderer) continue;
            if (!any) { body = r.bounds; any = true; } else body.Encapsulate(r.bounds);
        }
        Vector3 f = _interior != null && any ? Flat(body.center - InteriorWorldBounds().center) : Vector3.zero;
        if (f.sqrMagnitude < 0.01f)
        {
            Vector3 d = Vector3.forward;
            if (_driver.path != null) _driver.path.Sample(_driver.Distance, out _, out d);
            f = Flat(d);
        }
        f.Normalize();
        _fLocal = transform.InverseTransformDirection(f);

        Vector3 r2 = Vector3.Cross(Vector3.up, f), e = body.extents;
        _halfLen = Mathf.Abs(f.x) * e.x + Mathf.Abs(f.z) * e.z;
        _halfWid = Mathf.Abs(r2.x) * e.x + Mathf.Abs(r2.z) * e.z;
        _bodyAlong = Vector3.Dot(body.center - transform.position, f);
        _bodyLat = Vector3.Dot(body.center - transform.position, r2);

        float floor = _interior != null ? InteriorWorldBounds().min.y : transform.position.y + 1.2f;
        _floorAboveRoad = Mathf.Max(0.3f, floor - Ground(transform.position));
    }

    Bounds InteriorWorldBounds()
    {
        var mf = _interior.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            var r = _interior.GetComponent<Renderer>();
            return r != null ? r.bounds : new Bounds(_interior.position, Vector3.one);
        }
        Bounds lb = mf.sharedMesh.bounds, wb = default;
        for (int i = 0; i < 8; i++)
        {
            Vector3 w = _interior.TransformPoint(Corner(lb, i));
            if (i == 0) wb = new Bounds(w, Vector3.zero); else wb.Encapsulate(w);
        }
        return wb;
    }

    static Vector3 Corner(Bounds b, int i) =>
        b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));

    /// <summary>
    /// Seat the rig: sized like the other adults, its ground on the road, stretcher inside against
    /// the rear doors on one side, the paramedic's seat on the other. Mirrored when asked.
    /// </summary>
    void PlaceRig(bool mirror)
    {
        _mirror = mirror;
        _s = paramedicHeight / kRigHeight;
        float s = _s, m = mirror ? -1f : 1f;
        Vector3 F = Fwd, R = Right, p0 = transform.position;

        float rearA = -_halfLen + _bodyAlong + 0.3f, latMin = -0.8f, latMax = 0.8f, floorY = p0.y + 1.2f;
        if (_interior != null)
        {
            var mf = _interior.GetComponent<MeshFilter>();
            Bounds lb = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            rearA = float.MaxValue; latMin = float.MaxValue; latMax = float.MinValue; floorY = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 w = _interior.TransformPoint(Corner(lb, i));
                rearA = Mathf.Min(rearA, Vector3.Dot(w - p0, F));
                float l = Vector3.Dot(w - p0, R);
                latMin = Mathf.Min(latMin, l); latMax = Mathf.Max(latMax, l);
                floorY = Mathf.Min(floorY, w.y);
            }
        }
        _rearAlong = rearA;

        // Seat ends up on the -m side of the stretcher, so the stretcher goes on the +m side.
        float half = (latMax - latMin) * 0.5f, latC = (latMin + latMax) * 0.5f;
        float lat = latC + m * Mathf.Max(0f, half - kStretcherWidth * 0.5f * s - 0.03f);
        float along = rearA + (kStretcherLen * 0.5f + 0.06f) * s;

        // The rig's own ground sits on the road; the floor difference is made up per object.
        float roadY = floorY - _floorAboveRoad;
        _floorLift = Mathf.Max(0f, _floorAboveRoad - kFloor * s);

        Quaternion rot = Quaternion.LookRotation(-F, Vector3.up);
        Vector3 target = p0 + F * along + R * lat; target.y = roadY + kStretcherIn.y * s;
        Vector3 local = new Vector3(m * kStretcherIn.x * s, kStretcherIn.y * s, kStretcherIn.z * s);

        Vector3 pl = rig.parent != null ? rig.parent.lossyScale : Vector3.one;
        rig.rotation = rot;
        rig.localScale = new Vector3(m * s / Mathf.Abs(pl.x), s / Mathf.Abs(pl.y), s / Mathf.Abs(pl.z));
        rig.position = target - rot * local;
        _rigPlaced = true;
    }

    // ================================================================ update

    void Update()
    {
        switch (_state)
        {
            case State.Init: JoinTraffic(); break;

            case State.Traffic:
                if (_impactAt >= 0f)
                {
                    SetLights(true);
                    _stillFor = _driver.CurrentSpeed < 0.4f ? _stillFor + Time.deltaTime : 0f;
                    float toCrash = Flat(accidentCar.transform.position - transform.position).magnitude;
                    if (Time.time - _impactAt > minWaitAfterImpact && (_stillFor > stuckTime || toCrash < closeToCrash))
                        PrepareWrongWay();
                }
                break;

            case State.Waiting:
                if (StretchClear() || Time.time - _stateAt > 12f) BeginWrongWay();
                break;

            case State.WrongWay: DriveRoute(); break;

            case State.Parked:
            {
                float k = Mathf.Clamp01((Time.time - _stateAt) / doorTime);
                SetDoor(RootSpace.Smooth01(k));
                if (Time.time - _stateAt >= doorTime + startDelay) StartWork();
                break;
            }

            case State.Working:
                if (_rigAnim.GetCurrentAnimatorStateInfo(0).normalizedTime * _clipLen >= _clipLen - 0.05f)
                {
                    _rigAnim.speed = 0f;
                    _state = State.Closing; _stateAt = Time.time;
                }
                break;

            case State.Closing:
            {
                float k = Mathf.Clamp01((Time.time - _stateAt) / doorTime);
                SetDoor(1f - RootSpace.Smooth01(k));
                if (k >= 1f)
                {
                    _state = State.Done; _stateAt = Time.time;
                    if (log) Debug.Log("[Ambulance] doors shut - patient on board.", this);
                }
                break;
            }

            case State.Done:
                if (leaveAfterwards && _lane != null && Time.time - _stateAt >= leaveDelay) TryLeave();
                break;

            case State.Leaving: DriveRoute(); break;
        }

        if (flashLights && _impactAt >= 0f && _lights != null)
        {
            bool phase = Mathf.Repeat(Time.time * 3f, 1f) < 0.5f;
            for (int i = 0; i < _lights.Length; i++) _lights[i].enabled = (i % 2 == 0) == phase;
        }
    }

    /// <summary>
    /// The clip's floor is lower than this ambulance's. Anything still inside the vehicle (him,
    /// the stretcher, the bag, the mask) is lifted to the real floor, and comes down to the clip's
    /// height over the first metre outside the rear doors.
    /// </summary>
    void LateUpdate()
    {
        if (!_rigPlaced || _floorLift <= 0.001f) return;
        Lift(_rigHips);          // the hips are animated every frame; the armature object is not
        Lift(_strRoot);
        Lift(_kit);
        Lift(_mask);
    }

    readonly Dictionary<Transform, Vector3> _liftWritten = new Dictionary<Transform, Vector3>();
    readonly Dictionary<Transform, float> _liftOffset = new Dictionary<Transform, float>();

    void Lift(Transform t)
    {
        if (t == null) return;
        // If the Animator did not rewrite it this frame, our last offset is still in there: take it
        // out first, so it never adds up.
        // (Compared in local space, so the vehicle driving along does not look like a rewrite.)
        Vector3 cur = t.position;
        if (_liftWritten.TryGetValue(t, out Vector3 w) && (t.localPosition - w).sqrMagnitude < 1e-12f)
            cur -= Vector3.up * _liftOffset[t];

        float outside = _rearAlong - Vector3.Dot(cur - transform.position, Fwd);
        float off = _floorLift * (1f - RootSpace.Smooth01((outside + 0.3f) / 1.4f));
        t.position = cur + Vector3.up * off;
        _liftWritten[t] = t.localPosition;
        _liftOffset[t] = off;
    }

    // ================================================================ 1. traffic

    void JoinTraffic()
    {
        // AccidentCar places itself upstream in its first Update (it runs before this one).
        if (_accDriver == null || _accDriver.path == null) { _state = State.Traffic; return; }

        TrafficPath p = _accDriver.path;
        float start = StartBehind(p, _accDriver.Distance, false);

        // Placed there in the Editor already (see PlaceAtStartInEditor): stay exactly where it
        // stands, so nothing jumps when Play begins.
        if (_driver.path == p)
        {
            float here = _driver.Distance;
            float gap = Mathf.Abs(Mathf.DeltaAngle(here / p.Length * 360f, start / p.Length * 360f)) / 360f * p.Length;
            if (gap < 12f && !Occupied(p, here, 9f)) start = here;
            Debug.Log($"[Ambulance] join: car d={_accDriver.Distance:0.0}, wanted d={StartBehind(p, _accDriver.Distance, false):0.0}, standing at d={here:0.0} -> using {start:0.0}.", this);
        }

        if (_driver.path != p)
        {
            if (_driver.path != null) _driver.path.Unregister(_driver);
            _driver.path = p;
            p.Register(_driver);
        }
        _driver.Teleport(start);
        if (trafficSpeed > 0f) _driver.speed = trafficSpeed;
        _state = State.Traffic;
        if (log) Debug.Log($"[Ambulance] driving in traffic, {_accDriver.Distance - start:0} m behind '{accidentCar.name}'.", this);
    }

    /// <summary>Its starting distance: a little behind the accident car on the same straight.</summary>
    float StartBehind(TrafficPath p, float accDist, bool editorPositions)
    {
        p.Sample(accDist, out _, out Vector3 dir);
        Vector3 accDir = Flat(dir).normalized;
        float back = 0f;
        while (back < startBehind)
        {
            p.Sample(Mathf.Repeat(accDist - back - 1f, p.Length), out _, out Vector3 d);
            if (Vector3.Dot(Flat(d).normalized, accDir) < 0.95f) break;
            back += 1f;
        }
        float start = accDist - Mathf.Max(back, 10f);
        // Room for it if a car is already there - but never round the bend onto the other side.
        for (int k = 0; k < 6 && (editorPositions ? OccupiedInEditor(p, start, 14f) : Occupied(p, start, 14f)); k++)
        {
            p.Sample(Mathf.Repeat(start - 8f - 4f, p.Length), out _, out Vector3 d2);
            if (Vector3.Dot(Flat(d2).normalized, accDir) < 0.95f) break;
            start -= 8f;
        }
        return Mathf.Repeat(start, p.Length);
    }

    bool OccupiedInEditor(TrafficPath p, float d, float within)
    {
        var me = GetComponent<RoadDriver>();
        var acc = accidentCar != null ? accidentCar.GetComponent<RoadDriver>() : null;
        foreach (var o in FindObjectsByType<RoadDriver>(FindObjectsSortMode.None))
        {
            if (o == null || o == me || o.path != p || o is PedestrianWalker) continue;
            float od = o == acc ? accidentCar.PredictStartDistance() : o.NearestOnPath(o.transform.position);
            float a = Mathf.Repeat(od - d, p.Length);
            if (a < within || p.Length - a < within) return true;
        }
        return false;
    }

    /// <summary>
    /// Editor: put the ambulance where it will start driving when Play begins (behind the
    /// accident car, in its lane), instead of it jumping there at the first frame.
    /// </summary>
    public bool PlaceAtStartInEditor()
    {
        var drv = GetComponent<RoadDriver>();
        var acc = accidentCar != null ? accidentCar : FindFirstObjectByType<AccidentCar>();
        if (drv == null || acc == null) return false;
        accidentCar = acc;
        var accDrv = acc.GetComponent<RoadDriver>();
        if (accDrv == null || accDrv.path == null) return false;
        float accStart = acc.PredictStartDistance();
        if (accStart < 0f) return false;

        TrafficPath p = accDrv.path;
        Vector3 before = transform.position;
        drv.path = p;
        float start = StartBehind(p, accStart, true);
        drv.PlaceAtDistance(start);
        Debug.Log($"[Ambulance] editor start: accident car will start at d={accStart:0.0}, ambulance at d={start:0.0} -> {transform.position:F1}.", this);
        return (transform.position - before).sqrMagnitude > 0.0004f;
    }

    bool Occupied(TrafficPath p, float d, float within)
    {
        foreach (var o in p.Drivers)
        {
            if (o == null || o == _driver) continue;
            float a = Mathf.Repeat(o.Distance - d, p.Length);
            if (a < within || p.Length - a < within) return true;
        }
        return false;
    }

    // ================================================================ 2. round the jam

    void PrepareWrongWay()
    {
        Vector3 chest = VictimChest();
        if (!FindOppositeLane(chest)) { Debug.LogWarning("[Ambulance] no opposite carriageway found.", this); _state = State.Done; return; }
        _travel = -_laneDir;                      // up the wrong side = the way the jammed lane goes

        // Mirror the rig if the boy will be on the other side from where the clip expects.
        Vector3 boySideV = Vector3.Cross(Vector3.up, _travel);
        float boySide = Vector3.Dot(Flat(chest - _laneOrigin), boySideV);
        PlaceRig(false);
        float clipSide = Vector3.Dot(Flat(rig.TransformPoint(ParamedicRig.kChest) - transform.position), Right);
        if (Mathf.Sign(clipSide) != Mathf.Sign(boySide)) PlaceRig(true);

        SolveStop(chest);

        // Close the other carriageway a little past where it will stop.
        _dBlock = NearestOnLane(_stopPos + _stopFwd * (_halfLen + closeAhead));
        _lane.AddBlock(_dBlock);
        _dMerge = NearestOnLane(transform.position + _travel * mergeLength);

        _state = State.Waiting; _stateAt = Time.time;
        if (log) Debug.Log($"[Ambulance] stuck in the jam - closing '{_lane.name}' at {_dBlock:0} and waiting for it to clear. " +
                           $"Stop {_stopPos:F1} at {Vector3.SignedAngle(_travel, _stopFwd, Vector3.up):0} deg, rig mirrored={_mirror}.", this);
    }

    /// <summary>No oncoming car between the closure and just past where it merges.</summary>
    bool StretchClear()
    {
        float len = _lane.Length;
        float span = Mathf.Repeat(_dMerge + mergeLength + 10f - _dBlock, len);
        foreach (var o in _lane.Drivers)
        {
            if (o == null || o == _driver || o.freeRoam) continue;
            float a = Mathf.Repeat(o.Distance - _dBlock, len);
            if (a > 0.5f && a < span) return false;
        }
        return true;
    }

    void BeginWrongWay()
    {
        BuildRoute();
        SmoothRoute();
        if (_driver.path != null) _driver.path.Unregister(_driver);   // the queue closes up behind
        _driver.manualPose = true;
        _v = 0f; _a = 0f; _arc = 0f;
        _routeParks = true; _routeTop = wrongWaySpeed; _routeEndSpeed = 0f;
        _ownPath = _driver.path;
        _yOffset = transform.position.y - Ground(transform.position);
        _yNow = transform.position.y;
        _state = State.WrongWay;
        ClearWorkArea();
        if (log) Debug.Log($"[Ambulance] pulling out - {_routeLen[_routeLen.Count - 1]:0} m up the wrong side.", this);
    }

    /// <summary>Across onto the other side, along it against the traffic, then into the stop.</summary>
    void BuildRoute()
    {
        _route.Clear(); _routeLen.Clear();
        Vector3 p0 = Flat(transform.position), f0 = Fwd;
        Vector3 stop = Flat(_stopPos);

        float stopAlong = Vector3.Dot(stop - p0, _travel);
        float mergeAlong = Mathf.Min(mergeLength, stopAlong * 0.5f);
        float finalLen = Mathf.Min(18f, stopAlong * 0.4f);

        Vector3 M = LanePointAt(p0 + _travel * mergeAlong);
        Vector3 Q = LanePointAt(stop - _travel * finalLen);
        bool straight = Vector3.Dot(Q - M, _travel) > 2f;

        if (straight)
        {
            AddHermite(p0, f0, M, _travel, false);
            float dM = NearestOnLane(M), dQ = NearestOnLane(Q);
            float run = Mathf.Repeat(dM - dQ, _lane.Length);            // we travel against the lane's direction
            for (float k = 1f; k < run; k += 1f)
            {
                _lane.Sample(Mathf.Repeat(dM - k, _lane.Length), out Vector3 q, out _);
                Add(Flat(q));
            }
            AddHermite(Q, _travel, stop, _stopFwd, true);
        }
        else AddHermite(p0, f0, stop, _stopFwd, false);
    }

    Vector3 LanePointAt(Vector3 w)
    {
        _lane.Sample(NearestOnLane(w), out Vector3 p, out _);
        return Flat(p);
    }

    void AddHermite(Vector3 a, Vector3 fa, Vector3 b, Vector3 fb, bool skipFirst)
    {
        float d = (b - a).magnitude;
        Vector3 ta = fa * d * 0.6f, tb = fb * d * 0.6f;
        int n = Mathf.Max(8, Mathf.CeilToInt(d));
        for (int i = (skipFirst || _route.Count > 0) ? 1 : 0; i <= n; i++)
        {
            float u = i / (float)n, u2 = u * u, u3 = u2 * u;
            Add((2 * u3 - 3 * u2 + 1) * a + (u3 - 2 * u2 + u) * ta + (-2 * u3 + 3 * u2) * b + (u3 - u2) * tb);
        }
    }

    void Add(Vector3 p)
    {
        float len = _route.Count == 0 ? 0f : _routeLen[_routeLen.Count - 1] + (p - _route[_route.Count - 1]).magnitude;
        _route.Add(p); _routeLen.Add(len);
    }

    /// <summary>
    /// A few passes of neighbour averaging: the joins between the curves and the lane points, and
    /// the lane's own 1 m steps, otherwise show as little kinks in the heading. The ends (and the
    /// point next to each, which sets the end direction) stay put so it still parks exactly.
    /// </summary>
    void SmoothRoute()
    {
        int n = _route.Count;
        if (n < 6) return;
        var tmp = new Vector3[n];
        for (int pass = 0; pass < 4; pass++)
        {
            _route.CopyTo(tmp);
            for (int i = 2; i < n - 2; i++)
                _route[i] = (tmp[i - 2] + tmp[i - 1] * 2f + tmp[i] * 2f + tmp[i + 1] * 2f + tmp[i + 2]) / 8f;
        }
        _routeLen.Clear();
        float len = 0f;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) len += (_route[i] - _route[i - 1]).magnitude;
            _routeLen.Add(len);
        }
    }

    Vector3 RouteAt(float arc)
    {
        arc = Mathf.Clamp(arc, 0f, _routeLen[_routeLen.Count - 1]);
        int i = 1;
        while (i < _routeLen.Count - 1 && _routeLen[i] < arc) i++;
        float k = Mathf.InverseLerp(_routeLen[i - 1], _routeLen[i], arc);
        return Vector3.Lerp(_route[i - 1], _route[i], k);
    }

    void DriveRoute()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        float total = _routeLen[_routeLen.Count - 1], rem = total - _arc;

        // Speed with limited jerk: the acceleration eases in and out instead of jumping, so it
        // pulls away and comes to rest the way a heavy vehicle does.
        float brake = decel * 0.8f;
        float want = _routeTop;
        if (_routeParks) want = Mathf.Min(want, Mathf.Sqrt(2f * brake * Mathf.Max(0f, rem - 0.15f)));
        else want = Mathf.Min(want, Mathf.Sqrt(_routeEndSpeed * _routeEndSpeed + 2f * brake * Mathf.Max(0f, rem)));
        // The player standing in its way: brake and wait behind them until they step aside.
        bool blocked = false;
        if (RoadDriver.PlayerPosition(out Vector3 pl) && Mathf.Abs(pl.y - transform.position.y) < 5f)
        {
            Vector3 d = Flat(pl - transform.position);
            float along = Vector3.Dot(d, Fwd), side = Mathf.Abs(Vector3.Dot(d, Right));
            if (along > 0f && side < _halfWid + 0.8f)
            {
                float room = along - _halfLen - 1.5f;
                float wp = room <= 0f ? 0f : Mathf.Sqrt(2f * brake * room);
                if (wp < want) { want = wp; blocked = true; }
                if (room <= 0f) { _v = Mathf.Min(_v, 0.5f); _a = Mathf.Min(_a, 0f); }
            }
        }
        float aWant = Mathf.Clamp((want - _v) * 1.6f, -decel, accel);
        _a = Mathf.MoveTowards(_a, aWant, jerk * dt);
        if (_v > want + 0.5f) _a = Mathf.Min(_a, aWant);        // never overshoot the braking curve
        _v = Mathf.Max(0f, _v + _a * dt);
        if (_routeParks && rem < 0.6f && !blocked) _v = Mathf.Max(Mathf.Min(_v, rem * 1.2f), 0.12f);
        if (blocked && want <= 0.01f && _v < 0.6f) _v = Mathf.MoveTowards(_v, 0f, 3f * dt);
        _arc = Mathf.Min(total, _arc + _v * dt);

        Vector3 pos = RouteAt(_arc);
        // Heading along a chord of the route around it: turns follow the curve smoothly instead of
        // stepping from one route segment to the next.
        float span = Mathf.Min(steerSpan, total * 0.25f);
        Vector3 ahead = RouteAt(_arc + span), behind = RouteAt(_arc - span);
        Vector3 tan = ahead - behind;

        pos.y = transform.position.y;          // route points are flat: cast from the vehicle's height
        float gy = Ground(pos) + _yOffset;
        _yNow = Mathf.Lerp(_yNow, gy, 1f - Mathf.Exp(-12f * dt));
        pos.y = _yNow;
        transform.position = pos;
        if (Flat(tan).sqrMagnitude > 1e-6f)
        {
            float turn = Vector3.SignedAngle(Fwd, Flat(tan).normalized, Vector3.up);
            turn = Mathf.Clamp(turn, -60f * dt, 60f * dt) ;
            transform.rotation = Quaternion.AngleAxis(turn, Vector3.up) * transform.rotation;
        }

        if (_arc >= total - 0.01f)
        {
            if (_routeParks)
            {
                transform.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(Fwd, _stopFwd, Vector3.up), Vector3.up) * transform.rotation;
                _state = State.Parked; _stateAt = Time.time; _v = 0f; _a = 0f;
                if (log) Debug.Log($"[Ambulance] parked at {transform.position:F1}; chest point is " +
                                   $"{Flat(rig.TransformPoint(ParamedicRig.kChest) - VictimChest()).magnitude:0.00} m from the boy's chest.", this);
            }
            else Rejoin();
        }
    }

    // ================================================================ 5. leaving

    /// <summary>Pull away from the stop back onto its own side, past the boy, when that lane is free.</summary>
    void TryLeave()
    {
        TrafficPath p = _ownPath != null ? _ownPath : (_accDriver != null ? _accDriver.path : null);
        if (p == null) { _state = State.Gone; return; }
        _ownPath = p;

        // Its own lane, heading the way it was going, a little past the boy.
        Transform vh = victim != null ? RootSpace.Find(victim, "hips") : null;
        Vector3 boy = vh != null ? vh.position : (victim != null ? victim.position : transform.position);
        float best = float.MaxValue, dBoy = 0f;
        for (float d = 0f; d < p.Length; d += 1f)
        {
            p.Sample(d, out Vector3 q, out Vector3 dir);
            if (Vector3.Dot(Flat(dir).normalized, _travel) < 0.7f) continue;
            float dd = Flat(q - boy).sqrMagnitude;
            if (dd < best) { best = dd; dBoy = d; }
        }
        _rejoinD = Mathf.Repeat(dBoy + rejoinPast, p.Length);
        if (Occupied(p, _rejoinD, 12f)) return;                 // someone there - wait

        p.Sample(_rejoinD, out Vector3 M, out Vector3 mDir);
        mDir = Flat(mDir).normalized;

        _route.Clear(); _routeLen.Clear();
        AddHermite(Flat(transform.position), Fwd, Flat(M), mDir, false);
        // ...and a few metres straight along the lane, so it is lined up when RoadDriver takes over.
        for (float k = 1f; k <= 6f; k += 1f)
        {
            p.Sample(Mathf.Repeat(_rejoinD + k, p.Length), out Vector3 q, out _);
            Add(Flat(q));
        }
        _rejoinD = Mathf.Repeat(_rejoinD + 6f, p.Length);
        SmoothRoute();

        _arc = 0f; _v = 0f; _a = 0f;
        _routeParks = false;
        _routeTop = leaveSpeed;
        _routeEndSpeed = Mathf.Min(leaveSpeed, _driver.speed > 0.1f ? _driver.speed : leaveSpeed);
        _yOffset = transform.position.y - Ground(transform.position);
        _yNow = transform.position.y;
        _state = State.Leaving;
        if (log) Debug.Log($"[Ambulance] pulling away - {_routeLen[_routeLen.Count - 1]:0} m back onto its own side.", this);
    }

    /// <summary>Hand back to RoadDriver on its own lane; open the other side to traffic again.</summary>
    void Rejoin()
    {
        if (_ownPath != null)
        {
            _driver.path = _ownPath;
            _ownPath.Register(_driver);
            _driver.Teleport(_rejoinD);
            _driver.SetCurrentSpeed(_v);
        }
        _driver.manualPose = false;
        if (_lane != null) _lane.ClearBlocks();
        _state = State.Gone;
        if (log) Debug.Log("[Ambulance] back in its own lane - driving on.", this);
    }

    bool FindOppositeLane(Vector3 boy)
    {
        Vector3 accDir = Vector3.zero;
        if (_accDriver != null && _accDriver.path != null)
        {
            _accDriver.path.Sample(_accDriver.Distance, out _, out accDir);
            accDir = Flat(accDir).normalized;
        }
        float best = float.MaxValue;
        _lane = null;
        foreach (var p in FindObjectsByType<TrafficPath>(FindObjectsSortMode.None))
        {
            if (p.kind != TrafficPath.PathKind.Vehicles) continue;
            for (float d = 0f; d < p.Length; d += 1f)
            {
                p.Sample(d, out Vector3 pos, out Vector3 dir);
                dir = Flat(dir).normalized;
                if (accDir != Vector3.zero && Vector3.Dot(dir, accDir) > -0.8f) continue;
                float dist = Flat(pos - boy).magnitude;
                if (dist < best && dist < 30f) { best = dist; _lane = p; _laneDir = dir; _laneOrigin = pos; }
            }
        }
        return _lane != null;
    }

    float NearestOnLane(Vector3 w)
    {
        float best = float.MaxValue, bestD = 0f;
        for (float d = 0f; d < _lane.Length; d += 0.5f)
        {
            _lane.Sample(d, out Vector3 pos, out Vector3 dir);
            if (Vector3.Dot(Flat(dir).normalized, _laneDir) < 0.7f) continue;
            float dist = Flat(pos - w).sqrMagnitude;
            if (dist < best) { best = dist; bestD = d; }
        }
        return bestD;
    }

    // ================================================================ 3. where to stop

    void SolveStop(Vector3 chest)
    {
        // The clip's chest point relative to the vehicle, as (along, lateral) of its own frame.
        Vector3 P = rig.TransformPoint(ParamedicRig.kChest);
        float pa = Vector3.Dot(P - transform.position, Fwd);
        float pl = Vector3.Dot(P - transform.position, Right);

        Bounds car = AccidentCarBounds();
        Vector3[] boyPts = BoyPoints();
        Vector3 laneRight = Vector3.Cross(Vector3.up, _travel);

        // Where the clip lays the stretcher on the road, in the vehicle's frame - it must not
        // land on the boy (his head ended up lying on it).
        Vector3 sgW = rig.TransformPoint(kStretcherGround);
        float sa = Vector3.Dot(sgW - transform.position, Fwd), sl = Vector3.Dot(sgW - transform.position, Right);
        Vector3 sAxis = Flat(rig.TransformDirection(Vector3.forward)).normalized;
        float sAng = Vector3.SignedAngle(Fwd, sAxis, Vector3.up);
        float sHalfL = kStretcherLen * 0.5f * _s + 0.35f, sHalfW = kStretcherWidth * 0.5f * _s + 0.35f;

        float bestCost = float.MaxValue;
        for (float th = -maxAngle; th <= maxAngle + 0.01f; th += 0.5f)
        {
            Vector3 f = Quaternion.AngleAxis(th, Vector3.up) * _travel;
            Vector3 r = Vector3.Cross(Vector3.up, f);
            Vector3 pos = Flat(chest) - f * pa - r * pl;
            pos.y = transform.position.y;

            Vector3 centre = pos + f * _bodyAlong + r * _bodyLat;
            float dev = Mathf.Abs(Vector3.Dot(Flat(centre - _laneOrigin), laneRight));
            float cost = Mathf.Abs(th) + Mathf.Max(0f, dev - maxLaneOffset) * 30f;

            for (int i = 0; i <= 6; i++)
                for (int j = 0; j <= 2; j++)
                {
                    Vector3 q = centre + f * Mathf.Lerp(-_halfLen, _halfLen, i / 6f) + r * Mathf.Lerp(-_halfWid, _halfWid, j / 2f);
                    if (car.size != Vector3.zero && Inside2D(car, q, 0.5f)) cost += 40f;
                }
            Vector3 sc = pos + f * sa + r * sl;
            Vector3 sf = Quaternion.AngleAxis(sAng, Vector3.up) * f, sr = Vector3.Cross(Vector3.up, sf);
            foreach (var b in boyPts)
            {
                Vector3 d = Flat(b - centre);
                if (Mathf.Abs(Vector3.Dot(d, f)) < _halfLen + 0.8f && Mathf.Abs(Vector3.Dot(d, r)) < _halfWid + 0.8f) cost += 200f;
                Vector3 ds = Flat(b - sc);
                if (Mathf.Abs(Vector3.Dot(ds, sf)) < sHalfL && Mathf.Abs(Vector3.Dot(ds, sr)) < sHalfW) cost += 150f;
            }
            if (cost < bestCost) { bestCost = cost; _stopPos = pos; _stopFwd = f; }
        }
    }

    static bool Inside2D(Bounds b, Vector3 p, float pad) =>
        p.x > b.min.x - pad && p.x < b.max.x + pad && p.z > b.min.z - pad && p.z < b.max.z + pad;

    Bounds AccidentCarBounds()
    {
        Bounds b = default; bool any = false;
        foreach (var r in accidentCar.GetComponentsInChildren<Renderer>())
        {
            if (r is SkinnedMeshRenderer || r is ParticleSystemRenderer) continue;
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        return any ? b : new Bounds(Vector3.zero, Vector3.zero);
    }

    Vector3[] BoyPoints()
    {
        var pts = new List<Vector3>();
        foreach (var n in new[] { "hips", ":spine", "spine2", ":head", "headtop_end", "leftleg", "rightleg",
                                  "leftfoot", "rightfoot", "lefthand", "righthand", "leftforearm", "rightforearm" })
        {
            var t = RootSpace.Find(victim, n);
            if (t != null) pts.Add(t.position);
        }
        return pts.ToArray();
    }

    /// <summary>
    /// Where the CPR goes: his belly / lower chest (the lowest spine bone, just above the hips) -
    /// not the upper chest, where the clip's compressions sit right under the chin.
    /// </summary>
    Vector3 VictimChest() => ParamedicRig.CprPoint(victim, cprSpot);

    float Ground(Vector3 at)
    {
        var hits = Physics.RaycastAll(at + Vector3.up * 8f, Vector3.down, 30f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MinValue;
        foreach (var h in hits)
        {
            if (h.collider.transform.IsChildOf(transform)) continue;
            if (h.collider.GetComponentInParent<RoadDriver>() != null) continue;
            if (victim != null && h.collider.transform.IsChildOf(victim)) continue;
            var rb = h.collider.attachedRigidbody; if (rb != null && !rb.isKinematic) continue;
            if (h.point.y > best) best = h.point.y;
        }
        return best > float.MinValue ? best : at.y;
    }

    /// <summary>Bystanders standing on its route or where the paramedic has to work step aside.</summary>
    void ClearWorkArea()
    {
        Vector3 chest = Flat(VictimChest());
        Vector3 rear = Flat(_stopPos + _stopFwd * (_rearAlong));
        Vector3 toAmb = (rear - chest).normalized;

        Quaternion turn = Quaternion.AngleAxis(Vector3.SignedAngle(Fwd, _stopFwd, Vector3.up), Vector3.up);
        Vector3 Map(Vector3 rigLocal) => Flat(_stopPos + turn * (rig.TransformPoint(rigLocal) - transform.position));
        Vector3 sg = Map(kStretcherGround), kn = Map(kKneel);

        foreach (var b in FindObjectsByType<AccidentBystander>(FindObjectsSortMode.None))
        {
            Vector3 p = Flat(b.transform.position);
            float d = Mathf.Min(SegDist(p, rear, sg), SegDist(p, sg, chest), SegDist(p, chest, kn));
            d = Mathf.Min(d, (p - chest).magnitude - 1.5f);
            bool onRoute = false;
            for (int i = 0; i < _route.Count && !onRoute; i += 2)
                onRoute = (p - _route[i]).magnitude < _halfWid + 1.2f;
            bool inBox = Mathf.Abs(Vector3.Dot(p - Flat(_stopPos), _stopFwd)) < _halfLen + 1.5f &&
                         Mathf.Abs(Vector3.Dot(p - Flat(_stopPos), Vector3.Cross(Vector3.up, _stopFwd))) < _halfWid + 1.5f;
            if (d > 3.0f && !inBox && !onRoute) continue;

            // A spot on a ring round the boy, on the side away from the ambulance.
            Vector3 from = p - chest; if (from.sqrMagnitude < 0.01f) from = -toAmb;
            float ang = Vector3.SignedAngle(toAmb, from.normalized, Vector3.up);
            if (Mathf.Abs(ang) < 110f) ang = ang >= 0f ? 110f : -110f;
            Vector3 dir = Quaternion.AngleAxis(ang, Vector3.up) * toAmb;
            Vector3 centre = VictimChest();
            Vector3 spot = AccidentBystander.OnRoadLevel(centre, dir, 4.6f + (Mathf.Abs(p.x * 7.13f) % 1f) * 1.0f, 3.2f);
            spot.y = b.transform.position.y;
            b.MoveAside(spot, VictimChest());
        }
    }

    static float SegDist(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a; float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
        return (p - (a + ab * t)).magnitude;
    }

    // ================================================================ 4. work

    void StartWork()
    {
        var pr = rig.GetComponent<ParamedicRig>();
        if (pr == null) pr = rig.gameObject.AddComponent<ParamedicRig>();
        pr.victim = victim;
        pr.cprPoint = cprSpot;
        var carry = victim.GetComponent<BoyCarry>();
        if (carry == null) carry = victim.gameObject.AddComponent<BoyCarry>();
        carry.rigAnimator = _rigAnim;
        carry.paramedicHips = _rigHips;
        carry.leftHand = RootSpace.Find(rig, "lefthand");
        carry.rightHand = RootSpace.Find(rig, "righthand");
        carry.stretcherTop = RootSpace.Find(rig, "stretcher_top");
        carry.kit = _kit;
        carry.mask = _mask;

        _rigAnim.Play(_rigState, 0, 0f);
        _rigAnim.speed = 1f;
        _state = State.Working;
        if (log) Debug.Log($"[Ambulance] door open - paramedic starts (rig scale {_s:0.00}, floor lift {_floorLift:0.00}).", this);
    }

    // ================================================================ door / lights

    void PickDoorSign()
    {
        var r = _hinge.GetComponentInChildren<Renderer>();
        if (r == null) return;
        Vector3 c = r.bounds.center, h = _hinge.position;
        Vector3 c1 = Quaternion.AngleAxis(10f, Vector3.up) * (c - h);
        _doorSign = Vector3.Dot(c1 - (c - h), -Fwd) >= 0f ? 1f : -1f;
    }

    void SetDoor(float k)
    {
        if (_hinge == null) return;
        _hinge.localPosition = _hingeLocalPos;
        _hinge.localRotation = _hingeLocalRot;
        _hinge.RotateAround(_hinge.position, Vector3.up, _doorSign * doorOpenAngle * k);
    }

    void SetLights(bool on)
    {
        if (_lights == null) return;
        foreach (var l in _lights) if (l != null) l.enabled = on;
    }
}
