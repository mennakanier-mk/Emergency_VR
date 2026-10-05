using UnityEngine;

/// <summary>
/// Drives one vehicle along a TrafficPath. Replaces waypoint following entirely.
///
/// The vehicle's position is Sample(distance travelled), so it is on the road by construction.
/// It cannot overshoot a corner, cannot miss a waypoint and cannot wander off into a building -
/// all three of which happened with waypoints, and none of which are possible here.
///
/// Rotation deliberately keeps the pose you authored. These models import with a -90 on X and
/// an arbitrary Z, so overwriting rotation with LookRotation lays them on their side. Instead
/// the heading is applied as a rotation FROM the direction the model faced when it started TO
/// the direction the path wants, which is a pure yaw and leaves the tilt untouched. It is
/// computed fresh from the stored starting pose every frame rather than accumulated, so it
/// never drifts.
///
/// Spacing is a subtraction, not a physics cast: every vehicle on a path knows its own distance
/// along it, so the gap to the one in front is exact. No raycast means no braking for the road
/// surface, for a building on the bend, or for a car on a different street - which is what left
/// half the traffic standing still.
/// </summary>
[DefaultExecutionOrder(50)]
[DisallowMultipleComponent]
public class RoadDriver : MonoBehaviour
{
    [Header("Route")]
    public TrafficPath path;

    [Tooltip("Where on the loop this vehicle starts, in metres. Set for you at build time from " +
             "where the vehicle already stands.")]
    public float startDistance;

    [Header("Speed")]
    public float speed = 6f;

    [Tooltip("Seconds to reach full speed, and to stop. Keeps the queue from snapping.")]
    [Range(0.05f, 3f)] public float smoothing = 0.6f;

    [Header("Facing (does not change the route)")]
    /// <summary>
    /// The old VehicleController and AnimatedVehicleMover each had one of these, and they
    /// mattered: a donkey cart authored facing the other way, or a model whose "forwards" is its
    /// local X, ends up driving down the street backwards without it. The route is unaffected -
    /// this only turns the model on the spot.
    /// </summary>
    [Tooltip("Face the opposite way to travel. Same switch the old movers had.")]
    public bool reverseDirection = false;

    [Tooltip("Fine tune the facing, in degrees, on top of Reverse Direction. Use this when the " +
             "model sits at 90 degrees rather than a clean 180.")]
    [Range(-180f, 180f)] public float facingYawOffset = 0f;

    [Tooltip("Keep the direction the model faces in the Scene view: at Play, Facing Yaw Offset " +
             "is worked out from how you placed it relative to the road. Place the model facing " +
             "the way it should drive and tick this - no offset to guess.")]
    public bool keepPlacedFacing = false;

    [Header("Standing on the ground")]
    /// <summary>
    /// Places the model on the surface under it rather than at the path's Height number.
    ///
    /// The path carries a single height, so everything on it is put at exactly that Y. Get that
    /// number wrong by a metre and every character sinks into the road by a metre - which is
    /// what happened here: a walkway left at Height 5 over a pavement at 6.69 buried everyone
    /// up to the waist. Measuring the ground removes the number from the equation entirely, and
    /// as a bonus they now follow kerbs and slopes instead of floating over them.
    /// </summary>
    [Tooltip("Stand on whatever surface is under the path, instead of trusting the path's " +
             "Height. Fixes models sinking into the road.")]
    public bool stickToGround = true;

    [Tooltip("How far above the path to start looking down for the ground.")]
    public float groundProbeHeight = 6f;

    [Tooltip("How far down to look.")]
    public float groundProbeDepth = 30f;

    /// <summary>
    /// Height of the model's pivot above its own feet, measured from its renderers at startup.
    ///
    /// Some characters are authored with the pivot between the hips rather than on the floor.
    /// Putting THAT point on the ground buries the legs. Measuring the model rather than asking
    /// you to find the number by trial and error is the difference between this working on the
    /// first try and not.
    /// </summary>
    [Tooltip("Measured from the model at startup. Turn off to type the offset yourself.")]
    public bool autoGroundOffset = true;

    [Tooltip("Raise the model by this much above the ground.")]
    public float groundOffset = 0f;

    [Header("Start")]
    [Tooltip("Begin from wherever this object already stands, rather than from the start of " +
             "the path. Lets you drop the component on, drag a path in, and press Play.")]
    public bool snapOnStart = true;

    [Header("Debug")]
    public bool drawGizmo = false;

    // ---------------------------------------------------------------- state

    public float Distance { get; private set; }

    /// <summary>Speed being used right now, after queueing. Read by walk animations.</summary>
    public float CurrentSpeed { get { return _currentSpeed; } }

    /// <summary>
    /// Set by a scripted event (AccidentCar) to drive this vehicle at an exact speed, ignoring
    /// smoothing and the gap to the car in front. Cars BEHIND still queue on it normally.
    /// </summary>
    [System.NonSerialized] public bool externalControl;
    [System.NonSerialized] public float externalSpeed;

    /// <summary>Set by a scripted event to turn and face a point instead of the path.</summary>
    /// <summary>Half the length of the model along its direction of travel, measured at Start.</summary>
    public float HalfLength { get; private set; } = 2.2f;

    /// <summary>Scripted: leave the path and walk/run to a point, then stand (see BeginFreeRoam).</summary>
    /// <summary>Someone else is placing this object (scripted manoeuvre). It stays registered on
    /// its path at its last distance, so traffic behind still queues, but this script does not
    /// move it.</summary>
    [System.NonSerialized] public bool manualPose;

    [System.NonSerialized] public bool freeRoam;
    [System.NonSerialized] public Vector3 freeTarget;
    [System.NonSerialized] public float freeSpeed = 3f;
    [System.NonSerialized] public float freeStopRadius = 0.3f;

    [System.NonSerialized] public bool lookAtTarget;
    [System.NonSerialized] public Vector3 lookTarget;
    [System.NonSerialized] public float lookTurnSpeed = 120f;   // degrees per second
    float _heading;
    bool _headingSet;

    Quaternion _initialRotation;
    Vector3 _initialForwardFlat;
    float _currentSpeed;
    bool _ready;

    void OnEnable()
    {
        if (path != null) path.Register(this);
    }

    void OnDisable()
    {
        _roamers.Remove(this);
        if (path != null) path.Unregister(this);
    }

    void Start()
    {
        _initialRotation = transform.rotation;
        _initialForwardFlat = MeasureForward();

        if (autoGroundOffset) groundOffset = MeasurePivotAboveFeet();
        HalfLength = MeasureHalfLength();
        HalfWidth = MeasureHalfWidth();

        if (snapOnStart) SnapToNearest();

        Distance = startDistance;
        SpreadFromNeighbours();

        if (keepPlacedFacing && path != null)
            facingYawOffset = PlacedFacingOffset(Distance);

        _ready = path != null;

        if (!_ready)
            Debug.LogWarning($"[RoadDriver] '{name}': no path assigned, so it will not move.", this);
    }

    void Update()
    {
        if (!_ready || path == null || manualPose) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        if (freeRoam) { FreeRoamUpdate(dt); AfterMove(_currentSpeed); return; }
        if (path is TrafficRing ringPath) ringPath.EnsureSpread();

        // --- how fast are we allowed to go ---------------------------------
        if (externalControl)
        {
            _currentSpeed = Mathf.Max(0f, externalSpeed);
        }
        else
        {
            float gap = path.GapAhead(this);
            WaitingForPlayer = false;
            if (path.kind == TrafficPath.PathKind.Vehicles)
            {
                float pg = PlayerGap();
                WaitingForPlayer = pg < path.slowGap && pg <= gap;      // slowing/stopped because of you
                gap = Mathf.Min(gap, pg);
            }

            float target = speed;
            if (gap < path.minGap) target = 0f;
            else if (gap < path.slowGap)
                target = speed * Mathf.InverseLerp(path.minGap, path.slowGap, gap);

            _currentSpeed = Mathf.MoveTowards(_currentSpeed, target,
                                              (speed / Mathf.Max(0.05f, smoothing)) * dt);
        }

        Distance = Mathf.Repeat(Distance + _currentSpeed * dt, path.Length);

        // --- place it ------------------------------------------------------
        path.Sample(Distance, out Vector3 pos, out Vector3 dir);

        if (stickToGround) pos.y = GroundHeight(pos) + groundOffset;

        transform.position = pos;

        if (dir.sqrMagnitude > 0.0001f && _initialForwardFlat.sqrMagnitude > 0.0001f)
        {
            // The heading change is computed as an ANGLE, not with FromToRotation.
            //
            // This is what was throwing characters under the map at the end of a path.
            // FromToRotation(v, -v) has no single answer - every axis perpendicular to v turns
            // one into the other - so Unity picks one, and it is usually not the vertical. At
            // the U-turn, where the direction reverses exactly, it was handing back a 180
            // degree flip about X: the model turned upside down and dropped through the road.
            // Doing the arithmetic on the yaw angle cannot produce anything but a yaw.
            float fromYaw = Mathf.Atan2(_initialForwardFlat.x, _initialForwardFlat.z) * Mathf.Rad2Deg;
            float toYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

            if (lookAtTarget)
            {
                Vector3 to = lookTarget - transform.position; to.y = 0f;
                float want = to.sqrMagnitude > 0.01f ? Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg : toYaw;
                if (!_headingSet) { _heading = toYaw; _headingSet = true; }
                _heading = Mathf.MoveTowardsAngle(_heading, want, lookTurnSpeed * dt);
                toYaw = _heading;
            }
            else if (Time.time - _resumedAt < 2f && _headingSet)
            {
                // Just back on the path from walking somewhere: turn onto it, do not snap.
                _heading = Mathf.MoveTowardsAngle(_heading, toYaw, 200f * dt);
                toYaw = _heading;
            }
            else
            {
                _heading = toYaw;
                _headingSet = true;
            }

            float turn = Mathf.DeltaAngle(fromYaw, toYaw)
                       + (reverseDirection ? 180f : 0f)
                       + facingYawOffset;

            // About WORLD up, on the outside, so the import tilt underneath is untouched.
            transform.rotation = Quaternion.AngleAxis(turn, Vector3.up) * _initialRotation;
        }

        AfterMove(_currentSpeed);
    }

    /// <summary>
    /// Leave the path: walk or run to a point and stop there, facing Look At. Used by scripted
    /// events (people running to the accident). Leaves the path's queue so nobody waits on it.
    /// </summary>
    public void BeginFreeRoam(Vector3 target, float moveSpeed, float stopRadius, Vector3 lookAt)
    {
        if (path != null) path.Unregister(this);
        freeTarget = target;
        freeSpeed = moveSpeed;
        freeStopRadius = stopRadius;
        lookTarget = lookAt;
        freeRoam = true;
        externalControl = false;
        _roamers.Add(this);
    }

    float _resumedAt = -100f;

    /// <summary>Back onto the path at the nearest point after a free roam (bystanders walking on).</summary>
    public void ResumePath()
    {
        freeRoam = false;
        externalControl = false;
        lookAtTarget = false;
        _roamers.Remove(this);
        if (path == null) return;
        Distance = NearestOnPath(transform.position);
        startDistance = Distance;
        path.Register(this);
        _resumedAt = Time.time;
    }

    public float NearestOnPath(Vector3 w)
    {
        if (path is TrafficRoad road) return road.NearestDistance(w);
        if (path is TrafficRing ring) return ring.NearestDistance(w);
        if (path is TrafficWalkway way) return way.NearestDistance(w);
        float best = float.MaxValue, bestD = 0f;
        for (float d = 0f; d < path.Length; d += 0.5f)
        {
            path.Sample(d, out Vector3 q, out _);
            float dd = (new Vector3(q.x - w.x, 0f, q.z - w.z)).sqrMagnitude;
            if (dd < best) { best = dd; bestD = d; }
        }
        return bestD;
    }

    // ---------------------------------------------------------------- the player

    static int _playerFrame = -1;
    static bool _playerOk;
    static Vector3 _playerPos;
    static Transform _playerHead;
    static int _playerSearchAt;

    /// <summary>Where the player (the VR / phone camera) is standing, once per frame.</summary>
    public static bool PlayerPosition(out Vector3 p)
    {
        if (_playerFrame != Time.frameCount)
        {
            _playerFrame = Time.frameCount;
            // The phone VR rig keeps its head camera disabled (the eyes render to textures), so
            // ask the rig for its head first.
            if ((_playerHead == null || !_playerHead.gameObject.activeInHierarchy) && Time.frameCount >= _playerSearchAt)
            {
                _playerSearchAt = Time.frameCount + 30;
                _playerHead = null;
                // The walking character (normal mode): that is who the cars must not hit -
                // the camera is a few metres behind him.
                var tpp = FindFirstObjectByType<ThirdPersonPlayer>();
                if (tpp != null && tpp.isActiveAndEnabled) _playerHead = tpp.transform;
                var vr = _playerHead == null ? FindFirstObjectByType<SimpleStereoVR>() : null;
                if (vr != null && vr.enabled && vr.headCamera != null) _playerHead = vr.headCamera.transform;
                if (_playerHead == null && Camera.main != null) _playerHead = Camera.main.transform;
                if (_playerHead == null && Camera.allCamerasCount > 0) _playerHead = Camera.allCameras[0].transform;
            }
            _playerOk = _playerHead != null;
            if (_playerOk) _playerPos = _playerHead.position;
        }
        p = _playerPos;
        return _playerOk;
    }

    [Tooltip("Stop behind the player (the camera) when they stand in this vehicle's lane, and wait until they move.")]
    public bool stopForPlayer = true;
    [Tooltip("How far either side of the lane centre the player counts as in the way, metres.")]
    public float playerLaneHalfWidth = 1.8f;
    [Tooltip("Gap kept between the front bumper and the player, metres.")]
    public float playerClearance = 1.5f;

    /// <summary>
    /// Gap to the player in the same units as TrafficPath.GapAhead: the vehicle stops (gap reaches
    /// minGap) with 'playerClearance' metres between its bumper and the player.
    /// </summary>
    float PlayerGap()
    {
        if (!stopForPlayer) return Mathf.Infinity;
        path.Sample(Distance, out _, out Vector3 dir);
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return Mathf.Infinity;
        dir.Normalize();

        float best = Mathf.Infinity;
        if (PlayerPosition(out Vector3 p)) best = Mathf.Min(best, GapTo(p, dir));
        // People who left their pavement (bystanders walking back across the road) too.
        foreach (var w in _roamers)
            if (w != null && w != this) best = Mathf.Min(best, GapTo(w.transform.position, dir));
        return best;
    }

    /// <summary>True while this vehicle is slowing down or standing still because the player is in front of it.</summary>
    public bool WaitingForPlayer { get; private set; }

    float GapTo(Vector3 p, Vector3 dir)
    {
        Vector3 me = transform.position;
        if (Mathf.Abs(p.y - me.y) > 5f) return Mathf.Infinity;            // up on a roof / balcony
        Vector3 d = p - me; d.y = 0f;
        float along = Vector3.Dot(d, dir);
        float side = Mathf.Abs(Vector3.Dot(d, Vector3.Cross(Vector3.up, dir)));
        // Wide enough that someone at the front corner counts, not just dead centre: the
        // vehicle's own half width plus room for a person.
        float laneHalf = Mathf.Max(playerLaneHalfWidth, HalfWidth + 1.0f);
        if (along <= -HalfLength * 0.3f || side > laneHalf) return Mathf.Infinity;
        float room = along - HalfLength - playerClearance;
        if (room > path.slowGap + 4f) return Mathf.Infinity;
        return Mathf.Max(0.001f, path.minGap + room);
    }

    static readonly System.Collections.Generic.HashSet<RoadDriver> _roamers = new System.Collections.Generic.HashSet<RoadDriver>();

    void FreeRoamUpdate(float dt)
    {
        Vector3 pos = transform.position;
        Vector3 to = freeTarget - pos; to.y = 0f;
        float dist = to.magnitude;

        float want = dist > freeStopRadius ? freeSpeed : 0f;
        // Slow down over the last metre so they do not stop dead.
        if (dist < freeStopRadius + 1f) want *= Mathf.Clamp01((dist - freeStopRadius) / 1f);
        _currentSpeed = Mathf.MoveTowards(_currentSpeed, want, freeSpeed / 0.35f * dt);

        if (dist > 0.001f)
            pos += to / dist * Mathf.Min(_currentSpeed * dt, dist);

        // Off the path the ground under them is unknown: a pole, a sign or a car roof right next
        // to them would otherwise be "the ground" and lift them up onto it. Only step up a little.
        if (stickToGround) pos.y = GroundHeightNear(pos, transform.position.y - groundOffset, 0.7f) + groundOffset;
        transform.position = pos;

        // Face where they are going; once (nearly) stopped, face what they came to look at.
        Vector3 face = _currentSpeed > 0.2f ? to : (lookTarget - pos);
        face.y = 0f;
        if (face.sqrMagnitude > 0.01f && _initialForwardFlat.sqrMagnitude > 0.0001f)
        {
            float wantYaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
            if (!_headingSet) { _heading = wantYaw; _headingSet = true; }
            _heading = Mathf.MoveTowardsAngle(_heading, wantYaw, Mathf.Max(lookTurnSpeed, 240f) * dt);

            float fromYaw = Mathf.Atan2(_initialForwardFlat.x, _initialForwardFlat.z) * Mathf.Rad2Deg;
            float turn = Mathf.DeltaAngle(fromYaw, _heading)
                       + (reverseDirection ? 180f : 0f)
                       + facingYawOffset;
            transform.rotation = Quaternion.AngleAxis(turn, Vector3.up) * _initialRotation;
        }
    }

    /// <summary>Half the width of the model across its direction of travel.</summary>
    public float HalfWidth { get; private set; } = 1f;

    float MeasureHalfWidth()
    {
        var rs = GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return 1f;
        Bounds b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        Vector3 e = b.extents;
        return Mathf.Clamp(Mathf.Min(e.x, e.z), 0.2f, 4f);
    }

    float MeasureHalfLength()
    {
        var rs = GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return 2.2f;

        Bounds b = default; bool any = false;
        foreach (var r in rs)
        {
            if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        if (!any) return 2.2f;

        // The longer horizontal side: a vehicle is longer than it is wide, whatever axis its
        // import left "forward" on.
        Vector3 e = b.extents;
        return Mathf.Clamp(Mathf.Max(e.x, e.z), 0.2f, 12f);
    }

    /// <summary>Jump to a distance along the path (scripted events).</summary>
    public void Teleport(float distance)
    {
        if (path == null) return;
        Distance = Mathf.Repeat(distance, path.Length);
        startDistance = Distance;
    }

    /// <summary>Called every frame with the speed actually used. Override to drive an Animator.</summary>
    protected virtual void AfterMove(float speed) { }

    /// <summary>
    /// Nudges along the path until there is room.
    ///
    /// Two people standing on the same spot both snap to the same distance, both then read a
    /// gap of zero to each other, and both stand there for ever waiting for the other to move.
    /// Shuffling one of them forward at startup is the whole fix.
    /// </summary>
    void SpreadFromNeighbours()
    {
        if (path == null) return;

        float length = path.Length;
        if (length <= 0.01f) return;

        float step = Mathf.Max(0.5f, path.minGap);

        for (int attempt = 0; attempt < 32; attempt++)
        {
            bool clash = false;

            foreach (var other in path.Drivers)
            {
                if (other == null || other == this) continue;

                float gap = Mathf.Abs(Mathf.DeltaAngle(
                    Distance / length * 360f, other.Distance / length * 360f)) / 360f * length;

                if (gap < step * 0.9f) { clash = true; break; }
            }

            if (!clash) break;
            Distance = Mathf.Repeat(Distance + step, length);
        }

        startDistance = Distance;
    }

    /// <summary>
    /// The direction this model considers "forwards", measured once at startup.
    ///
    /// A Blender export rotated -90 on X has transform.forward pointing at the sky, and its real
    /// heading on transform.up. Checking which one lies flat picks the right axis without
    /// needing to know how the asset was made.
    /// </summary>
    Vector3 MeasureForward()
    {
        Vector3 f = transform.forward; f.y = 0f;
        if (f.sqrMagnitude > 0.05f) return f.normalized;

        f = transform.up; f.y = 0f;
        if (f.sqrMagnitude > 0.05f) return f.normalized;

        f = transform.right; f.y = 0f;
        return f.sqrMagnitude > 0.05f ? f.normalized : Vector3.forward;
    }

    /// <summary>
    /// Ground height under a point, ignoring this object's own colliders.
    ///
    /// RaycastAll rather than Raycast because a character with a collider on it would otherwise
    /// find itself first and stand on its own head, drifting upwards a little every frame.
    /// </summary>
    float GroundHeight(Vector3 at)
    {
        Vector3 origin = at + Vector3.up * Mathf.Max(0.5f, groundProbeHeight);
        float distance = Mathf.Max(1f, groundProbeHeight + groundProbeDepth);

        var hits = Physics.RaycastAll(origin, Vector3.down, distance, ~0,
                                      QueryTriggerInteraction.Ignore);

        float best = float.NegativeInfinity;

        foreach (var h in hits)
        {
            if (h.transform == transform || h.transform.IsChildOf(transform)) continue;
            if (h.point.y > best) best = h.point.y;
        }

        // Nothing under us - a gap in the colliders, or the path runs off the map. Falling back
        // to the path's own height is better than dropping the model to y = 0.
        return float.IsNegativeInfinity(best) ? at.y : best;
    }

    /// <summary>The highest ground under a point that is at most 'maxUp' above where they stand now.</summary>
    float GroundHeightNear(Vector3 at, float feetY, float maxUp)
    {
        Vector3 origin = new Vector3(at.x, feetY + maxUp + 0.05f, at.z);
        var hits = Physics.RaycastAll(origin, Vector3.down, maxUp + 0.05f + Mathf.Max(1f, groundProbeDepth), ~0,
                                      QueryTriggerInteraction.Ignore);
        float best = float.NegativeInfinity;
        foreach (var h in hits)
        {
            if (h.transform == transform || h.transform.IsChildOf(transform)) continue;
            if (h.collider.GetComponentInParent<RoadDriver>() != null) continue;     // cars, other people
            if (h.point.y > best) best = h.point.y;
        }
        return float.IsNegativeInfinity(best) ? feetY : best;
    }

    /// <summary>Distance from the pivot down to the lowest point of the model's geometry.</summary>
    float MeasurePivotAboveFeet()
    {
        var renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return 0f;

        float lowest = float.PositiveInfinity;

        // A person sitting in a car is not part of the car: before his Animator has run, his
        // skinned mesh still has its standing bind pose, legs reaching well below the wheels,
        // and measuring it lifted the whole car off the road. Skip anything animated under us
        // (unless that is all there is, e.g. a walking character).
        var own = GetComponent<Animator>();
        bool anyCarPart = false;
        foreach (var r in renderers)
            if (r != null && r.enabled && !IsPassenger(r, own)) { anyCarPart = true; break; }

        foreach (var r in renderers)
        {
            if (r == null || !r.enabled) continue;
            if (anyCarPart && IsPassenger(r, own)) continue;
            if (r.bounds.min.y < lowest) lowest = r.bounds.min.y;
        }

        if (float.IsPositiveInfinity(lowest)) return 0f;

        // Clamped: a wildly wrong value here would launch the model into the sky, and no human
        // model has its pivot three metres above its feet.
        return Mathf.Clamp(transform.position.y - lowest, 0f, 3f);
    }

    bool IsPassenger(Renderer r, Animator own)
    {
        var a = r.GetComponentInParent<Animator>();
        if (a != null && a != own && a.transform != transform) return true;
        return own == null && r is SkinnedMeshRenderer && GetComponentsInChildren<MeshRenderer>().Length > 0;
    }

    /// <summary>
    /// Put the vehicle exactly where (and facing how) this script would put it at 'distance' -
    /// used in the Editor so what is placed before Play is where Play starts.
    /// </summary>
    public void PlaceAtDistance(float distance)
    {
        if (path == null) return;
        float cur = NearestOnPath(transform.position);
        float off = keepPlacedFacing ? PlacedFacingOffset(cur) : facingYawOffset;

        distance = Mathf.Repeat(distance, path.Length);
        path.Sample(distance, out Vector3 pos, out Vector3 dir);
        float lift = autoGroundOffset ? MeasurePivotAboveFeet() : groundOffset;
        if (stickToGround) pos.y = GroundHeight(pos) + lift;

        Vector3 f0 = MeasureForward();
        if (dir.sqrMagnitude > 0.0001f && f0.sqrMagnitude > 0.0001f)
        {
            float fromYaw = Mathf.Atan2(f0.x, f0.z) * Mathf.Rad2Deg;
            float toYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float turn = Mathf.DeltaAngle(fromYaw, toYaw) + (reverseDirection ? 180f : 0f) + off;
            transform.rotation = Quaternion.AngleAxis(turn, Vector3.up) * transform.rotation;
        }
        transform.position = pos;
        startDistance = distance;
        Distance = distance;
    }

    /// <summary>Hand over at a given speed (after being driven by a script), so it does not
    /// start again from a standstill.</summary>
    public void SetCurrentSpeed(float v) { _currentSpeed = Mathf.Max(0f, v); }

    /// <summary>Called by the build tool so the car starts where it already stands.</summary>
    public void SnapToNearest()
    {
        if (path is TrafficRoad road) startDistance = road.NearestDistance(transform.position);
        else if (path is TrafficRing ring) startDistance = ring.NearestDistance(transform.position);
        else if (path is TrafficWalkway way) startDistance = way.NearestDistance(transform.position);
    }

    /// <summary>
    /// The offset that keeps the model's current heading when the root is turned to face the
    /// path. RoadDriver turns the ROOT's forward onto the path direction; if the model inside the
    /// root was authored at some other angle, this is that angle.
    /// </summary>
    float PlacedFacingOffset(float distance)
    {
        path.Sample(distance, out _, out Vector3 dir);
        Vector3 fwd = MeasureForward();

        float pathYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        float rootYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;

        float offset = Mathf.DeltaAngle(pathYaw, rootYaw);
        if (reverseDirection) offset = Mathf.DeltaAngle(0f, offset - 180f);
        return offset;
    }

    /// <summary>Editor helper: bake the placed heading into Facing Yaw Offset now.</summary>
    [ContextMenu("Use Placed Facing (set Facing Yaw Offset from Scene view)")]
    public void UsePlacedFacing()
    {
        if (path == null) { Debug.LogWarning($"[RoadDriver] '{name}': assign a path first.", this); return; }
#if UNITY_EDITOR
        UnityEditor.Undo.RecordObject(this, "Use Placed Facing");
#endif
        SnapToNearest();
        facingYawOffset = PlacedFacingOffset(startDistance);
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
        Debug.Log($"[RoadDriver] '{name}': Facing Yaw Offset = {facingYawOffset:0.#}", this);
    }

    [ContextMenu("Face The Other Way")]
    public void FaceTheOtherWay()
    {
        reverseDirection = !reverseDirection;
    }

    void OnDrawGizmosSelected()
    {
        // The facing arrow is always useful, so it is not behind the debug toggle - it is the
        // only way to see which way a parked model thinks it is pointing.
        Vector3 origin = transform.position + Vector3.up * 1.5f;

        Vector3 facing = Application.isPlaying
            ? transform.rotation * Quaternion.Inverse(_initialRotation) * _initialForwardFlat
            : Quaternion.AngleAxis((reverseDirection ? 180f : 0f) + facingYawOffset, Vector3.up)
              * MeasureForward();

        facing.y = 0f;
        if (facing.sqrMagnitude > 0.0001f)
        {
            facing.Normalize();

            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(origin, origin + facing * 6f);

            Vector3 side = new Vector3(facing.z, 0f, -facing.x);
            Gizmos.DrawLine(origin + facing * 6f, origin + facing * 4f + side * 1.2f);
            Gizmos.DrawLine(origin + facing * 6f, origin + facing * 4f - side * 1.2f);
        }

        if (!drawGizmo || path == null) return;

        path.Sample(Application.isPlaying ? Distance : startDistance,
                    out Vector3 p, out Vector3 d);

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(p, 2f);
        Gizmos.DrawLine(p, p + d * 5f);
    }
}
