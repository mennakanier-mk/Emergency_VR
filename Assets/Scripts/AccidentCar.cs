using UnityEngine;

/// <summary>
/// Scripted accident: this car reaches the boy at the exact moment he falls, brakes to a stop
/// at the impact point, then its (separate) door swings open. Every car behind it in the SAME
/// lane queues up behind it on its own - each one brakes when it gets close, so they stop one
/// after another with natural gaps, not all at once. Cars in other lanes keep driving.
///
/// How the timing works: the boy's clip starts when the scene starts, so the moment he falls is
///     Impact Clip Time + Run Extender's extra strides x stride length
/// seconds after Play. Every frame the car's speed is set to
///     (path distance left to the boy) / (time left)
/// so it arrives on that frame whatever its starting distance. Place the car anywhere upstream
/// in the boy's lane; the further away, the faster it comes in (the speed is logged at start).
///
/// Uses RoadDriver for the driving, so the queue behind it is RoadDriver's normal gap-keeping.
/// If the car has no RoadDriver, one is added on the nearest vehicle path at Play.
/// </summary>
[DefaultExecutionOrder(40)]     // before RoadDriver (50) so its speed is set for this frame
[DisallowMultipleComponent]
public class AccidentCar : MonoBehaviour
{
    [Header("Victim")]
    [Tooltip("The boy. Left empty, the object with Run Extender / Ground Follow Animated is used.")]
    public Transform victim;

    [Tooltip("Seconds into the boy's clip at which he is hit (the end of his run).")]
    public float impactClipTime = 4.33f;

    [Tooltip("Hit him this many metres before the car's centre reaches him. 0 = measured " +
             "(half the car's length), so the front bumper is what hits.")]
    public float frontOffset = 0f;

    [Header("Approach")]
    [Tooltip("On: the car starts this far back up the lane at Play and drives in at Approach " +
             "Speed, so you can leave it parked right where the hit happens. Off: it starts " +
             "where you placed it and its speed is worked out from the distance.")]
    public bool startUpstream = true;

    [Tooltip("Speed it arrives at, km/h (Start Upstream only).")]
    [Range(10f, 100f)] public float approachSpeedKmh = 40f;

    [Header("Stopping")]
    [Tooltip("Seconds from impact speed to standstill.")]
    [Range(0.1f, 2f)] public float brakeTime = 0.35f;

    [Tooltip("Bumper-to-boy distance at the moment of impact, metres. Small = the bumper " +
             "actually touches him.")]
    public float contactGap = 0.25f;

    [Tooltip("How far the hit throws him forward, beyond what the braking car pushes. The car " +
             "then stops just short of him instead of on top of him.")]
    public float knockback = 0.5f;

    [Tooltip("Seconds the throw takes.")]
    [Range(0.1f, 1.5f)] public float knockbackTime = 0.45f;

    [Header("Door")]
    [Tooltip("The separate door mesh. Left empty, a child named '*door*' or ending in '.001' " +
             "under the body is used.")]
    public Transform door;

    [Tooltip("Seconds after impact before the door opens.")]
    public float doorDelay = 1.2f;

    [Tooltip("How far it swings open, degrees.")]
    [Range(10f, 90f)] public float doorAngle = 65f;

    [Tooltip("Seconds the swing takes.")]
    [Range(0.2f, 3f)] public float doorOpenTime = 0.9f;

    [Tooltip("Flip if the door swings into the car instead of out.")]
    public bool flipDoor = false;

    [Header("Debug")]
    public bool log = true;

    [Header("Blood")]
    [Tooltip("Add a Blood Pool to the victim: starts at the head once he is lying still.")]
    public bool addBlood = true;

    [Header("Bystanders")]
    [Tooltip("Pedestrians within this many metres stop, turn and look at the accident.")]
    public float bystanderRadius = 40f;

    [Tooltip("Of those, people within this many metres run over to it; the rest stop and look.")]
    public float runRadius = 25f;

    [Header("Driver")]
    [Tooltip("Seconds after the door starts opening before the person inside starts getting out.")]
    public float exitDelay = 0.3f;

    /// <summary>Fired on the impact frame, with the point of impact.</summary>
    [Header("Afterwards")]
    [Tooltip("Once the ambulance has the boy on board: the driver walks back, gets in, shuts the " +
             "door, and the car drives on with the traffic.")]
    public bool driveOnAfterwards = true;
    [Tooltip("Seconds after the ambulance doors shut before the driver walks back.")]
    public float returnDelay = 1.0f;
    [Tooltip("Speed it drives on at, m/s (0 = the RoadDriver's own speed).")]
    public float driveOnSpeed = 0f;

    public event System.Action<Vector3> Impact;
    /// <summary>Fired when the door starts to open.</summary>
    public event System.Action DoorOpening;

    // ---------------------------------------------------------------- state

    RoadDriver _driver;
    float _impactTime;          // seconds after Start
    float _impactSpeed;
    bool _hit, _stopped;
    float _hitAt;
    Transform _doorPivot;
    Quaternion _doorBase;
    bool _noDoorFired;
    bool _pushInit;
    float _hitDist;
    Vector3 _victimStart;
    Vector3 _laneDir = Vector3.forward;
    float _doorT;
    float _doorSign = 1f;
    bool _pushDone;
    readonly System.Collections.Generic.List<CarExitPassenger> _passengers = new System.Collections.Generic.List<CarExitPassenger>();
    AmbulanceResponse _amb; bool _ambLooked;
    bool _returnStarted, _returnSent, _doorClosing, _drivingOn, _crowdReleased;
    float _returnAt;

    void Awake()
    {
        // Anything else that drives this car would fight us.
        foreach (var mb in GetComponents<MonoBehaviour>())
        {
            if (mb == this || mb is RoadDriver) continue;
            string n = mb.GetType().Name;
            if (n == "VehicleController" || n == "AnimatedVehicleMover") mb.enabled = false;
        }

        _driver = GetComponent<RoadDriver>();
        if (_driver == null)
        {
            var path = NearestVehiclePath(transform.position);
            if (path == null)
            {
                Debug.LogError($"[AccidentCar] '{name}': no vehicle path in the scene.", this);
                enabled = false;
                return;
            }
            _driver = gameObject.AddComponent<RoadDriver>();
            _driver.path = path;
            _driver.keepPlacedFacing = true;
            _driver.stickToGround = true;
            path.Register(_driver);     // OnEnable ran before the path was set
        }

        _driver.externalControl = true;
        _driver.externalSpeed = 0f;
    }

    void Start()
    {
        if (!enabled) return;

        if (victim == null)
        {
            var ext = FindFirstObjectByType<RunExtender>();
            if (ext != null) victim = ext.transform;
            else
            {
                var gf = FindFirstObjectByType<GroundFollowAnimated>();
                if (gf != null) victim = gf.transform;
            }
        }

        if (victim == null)
        {
            Debug.LogError($"[AccidentCar] '{name}': no victim found - assign Victim.", this);
            _driver.externalControl = false;
            enabled = false;
            return;
        }

        if (addBlood && victim.GetComponent<BloodPool>() == null)
            victim.gameObject.AddComponent<BloodPool>();

        _impactTime = impactClipTime;
        var run = victim.GetComponent<RunExtender>();
        if (run != null && run.enabled)
            _impactTime += run.ExtraTime;

        if (door == null) door = FindDoor();

        // Anyone sitting in the car (an animated character parented under it) waits for the door.
        foreach (var a in GetComponentsInChildren<Animator>(true))
        {
            if (a.gameObject == gameObject || a.runtimeAnimatorController == null) continue;
            var p = a.GetComponent<CarExitPassenger>();
            if (p == null) p = a.gameObject.AddComponent<CarExitPassenger>();
            p.Bind(this, exitDelay, victim);
            _passengers.Add(p);
        }
    }

    bool _ready;

    /// <summary>First Update: RoadDriver.Start has run by now, so the car's path distance is set.</summary>
    void Init()
    {
        _ready = true;
        if (frontOffset <= 0f) frontOffset = MeasureHalfLength();

        // The lane is the stretch of path the car was PLACED on; remember its direction so the
        // target is always looked for on this carriageway, wherever the car is on the loop.
        _driver.path.Sample(_driver.Distance, out _, out _laneDir);
        _laneDir.y = 0f; _laneDir.Normalize();

        if (startUpstream)
        {
            float left0 = Mathf.Max(0.5f, _impactTime - Time.timeSinceLevelLoad);
            float target = NearestInMyLane(_driver.path, VictimPoint());
            float want = approachSpeedKmh / 3.6f * left0 + frontOffset + StopShort();

            // Walk back along the straight; stop before the bend so the car never starts on the
            // U-turn or the other carriageway. It then simply comes in a little slower.
            float back = 0f;
            while (back < want)
            {
                _driver.path.Sample(Mathf.Repeat(target - back - 0.5f, _driver.path.Length), out _, out Vector3 d);
                d.y = 0f;
                if (Vector3.Dot(d.normalized, _laneDir) < 0.97f) break;
                back += 0.5f;
            }
            _driver.Teleport(target - back);
        }

        if (log)
        {
            float d = DistanceToTarget();
            float left = Mathf.Max(0.01f, _impactTime - Time.timeSinceLevelLoad);
            Debug.Log($"[AccidentCar] '{name}': hits '{victim.name}' at t={_impactTime:0.00}s, " +
                      $"{d:0.0} m away -> {d / left * 3.6f:0} km/h. " +
                      $"Door: {(door != null ? door.name : "none")}", this);
        }
    }

    void Update()
    {
        if (!_ready) Init();
        float now = Time.timeSinceLevelLoad;

        if (!_hit)
        {
            float left = _impactTime - now;
            float dist = DistanceToTarget();

            if (left <= Time.deltaTime || dist <= 0.05f)
            {
                _hit = true;
                _hitAt = now;
                _impactSpeed = _driver.CurrentSpeed;
                Vector3 at = VictimPoint();
                AccidentBystander.AlertAll(at, bystanderRadius, runRadius, victim);
                Impact?.Invoke(at);
                if (log) Debug.Log($"[AccidentCar] impact at t={now:0.00}s, {_impactSpeed * 3.6f:0} km/h, " +
                                   $"{dist:0.00} m off.", this);
            }
            else
            {
                _driver.externalSpeed = dist / left;
                return;
            }
        }

        // Brake to a stop; RoadDriver keeps it registered on the path, so the cars behind
        // see a stopped car and queue on their own.
        // The hit moves him with the bumper (exactly the distance the car has travelled since
        // the impact) plus a short throw, so he stays just ahead of the car instead of under it.
        if (victim != null && !_pushDone)
        {
            if (!_pushInit) { _pushInit = true; _hitDist = _driver.Distance; _victimStart = victim.position; }
            float travel = Mathf.Repeat(_driver.Distance - _hitDist, _driver.path.Length);
            if (travel > _driver.path.Length * 0.5f) travel = 0f;
            float k = Mathf.Clamp01((now - _hitAt) / knockbackTime);
            float total = travel + knockback * (1f - (1f - k) * (1f - k));
            Vector3 p = _victimStart + _laneDir * total;
            p.y = victim.position.y;
            victim.position = p;
            if (_stopped && k >= 1f) _pushDone = true;      // he stays where he fell; the car may leave later
        }

        if (!_stopped)
        {
            float k = Mathf.Clamp01((now - _hitAt) / brakeTime);
            _driver.externalSpeed = Mathf.Lerp(_impactSpeed, 0f, k * (2f - k));   // ease out
            if (k >= 1f) { _driver.externalSpeed = 0f; _stopped = true; }
        }

        if (door == null && !_noDoorFired && now - _hitAt >= doorDelay)
        {
            _noDoorFired = true;
            DoorOpening?.Invoke();
        }

        // Door
        if (door != null && !_doorClosing && now - _hitAt >= doorDelay && _doorT < 1f)
        {
            if (_doorPivot == null) { BuildDoorPivot(); DoorOpening?.Invoke(); }
            _doorT = Mathf.Clamp01(_doorT + Time.deltaTime / doorOpenTime);
            float e = 1f - Mathf.Pow(1f - _doorT, 3f);
            _doorPivot.rotation = Quaternion.AngleAxis(_doorSign * doorAngle * e, Vector3.up) * _doorBase;
        }

        if (driveOnAfterwards && _stopped) Afterwards(now);
    }

    // ---------------------------------------------------------------- afterwards

    void Afterwards(float now)
    {
        if (_drivingOn) return;
        if (!_ambLooked) { _ambLooked = true; _amb = FindFirstObjectByType<AmbulanceResponse>(); }
        if (_amb == null || !_amb.isActiveAndEnabled) return;

        // 1. The ambulance has him: the driver walks back to his car.
        if (!_returnStarted && _amb.PatientLoaded) { _returnStarted = true; _returnAt = now + returnDelay; }
        if (_returnStarted && !_returnSent && now >= _returnAt)
        {
            _returnSent = true;
            foreach (var p in _passengers) if (p != null) p.ReturnToCar();
            if (log) Debug.Log($"[AccidentCar] '{name}': the driver goes back to his car.", this);
        }
        if (!_returnSent) return;

        // The ambulance has gone: the onlookers walk on.
        if (!_crowdReleased && _amb.HasLeft) { _crowdReleased = true; AccidentBystander.ResumeAll(); }

        // 2. Everyone in: shut the door.
        foreach (var p in _passengers) if (p != null && !p.Seated) return;
        if (door != null && _doorPivot != null && _doorT > 0f)
        {
            _doorClosing = true;
            _doorT = Mathf.Clamp01(_doorT - Time.deltaTime / doorOpenTime);
            float e = _doorT * _doorT * (3f - 2f * _doorT);
            _doorPivot.rotation = Quaternion.AngleAxis(_doorSign * doorAngle * e, Vector3.up) * _doorBase;
            return;
        }

        // 3. Off it goes, once the ambulance is back in the lane ahead.
        if (!_amb.HasLeft) return;
        _drivingOn = true;
        if (driveOnSpeed > 0f) _driver.speed = driveOnSpeed;
        else if (_driver.speed < 3f) _driver.speed = Mathf.Max(6f, approachSpeedKmh / 3.6f * 0.6f);
        _driver.externalControl = false;
        if (log) Debug.Log($"[AccidentCar] '{name}': door shut - driving on at {_driver.speed:0.0} m/s.", this);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Path distance from the car's front bumper to the boy, ahead along the lane.</summary>
    float DistanceToTarget()
    {
        var path = _driver.path;
        float len = path.Length;
        float target = NearestInMyLane(path, VictimPoint());
        float d = Mathf.Repeat(target - _driver.Distance, len) - frontOffset - StopShort();
        // Just past him (overshoot by a frame) is a hit, not a full lap to go.
        if (d > len - 3f * frontOffset) d -= len;
        return d;
    }

    /// <summary>
    /// Nearest point to the boy on the stretch of path going the same way as this car. A road
    /// path is a loop with both carriageways on it; a plain nearest-point would happily pick the
    /// opposite carriageway when the boy lies on the centre line, and send the car round a lap.
    /// </summary>
    float NearestInMyLane(TrafficPath path, Vector3 w)
    {
        Vector3 myDir = _laneDir;

        float len = path.Length, best = _driver.Distance, bestD = float.MaxValue;
        for (float s = 0f; s < len; s += 1f)
        {
            path.Sample(s, out Vector3 p, out Vector3 d);
            d.y = 0f;
            if (Vector3.Dot(d.normalized, myDir) < 0.7f) continue;
            float dd = (new Vector3(p.x - w.x, 0f, p.z - w.z)).sqrMagnitude;
            if (dd < bestD) { bestD = dd; best = s; }
        }
        for (float s = best - 1f; s <= best + 1f; s += 0.05f)
        {
            float r = Mathf.Repeat(s, len);
            path.Sample(r, out Vector3 p, out _);
            float dd = (new Vector3(p.x - w.x, 0f, p.z - w.z)).sqrMagnitude;
            if (dd < bestD) { bestD = dd; best = r; }
        }
        return best;
    }

    /// <summary>
    /// Where along its path this car will start once Play begins (it moves itself upstream so it
    /// reaches the boy on time). Works in the Editor too, so the ambulance behind it can be put
    /// in its starting place before Play.
    /// </summary>
    public float PredictStartDistance()
    {
        var drv = _driver != null ? _driver : GetComponent<RoadDriver>();
        if (drv == null || drv.path == null) return -1f;
        Transform v = victim;
        if (v == null) { var ext = FindFirstObjectByType<RunExtender>(); if (ext != null) v = ext.transform; }
        if (v == null) return -1f;

        TrafficPath path = drv.path;
        float cur = Application.isPlaying && _ready ? drv.Distance : drv.NearestOnPath(transform.position);
        path.Sample(cur, out _, out Vector3 lane); lane.y = 0f; lane.Normalize();
        if (!startUpstream) return cur;

        float impact = impactClipTime;
        var run = v.GetComponent<RunExtender>();
        if (run != null && run.enabled) impact += run.ExtraTime;

        Vector3 w = v.position;
        foreach (var t in v.GetComponentsInChildren<Transform>())
            if (t.name.ToLowerInvariant().EndsWith("hips")) { w = t.position; break; }

        float len = path.Length, target = cur, bestD = float.MaxValue;
        for (float s = 0f; s < len; s += 1f)
        {
            path.Sample(s, out Vector3 q, out Vector3 d); d.y = 0f;
            if (Vector3.Dot(d.normalized, lane) < 0.7f) continue;
            float dd = (new Vector3(q.x - w.x, 0f, q.z - w.z)).sqrMagnitude;
            if (dd < bestD) { bestD = dd; target = s; }
        }
        for (float s = target - 1f; s <= target + 1f; s += 0.05f)
        {
            float r = Mathf.Repeat(s, len);
            path.Sample(r, out Vector3 q, out _);
            float dd = (new Vector3(q.x - w.x, 0f, q.z - w.z)).sqrMagnitude;
            if (dd < bestD) { bestD = dd; target = r; }
        }

        float fo = frontOffset;
        if (fo <= 0f)
        {
            Vector3 e = CarBounds().extents;
            fo = Mathf.Clamp(Mathf.Abs(lane.x) * e.x + Mathf.Abs(lane.z) * e.z, 1f, 4f);
        }
        float want = approachSpeedKmh / 3.6f * Mathf.Max(0.5f, impact) + fo + contactGap;
        float back = 0f;
        while (back < want)
        {
            path.Sample(Mathf.Repeat(target - back - 0.5f, len), out _, out Vector3 d); d.y = 0f;
            if (Vector3.Dot(d.normalized, lane) < 0.97f) break;
            back += 0.5f;
        }
        return Mathf.Repeat(target - back, len);
    }

    /// <summary>Bumper-to-boy gap at the moment of impact: stop clearance + braking distance.</summary>
    float StopShort() => contactGap;

    // ---------------------------------------------------------------- driver route

    Vector3 _doorHinge, _doorSide, _carFwd;
    float _doorLen;

    /// <summary>
    /// Route for the driver once the door is open: round the outside of the open door, then to a
    /// spot in front of the car beside the bonnet, looking at the boy.
    /// </summary>
    public bool GetExitRoute(out Vector3 aroundDoor, out Vector3 standPoint, out Vector3 lookAt)
    {
        aroundDoor = standPoint = lookAt = Vector3.zero;
        if (_driver == null || victim == null) return false;

        Vector3 fwd = _carFwd.sqrMagnitude > 0.5f ? _carFwd : _laneDir;
        Vector3 side = _doorSide.sqrMagnitude > 0.5f ? _doorSide : Vector3.Cross(Vector3.up, fwd);

        Bounds cb = CarBounds();
        Vector3 c = cb.center; c.y = transform.position.y;

        if (_doorLen > 0.01f)
        {
            Vector3 open = Quaternion.AngleAxis(_doorSign * doorAngle, Vector3.up) * -fwd;
            Vector3 tip = _doorHinge + open * _doorLen;
            aroundDoor = tip + side * 0.7f - fwd * 0.2f;
        }
        else aroundDoor = c + side * (SideExtent(cb, side) + 0.9f);

        standPoint = c + fwd * (frontOffset + 0.9f) + side * (SideExtent(cb, side) * 0.6f);
        lookAt = VictimPoint();
        aroundDoor.y = standPoint.y = lookAt.y = transform.position.y;
        return true;
    }

    static float SideExtent(Bounds b, Vector3 side) =>
        Mathf.Abs(side.x) * b.extents.x + Mathf.Abs(side.z) * b.extents.z;

    /// <summary>The car's own renderers - not the people sitting in it.</summary>
    Bounds CarBounds()
    {
        Bounds b = default; bool any = false;
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            if (r is SkinnedMeshRenderer) continue;
            var a = r.GetComponentInParent<Animator>();
            if (a != null && a.gameObject != gameObject) continue;
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        return any ? b : new Bounds(transform.position, Vector3.one * 2f);
    }

    Vector3 VictimPoint()
    {
        foreach (var t in victim.GetComponentsInChildren<Transform>())
            if (t.name.ToLowerInvariant().EndsWith("hips")) return t.position;
        return victim.position;
    }

    static float Nearest(TrafficPath p, Vector3 w)
    {
        if (p is TrafficRoad r) return r.NearestDistance(w);
        if (p is TrafficRing g) return g.NearestDistance(w);
        if (p is TrafficWalkway k) return k.NearestDistance(w);
        return 0f;
    }

    static TrafficPath NearestVehiclePath(Vector3 at)
    {
        TrafficPath best = null;
        float bestD = float.MaxValue;
        foreach (var p in FindObjectsByType<TrafficPath>(FindObjectsSortMode.None))
        {
            if (p.kind != TrafficPath.PathKind.Vehicles) continue;
            p.Sample(Nearest(p, at), out Vector3 pos, out _);
            float d = (pos - at).sqrMagnitude;
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    float MeasureHalfLength()
    {
        Bounds b = CarBounds();

        _driver.path.Sample(_driver.Distance, out _, out Vector3 dir);
        dir.y = 0f; dir.Normalize();
        Vector3 e = b.extents;
        return Mathf.Clamp(Mathf.Abs(dir.x) * e.x + Mathf.Abs(dir.z) * e.z, 1f, 4f);
    }

    Transform FindDoor()
    {
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (t.name.ToLowerInvariant().Contains("door")) return t;
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (t.GetComponent<MeshRenderer>() != null && t.name.EndsWith(".001") &&
                t.parent != null && t.parent.name.ToUpperInvariant().Contains("BODY"))
                return t;
        return null;
    }

    /// <summary>
    /// Hinge on the door's front edge, on its outer side, turning about world up - worked out
    /// from the mesh, because the door was split off with the car's origin as its pivot.
    /// </summary>
    void BuildDoorPivot()
    {
        var r = door.GetComponent<Renderer>();
        Bounds db = r != null ? r.bounds : new Bounds(door.position, Vector3.one * 0.5f);

        Bounds cb = CarBounds(); bool any = true;

        _driver.path.Sample(_driver.Distance, out _, out Vector3 fwd);
        fwd.y = 0f; fwd.Normalize();
        if (_driver.reverseDirection) fwd = -fwd;

        Vector3 side = db.center - (any ? cb.center : transform.position);
        side -= Vector3.Dot(side, fwd) * fwd; side.y = 0f;
        side = side.sqrMagnitude > 1e-4f ? side.normalized : Vector3.Cross(Vector3.up, fwd);

        // Front-outer corner of the door's bounds, at mid height.
        Vector3 c = db.center, e = db.extents;
        float alongF = Mathf.Abs(fwd.x) * e.x + Mathf.Abs(fwd.z) * e.z;
        float alongS = Mathf.Abs(side.x) * e.x + Mathf.Abs(side.z) * e.z;
        Vector3 hinge = c + fwd * alongF + side * alongS;

        var pivot = new GameObject(door.name + "_Hinge").transform;
        pivot.SetParent(door.parent, false);
        pivot.position = hinge;
        pivot.rotation = Quaternion.identity;
        pivot.SetParent(door.parent, true);
        door.SetParent(pivot, true);
        _doorPivot = pivot;
        _doorBase = pivot.rotation;

        // Which way round swings the rear edge outwards?
        Vector3 rear = -fwd;
        Vector3 turned = Quaternion.AngleAxis(doorAngle, Vector3.up) * rear;
        _doorSign = Vector3.Dot(turned, side) > 0f ? 1f : -1f;
        if (flipDoor) _doorSign = -_doorSign;

        _doorHinge = hinge;
        _doorSide = side;
        _carFwd = fwd;
        _doorLen = alongF * 2f;

        // Solid door, so the driver (and the player) cannot walk through it. It turns with the
        // hinge, so it is always where the door is.
        if (door.GetComponent<Collider>() == null)
        {
            var bc = door.gameObject.AddComponent<BoxCollider>();
            if (door.GetComponent<MeshFilter>() is MeshFilter mf && mf.sharedMesh != null)
            {
                bc.center = mf.sharedMesh.bounds.center;
                bc.size = mf.sharedMesh.bounds.size;
            }
        }
    }
}
