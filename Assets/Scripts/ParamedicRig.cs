using UnityEngine;

/// <summary>
/// Corrections on top of the paramedic's baked clip (em.fbx), applied after the Animator:
///
/// 1. Arm clearance. While he walks with the bag (about 13.5-16 s) his free hand swings behind
///    his back and into his body, and again after he lays the boy down (47.5-49 s). Any hand
///    that goes behind the torso, or into it, is swung back out to the side by turning the upper
///    arm. A hand holding the bag is left alone so the bag stays in it.
///
/// 2. Reach. The first aid was animated with the patient about 30 cm higher than the boy really
///    lies, so the hands would float above his chest. While a hand is working at the chest or
///    face, it is pulled down to the boy's real chest/face - bending the spine forward when the
///    arms alone cannot reach - and the oxygen mask is moved onto his real face.
///
/// Added by AmbulanceResponse. Runs before BoyCarry, which reads the corrected hands.
/// </summary>
[DefaultExecutionOrder(1150)]
public class ParamedicRig : MonoBehaviour
{
    [Header("Arms")]
    [Tooltip("When the clip puts his hands behind his back (it does whenever he stands still), " +
             "let the arms hang relaxed at his sides instead.")]
    public bool relaxArms = true;
    [Tooltip("How far behind the chest a hand may go before the arm is relaxed (rig metres).")]
    public float behindLimit = -0.03f;
    [Tooltip("The clip crosses his arms when he pulls the stretcher (left hand on the right handle). " +
             "Put each hand on the handle on its own side.")]
    public bool uncrossArms = true;

    [Tooltip("Swing his elbows out from his body when they press into it (degrees, 0 = off). " +
             "The hand stays where it is, only the elbow moves. Can be changed in Play.")]
    [Range(0f, 60f)] public float elbowOut = 25f;
    [Tooltip("A hand that goes inside his own body is pushed out to the side by straightening the arm.")]
    public bool handsOutOfBody = true;
    [Tooltip("Extra room beside his body for the hand carrying the bag, rig metres. Raise it if the arm still goes into him while he carries it.")]
    [Range(0f, 0.4f)] public float kitClearance = 0.16f;
    [Tooltip("Half the width of his chest/belly in rig metres - the zone his hands and elbows keep out of.")]
    [Range(0.08f, 0.3f)] public float bodyHalfWidth = 0.17f;

    [Header("Kneeling beside the boy")]
    [Tooltip("While he kneels by the boy, move him sideways just enough that his knees and feet stay off the boy.")]
    public bool keepLegsOffBoy = true;
    [Tooltip("Room kept between his legs and the middle of the boy's body, metres.")]
    [Range(0.1f, 0.8f)] public float legClearance = 0.38f;

    [Header("The bag during the first aid")]
    [Tooltip("The clip sets the bag down where the boy's body is. Put it on the road beside his head instead.")]
    public bool kitBesideHead = true;
    [Tooltip("How far past the top of his head the bag goes, metres.")]
    [Range(0f, 1.5f)] public float kitPastHead = 0.55f;
    [Tooltip("How far to the paramedic's side of his head the bag goes, metres.")]
    [Range(-1f, 1f)] public float kitToSide = 0.35f;

    [Header("Picking the bag up off the road")]
    [Tooltip("Instead of the clip's deep squat (which put his legs under the road), he keeps his " +
             "legs nearly straight, feet on the road, and bends his back forward to reach the bag.")]
    public bool bendToPickUp = true;
    [Tooltip("Most his back bends forward to reach the bag, degrees.")]
    [Range(30f, 95f)] public float maxPickBend = 80f;
    [Tooltip("How straight his legs stay while he bends: 1 = standing height, 0.8 = knees a little bent.")]
    [Range(0.6f, 1f)] public float pickLegStraightness = 0.9f;
    [Tooltip("Seconds before the grab that he starts bending down.")]
    [Range(0.3f, 2f)] public float pickupLead = 0.9f;
    [Tooltip("Seconds after the grab that he takes to stand back up.")]
    [Range(0.3f, 2f)] public float pickupRise = 0.7f;
    [Tooltip("Clip second at which his hand closes on the bag. 0 = measured from the clip.")]
    public float pickupGrabTime = 0f;

    [Header("Carrying the bag")]
    [Tooltip("Whenever he carries the bag, it never goes into his body or legs: his arm holds it " +
             "out to the side, hanging, like a real person carrying a heavy bag.")]
    public bool bagClearOfBody = true;
    [Tooltip("Gap kept between the bag and his leg, rig metres.")]
    [Range(0f, 0.2f)] public float bagGap = 0.05f;

    [Header("Ground")]
    [Tooltip("His knees, feet and toes never go below the road - the whole body is lifted instead.")]
    public bool keepAboveGround = true;

    [Header("Reach to the real patient")]
    public bool reachPatient = true;
    public Transform victim;
    [Tooltip("Clip seconds: hands are corrected only between these (the first aid).")]
    public float reachFrom = 16.0f, reachTo = 43.2f;
    [Tooltip("Height of his hands above the boy's belly bone during the first aid, metres. " +
             "Raise it if the hands sink into the boy, lower it if they float. Can be changed in Play.")]
    [Range(0f, 0.8f)] public float cprHandHeight = 0.24f;
    [Tooltip("Where on his body the CPR goes: 0 = hips, 0.5 = stomach, 1 = chest.")]
    [Range(0f, 1f)] public float cprPoint = 0.15f;
    [Tooltip("Moves the compressions from the hips towards his head (metres). Raise it to press higher on the stomach, lower it to go towards the hips.")]
    [Range(-0.3f, 0.6f)] public float handsOnBelly = 0.25f;
    [Tooltip("Most he bends forward at the waist to reach, degrees.")]
    [Range(0f, 40f)] public float maxBend = 20f;

    [Header("Oxygen mask on the face")]
    [Tooltip("How far in front of the head bone the mask sits (out of the face), metres.")]
    [Range(0f, 0.4f)] public float maskOut = 0.15f;
    [Tooltip("How far down from the head bone towards the chin, metres.")]
    [Range(-0.2f, 0.3f)] public float maskDown = 0.05f;

    // In em.fbx local space (rig metres), measured from the clip.
    public static readonly Vector3 kChest = new Vector3(1.40f, 0.46f, 5.58f);
    public static readonly Vector3 kFace = new Vector3(1.46f, 0.41f, 5.73f);

    Animator _anim;
    Transform _hips, _spine, _spine2, _neck, _lSh, _rSh, _lToe, _lFoot;
    Transform[] _arm = new Transform[2], _fore = new Transform[2], _hand = new Transform[2];
    Transform[] _upLeg = new Transform[2], _leg = new Transform[2], _foot = new Transform[2], _toe = new Transform[2];
    Transform _kit, _mask;
    Transform _vChest, _vHead;
    float[] _relaxW = new float[2];
    float _reachW, _flexW;

    void Start()
    {
        _anim = GetComponent<Animator>();
        _hips = RootSpace.Find(transform, "hips");
        _spine = RootSpace.Find(transform, ":spine");
        _spine2 = RootSpace.Find(transform, "spine2");
        _spine1 = RootSpace.Find(transform, "spine1");
        _neck = RootSpace.Find(transform, "neck");
        _lSh = RootSpace.Find(transform, "leftshoulder");
        _rSh = RootSpace.Find(transform, "rightshoulder");
        _lFoot = RootSpace.Find(transform, "leftfoot");
        _lToe = RootSpace.Find(transform, "lefttoebase");
        string[] side = { "left", "right" };
        for (int i = 0; i < 2; i++)
        {
            _arm[i] = RootSpace.Find(transform, side[i] + "arm");
            _fore[i] = RootSpace.Find(transform, side[i] + "forearm");
            _hand[i] = RootSpace.Find(transform, side[i] + "hand");
        }
        _kit = transform.Find("KIT");
        _mask = transform.Find("FA_mask");
        if (victim != null)
        {
            _vChest = RootSpace.Find(victim, ":spine");      // belly, not under the chin
            _vHead = RootSpace.Find(victim, ":head");
        }
        if (_hips == null || _neck == null || _hand[0] == null) { enabled = false; return; }
        for (int i = 0; i < 2; i++)
        {
            string sd = i == 0 ? "left" : "right";
            _upLeg[i] = RootSpace.Find(transform, sd + "upleg");
            _leg[i] = RootSpace.Find(transform, sd + "leg");
            _foot[i] = RootSpace.Find(transform, sd + "foot");
            _toe[i] = RootSpace.Find(transform, sd + "toebase");
        }
        SampleKitRest();
    }

    // Where the clip has the bag once it is set down (rig space), and how far its pivot sits
    // above its bottom - measured once from the clip.
    bool _haveKitRest;
    Vector3 _kitRestLocal;
    float _kitBottomOff;
    const float kKitRestTime = 18f;

    void SampleKitRest()
    {
        if (_kit == null || _anim == null || _anim.runtimeAnimatorController == null) return;
        var clips = _anim.runtimeAnimatorController.animationClips;
        if (clips == null || clips.Length == 0) return;
        var clip = clips[0];
        float now = ClipTime;
        clip.SampleAnimation(gameObject, kKitRestTime);
        _kitRestLocal = transform.InverseTransformPoint(_kit.position);
        var r = _kit.GetComponentInChildren<Renderer>();
        _kitBottomOff = r != null ? _kit.position.y - r.bounds.min.y : 0f;

        // When does his hand close on the bag again (after laying the boy down)? The first moment
        // the clip's bag starts to move off the spot where it has been lying.
        _grabT = pickupGrabTime;
        if (_grabT <= 0f)
        {
            clip.SampleAnimation(gameObject, kPickSearchFrom);
            Vector3 rest = transform.InverseTransformPoint(_kit.position);
            for (float s = kPickSearchFrom; s <= Mathf.Min(clip.length, kPickSearchTo); s += 1f / 30f)
            {
                clip.SampleAnimation(gameObject, s);
                if ((transform.InverseTransformPoint(_kit.position) - rest).magnitude > 0.03f) { _grabT = s; break; }
            }
        }

        clip.SampleAnimation(gameObject, now);           // put this frame's pose back
        _haveKitRest = true;
    }

    const float kPickSearchFrom = 46.6f, kPickSearchTo = 53f;
    float _grabT;

    public float ClipTime
    {
        get
        {
            if (_anim == null) return 0f;
            var s = _anim.GetCurrentAnimatorStateInfo(0);
            return Mathf.Min(s.normalizedTime, 1f) * s.length;
        }
    }

    void LateUpdate()
    {
        float t = ClipTime;
        // Where the clip has the bag and the mask this frame, before anything here moves them.
        bool haveKit = _kit != null && _mask != null;
        Vector3 clipKitPos = haveKit ? _kit.position : Vector3.zero, clipMaskPos = haveKit ? _mask.position : Vector3.zero;
        Quaternion clipKitRot = haveKit ? _kit.rotation : Quaternion.identity;

        if (keepLegsOffBoy && victim != null) KneelClear(t);
        if (keepAboveGround) AboveGround();
        if (bendToPickUp) PickupPosture(t);
        if (reachPatient && _vChest != null) Reach(t);
        if (uncrossArms) UncrossArms();
        if (relaxArms) RelaxArms();
        if (kitBesideHead) KitOffBoy(t);
        if (bendToPickUp) PickupHand();
        if (bagClearOfBody) KitClearOfBody();
        if (handsOutOfBody) HandsOutOfBody(t);
        if (elbowOut > 0f) ElbowsOut();

        // The mask is in the bag in the clip (before he takes it out, and after he puts it back):
        // wherever the bag has been moved to, the mask goes with it, inside it.
        if (haveKit && (clipMaskPos - clipKitPos).magnitude < 0.22f * Mathf.Abs(transform.lossyScale.y))
        {
            Quaternion turn = _kit.rotation * Quaternion.Inverse(clipKitRot);
            _mask.position = _kit.position + turn * (clipMaskPos - clipKitPos);
            _mask.rotation = turn * _mask.rotation;
        }
    }

    // ------------------------------------------------------------------ 0b. never under the road

    Transform _spine1;
    float _groundRaise;

    /// <summary>
    /// The clip was made on a floor that is not this road, so in places his knees and feet went
    /// into it. If any of them is below the road, lift his whole body by just that much. Raised
    /// at once (no sinking first), lowered back gently.
    /// </summary>
    void AboveGround()
    {
        float lowest = float.MaxValue;
        for (int i = 0; i < 2; i++)
        {
            if (_leg[i] != null) lowest = Mathf.Min(lowest, _leg[i].position.y);
            if (_foot[i] != null) lowest = Mathf.Min(lowest, _foot[i].position.y);
            if (_toe[i] != null) lowest = Mathf.Min(lowest, _toe[i].position.y);
        }
        if (lowest == float.MaxValue) return;

        float ground = GroundUnder(_hips.position, float.MinValue);
        if (ground == float.MinValue) return;

        float need = Mathf.Max(0f, ground + 0.01f - lowest);
        _groundRaise = need > _groundRaise ? need : Mathf.MoveTowards(_groundRaise, need, Time.deltaTime * 0.6f);
        if (_groundRaise > 0.0005f) _hips.position += Vector3.up * _groundRaise;
    }

    // ------------------------------------------------------------------ 0c. bending down for the bag

    float _pickW, _kitHeight = -1f;
    int _pickHand = -1;

    /// <summary>
    /// The clip squats all the way down to pick the bag up, and on this road that put his legs
    /// underground. Instead: his feet stay where they are on the road, his legs stay nearly
    /// straight, and his back bends forward as far as it takes for his hand to reach the bag
    /// handle. Eases in before the grab and back up after it. The hand itself is placed on the
    /// bag in PickupHand, after the bag has been positioned for this frame.
    /// </summary>
    void PickupPosture(float t)
    {
        _pickW = 0f;
        if (_grabT <= 0f || _kit == null || _upLeg[0] == null || _upLeg[1] == null) return;

        float wIn = RootSpace.Smooth01(Mathf.InverseLerp(_grabT - pickupLead, _grabT - pickupLead * 0.3f, t));
        float wOut = 1f - RootSpace.Smooth01(Mathf.InverseLerp(_grabT + 0.1f, _grabT + pickupRise, t));
        float w = Mathf.Min(wIn, wOut);
        if (w <= 0.001f) { _pickHand = -1; return; }
        _pickW = w;

        Transform R = transform;

        // Where the bag handle is: on the road where it was put down.
        if (_kitR == null) _kitR = _kit.GetComponentInChildren<Renderer>();
        if (_kitHeight < 0f) _kitHeight = _kitR != null ? _kitR.bounds.size.y : 0.25f;
        Vector3 bag = _kitSpotSet ? _kitSpot : _kit.position;
        Vector3 handle = new Vector3(bag.x, bag.y - _kitBottomOff + _kitHeight + 0.02f, bag.z);

        // Which hand picks it up: the one nearer the bag when the bend starts.
        if (_pickHand < 0)
        {
            float best = float.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                if (_hand[i] == null) continue;
                float d = (Flat(_hand[i].position) - Flat(handle)).sqrMagnitude;
                if (d < best) { best = d; _pickHand = i; }
            }
        }
        int g = _pickHand;
        if (g < 0 || _arm[g] == null || _fore[g] == null) return;

        // 1. Feet stay planted; hips come up so the legs are nearly straight.
        var footPos = new Vector3[2]; var footRot = new Quaternion[2];
        for (int i = 0; i < 2; i++)
        {
            if (_foot[i] == null) continue;
            footPos[i] = _foot[i].position;
            footRot[i] = RootSpace.Rot(R, _foot[i]);
        }
        float ground = GroundUnder(_hips.position, Mathf.Min(footPos[0].y, footPos[1].y) - 0.08f);
        float ankle = Mathf.Min(footPos[0].y, footPos[1].y) - ground;
        float legLen = 0f;
        for (int i = 0; i < 2; i++)
            if (_leg[i] != null && _foot[i] != null)
                legLen = Mathf.Max(legLen, (_upLeg[i].position - _leg[i].position).magnitude +
                                           (_leg[i].position - _foot[i].position).magnitude);
        float wantH = ground + ankle + legLen * pickLegStraightness;
        float raise = Mathf.Max(0f, wantH - _hips.position.y) * w;
        if (raise > 0f) _hips.position += Vector3.up * raise;

        for (int i = 0; i < 2; i++)
        {
            if (_upLeg[i] == null || _leg[i] == null || _foot[i] == null) continue;
            RootSpace.TwoBoneIK(R, _upLeg[i], _leg[i], _foot[i], RootSpace.Pos(R, footPos[i]), 1f);
            RootSpace.SetRot(R, _foot[i], footRot[i]);
        }

        // 2. Bend the back forward until the shoulder is an arm's length above the handle.
        float armLen = (_arm[g].position - _fore[g].position).magnitude + (_fore[g].position - _hand[g].position).magnitude;
        Vector3 torso = _arm[g].position - _hips.position;
        float L = torso.magnitude;
        if (L < 0.05f || armLen < 0.05f) return;
        float phi0 = Vector3.Angle(Vector3.up, torso);
        float above = _hips.position.y - handle.y;
        float cos = Mathf.Clamp((armLen * 0.88f - above) / L, -1f, 1f);
        float phi = Mathf.Acos(cos) * Mathf.Rad2Deg;
        float bend = Mathf.Clamp(phi - phi0, 0f, maxPickBend) * w;
        if (bend < 0.1f) return;

        Vector3 toBag = Flat(handle - _hips.position);
        if (toBag.sqrMagnitude < 1e-4f) { Torso(out _, out _, out _, out Vector3 fwRS); toBag = Flat(R.TransformDirection(fwRS)); }
        Vector3 upRS = RootSpace.Dir(R, Vector3.up), fRS = RootSpace.Dir(R, toBag.normalized);
        Vector3 axis = Vector3.Cross(upRS, fRS);
        if (axis.sqrMagnitude < 1e-6f) return;
        axis.Normalize();
        if (Vector3.Dot(Quaternion.AngleAxis(10f, axis) * upRS, fRS) < 0f) axis = -axis;   // tips towards the bag

        // Spread over the spine so it curves rather than folds at one joint.
        Transform[] spine = { _spine, _spine1, _spine2 };
        float[] share = { 0.45f, 0.3f, 0.25f };
        float missing = 0f;
        for (int k = 0; k < 3; k++) if (spine[k] == null) missing += share[k];
        for (int k = 0; k < 3; k++)
        {
            if (spine[k] == null) continue;
            float a = bend * share[k] / Mathf.Max(0.01f, 1f - missing);
            RootSpace.SetRot(R, spine[k], Quaternion.AngleAxis(a, axis) * RootSpace.Rot(R, spine[k]));
        }
        // Head: looks down at the bag, a little less tipped than the back.
        if (_neck != null)
            RootSpace.SetRot(R, _neck, Quaternion.AngleAxis(-bend * 0.25f, axis) * RootSpace.Rot(R, _neck));
    }

    /// <summary>Puts the picking hand on the bag handle, wherever the bag is this frame.</summary>
    void PickupHand()
    {
        if (_pickW <= 0.001f || _pickHand < 0 || _kit == null) return;
        int g = _pickHand;
        if (_arm[g] == null || _fore[g] == null || _hand[g] == null) return;

        if (_kitR == null) _kitR = _kit.GetComponentInChildren<Renderer>();
        Vector3 target;
        if (_kitR != null)
        {
            var b = _kitR.bounds;
            target = new Vector3(b.center.x, b.max.y + 0.07f, b.center.z);   // wrist just above the handle
        }
        else target = _kit.position + Vector3.up * 0.2f;

        Transform R = transform;
        Quaternion q = RootSpace.Rot(R, _hand[g]);
        RootSpace.TwoBoneIK(R, _arm[g], _fore[g], _hand[g], RootSpace.Pos(R, target), RootSpace.Smooth01(_pickW));
        RootSpace.SetRot(R, _hand[g], q);
    }

    // ------------------------------------------------------------------ 0d. the bag clear of his body

    float[] _bagClearW = new float[2];

    /// <summary>
    /// While a hand holds the bag off the ground, measure how far the bag sits from his body's
    /// centre line sideways. If its near side is inside his hip/leg (plus a small gap), swing
    /// that arm out by exactly the missing amount and move the bag with the hand - so it hangs
    /// beside his leg instead of through it. Eases in fast (no visible overlap), out slowly.
    /// </summary>
    void KitClearOfBody()
    {
        if (_kit == null) return;
        if (_kitR == null) _kitR = _kit.GetComponentInChildren<Renderer>();
        if (_kitR == null) return;

        Transform R = transform;
        float scale = Mathf.Max(0.01f, Mathf.Abs(R.lossyScale.y));
        Torso(out _, out Vector3 up, out Vector3 lr, out Vector3 fw);
        Vector3 hipsRS = RootSpace.Pos(R, _hips);

        Bounds kb = _kitR.bounds;
        Vector3 kitRS = RootSpace.Pos(R, kb.center);
        // Half the bag's size across his body, in rig metres.
        float halfLat = Mathf.Max(kb.extents.x, kb.extents.z) / scale * 0.75f;

        // Off the ground only - setting it down / picking it up is handled elsewhere.
        float ground = GroundUnder(kb.center, kb.min.y);
        float lifted = Mathf.InverseLerp(0.05f, 0.25f, kb.min.y - ground);

        for (int i = 0; i < 2; i++)
        {
            if (_arm[i] == null || _fore[i] == null || _hand[i] == null) continue;
            Vector3 outSide = i == 0 ? lr : -lr;
            Vector3 handRS = RootSpace.Pos(R, _hand[i]);

            // Is this the hand holding it? (close to the bag's top)
            Vector3 topRS = RootSpace.Pos(R, new Vector3(kb.center.x, kb.max.y, kb.center.z));
            bool holding = (handRS - topRS).magnitude < 0.3f && lifted > 0f;

            float lat = Vector3.Dot(kitRS - hipsRS, outSide);          // bag centre, out from his middle
            float need = bodyHalfWidth + bagGap + halfLat - lat;        // > 0: bag is in his body/leg
            float want = holding && need > 0f ? 1f : 0f;
            _bagClearW[i] = Mathf.MoveTowards(_bagClearW[i], want, Time.deltaTime * (want > 0f ? 12f : 1.5f));
            if (_bagClearW[i] <= 0.001f || !holding) continue;

            float push = Mathf.Max(0f, need) * lifted;
            if (push <= 0.0005f) continue;

            Vector3 before = _hand[i].position;
            Quaternion q = RootSpace.Rot(R, _hand[i]);
            RootSpace.TwoBoneIK(R, _arm[i], _fore[i], _hand[i], handRS + outSide * push, RootSpace.Smooth01(_bagClearW[i]));
            RootSpace.SetRot(R, _hand[i], q);

            // The bag (and the mask inside it) stays in the hand.
            Vector3 moved = _hand[i].position - before;
            _kit.position += moved;
            if (_mask != null && (_mask.position - kb.center).magnitude < kb.extents.magnitude * 1.5f)
                _mask.position += moved;
            break;                                                      // one hand carries it
        }
    }

    // ------------------------------------------------------------------ 1b. legs off the boy

    float _kneelW;

    /// <summary>
    /// The clip kneels him where its own patient was; on our boy one shin/knee ends up lying
    /// over the boy's legs. While he works there, slide his whole body sideways (away from the
    /// boy) by just as much as his legs overlap - the hands are then pulled back onto the boy by
    /// the reach.
    /// </summary>
    void KneelClear(float t)
    {
        Transform bh = RootSpace.Find(victim, "hips"), head = _vHead, lf = RootSpace.Find(victim, "leftfoot"), rf = RootSpace.Find(victim, "rightfoot");
        if (bh == null || head == null) return;
        Vector3 feet = (lf != null && rf != null) ? (lf.position + rf.position) * 0.5f : bh.position;
        Vector3 a = Flat(feet), b = Flat(head.position);
        Vector3 ab = b - a; float len = ab.magnitude;
        if (len < 0.1f) return;
        Vector3 along = ab / len;
        Vector3 side = Vector3.Cross(Vector3.up, along);
        if (Vector3.Dot(Flat(_hips.position) - a, side) < 0f) side = -side;      // towards him

        bool window = t > 15.5f && t < 43.0f;
        float need = 0f;
        if (window)
        {
            for (int i = 0; i < 2; i++)
            {
                Transform[] chain = { _upLeg[i], _leg[i], _foot[i], _toe[i] };
                for (int k = 0; k < chain.Length; k++)
                {
                    if (chain[k] == null) continue;
                    Check(chain[k].position);
                    if (k + 1 < chain.Length && chain[k + 1] != null) Check((chain[k].position + chain[k + 1].position) * 0.5f);
                }
            }
        }
        void Check(Vector3 p)
        {
            Vector3 f = Flat(p) - a;
            float u = Vector3.Dot(f, along);
            if (u < -0.15f || u > len + 0.15f) return;                 // past his feet or head
            float lat = Vector3.Dot(f, side);
            need = Mathf.Max(need, legClearance - lat);
        }
        need = Mathf.Min(need, 0.7f);
        // Smooth: settle into the kneel, ease out when he gets up.
        _kneelW = Mathf.MoveTowards(_kneelW, need, Time.deltaTime * 0.8f);
        if (_kneelW <= 0.001f) return;
        _hips.position += side * _kneelW;
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // ------------------------------------------------------------------ 2b. the bag off the boy

    float _kitW;
    Renderer _kitR;

    bool _kitSpotSet, _kitPicked;
    Vector3 _kitSpot;
    float _reachKitW;

    void KitOffBoy(float t)
    {
        if (_kit == null || victim == null || _vHead == null) return;
        Transform hips = RootSpace.Find(victim, "hips");
        if (hips == null) return;
        if (_kitR == null) _kitR = _kit.GetComponentInChildren<Renderer>();
        if (t < 1f) { _kitSpotSet = false; _kitPicked = false; }

        float scale = Mathf.Abs(transform.lossyScale.y);
        Vector3 clipKit = _kit.position;               // where the clip has it this frame

        // The spot: past the top of his head, a little to the paramedic's side, on the road.
        // Fixed while he still lies there, so it does not follow him when he is lifted.
        if (t < 43f || !_kitSpotSet)
        {
            Vector3 along = _vHead.position - hips.position; along.y = 0f;
            if (along.sqrMagnitude < 1e-4f) return;
            along.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, along);
            Vector3 me = _hips != null ? _hips.position : transform.position;
            if (Vector3.Dot(me - hips.position, side) < 0f) side = -side;
            Vector3 spot = _vHead.position + along * kitPastHead + side * kitToSide;
            float bottom = _kitR != null ? _kitR.bounds.min.y : clipKit.y;
            float ground = GroundUnder(spot, clipKit.y);
            _kitSpot = _haveKitRest ? new Vector3(spot.x, ground + _kitBottomOff, spot.z)
                                    : new Vector3(spot.x, clipKit.y + (ground - bottom), spot.z);
            _kitSpotYaw = Vector3.SignedAngle(Vector3.forward, along, Vector3.up);
            if (t > 17.5f) _kitSpotSet = true;
        }

        // Setting it down: from when he walks up with it, carry it (and the hand holding it) to
        // the spot, so it is put down right there on the road - not on the boy, and not dropped
        // in from the air afterwards.
        if (_haveKitRest && !_kitPicked && t > 13.5f && t < 17.5f)
        {
            Vector3 restWorld = transform.TransformPoint(_kitRestLocal);
            Vector3 spotC = new Vector3(_kitSpot.x, GroundUnder(_kitSpot, restWorld.y) + _kitBottomOff, _kitSpot.z);
            _kitSpot = spotC;
            float wd = RootSpace.Smooth01(Mathf.InverseLerp(13.5f, 16.3f, t));
            Vector3 delta = (spotC - restWorld) * wd;
            int hg = -1; float hd = float.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                if (_hand[i] == null) continue;
                float dd = (_hand[i].position - clipKit).magnitude / scale;
                if (dd < hd) { hd = dd; hg = i; }
            }
            if (hg >= 0 && hd < 0.45f && _arm[hg] != null && _fore[hg] != null)
            {
                Quaternion q = RootSpace.Rot(transform, _hand[hg]);
                RootSpace.TwoBoneIK(transform, _arm[hg], _fore[hg], _hand[hg], RootSpace.Pos(transform, _hand[hg].position + delta), 1f);
                RootSpace.SetRot(transform, _hand[hg], q);
            }
            _kit.position = clipKit + delta;
            Vector3 kf = _kit.forward; kf.y = 0f;
            if (kf.sqrMagnitude > 1e-4f)
            {
                Vector3 al = Quaternion.AngleAxis(_kitSpotYaw, Vector3.up) * Vector3.forward;
                float yaw = Mathf.Repeat(Vector3.SignedAngle(kf, al, Vector3.up) + 45f, 90f) - 45f;
                _kit.rotation = Quaternion.AngleAxis(yaw * wd, Vector3.up) * _kit.rotation;
            }
            _kitW = wd;
            return;
        }

        // Set down at ~16.5 s; it stays on the road until he comes back for it after laying the
        // boy on the stretcher. Then his hand goes down to where the bag really is and lifts it -
        // the bag never flies up to his hand.
        int g = -1; float gd = float.MaxValue;
        for (int i = 0; i < 2; i++)
        {
            if (_hand[i] == null) continue;
            float d = (_hand[i].position - clipKit).magnitude / scale;
            if (d < gd) { gd = d; g = i; }
        }
        bool settingDown = t < 17.5f && gd < 0.35f;
        // With the bend-down pickup his hands are no longer where the clip has them, so the
        // moment of the grab comes from the clip's timing instead of from hand distance.
        bool grabbed = bendToPickUp && _grabT > 0f ? t >= _grabT : gd < 0.30f;
        if (!_kitPicked && t > 46.0f && grabbed) _kitPicked = true;

        float wantKit = (t > 16.0f && !settingDown && !_kitPicked) ? 1f : 0f;
        _kitW = Mathf.MoveTowards(_kitW, wantKit, Time.deltaTime * (_kitPicked ? 2.2f : 2.5f));

        // The reaching hand: on its way down to pick it up, aim it at the real bag.
        float wantReach = (!_kitPicked && t > 45.5f && g >= 0) ? Mathf.InverseLerp(0.8f, 0.3f, gd) : 0f;
        if (_kitPicked) wantReach = _kitW;              // let go of the correction as the bag rises
        _reachKitW = _kitPicked ? Mathf.Min(_reachKitW, wantReach) : Mathf.MoveTowards(_reachKitW, wantReach, Time.deltaTime * 4f);

        if (_kitW <= 0.001f && _reachKitW <= 0.001f) return;
        float w = RootSpace.Smooth01(_kitW);

        if (_reachKitW > 0.001f && _pickW <= 0.001f && g >= 0 && _arm[g] != null && _fore[g] != null)
        {
            Vector3 bagNow = Vector3.Lerp(clipKit, _kitSpot, w);
            Vector3 handTarget = _hand[g].position + (bagNow - clipKit);
            Quaternion q = RootSpace.Rot(transform, _hand[g]);
            RootSpace.TwoBoneIK(transform, _arm[g], _fore[g], _hand[g], RootSpace.Pos(transform, handTarget), RootSpace.Smooth01(_reachKitW));
            RootSpace.SetRot(transform, _hand[g], q);
        }

        if (_kitW <= 0.001f) return;
        _kit.position = Vector3.Lerp(clipKit, _kitSpot, w);
        // Square to his body, so it does not sit at a random angle.
        Vector3 f = _kit.forward; f.y = 0f;
        if (f.sqrMagnitude > 1e-4f)
        {
            Vector3 along = Quaternion.AngleAxis(_kitSpotYaw, Vector3.up) * Vector3.forward;
            float yaw = Vector3.SignedAngle(f, along, Vector3.up);
            yaw = Mathf.Repeat(yaw + 45f, 90f) - 45f;      // nearest quarter turn
            _kit.rotation = Quaternion.AngleAxis(yaw * w, Vector3.up) * _kit.rotation;
        }
    }

    float _kitSpotYaw;

    float GroundUnder(Vector3 at, float fallback)
    {
        float best = float.MinValue;
        foreach (var h in Physics.RaycastAll(at + Vector3.up * 4f, Vector3.down, 12f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (h.collider.transform.IsChildOf(transform)) continue;
            if (victim != null && h.collider.transform.IsChildOf(victim)) continue;
            if (h.collider.GetComponentInParent<Animator>() != null) continue;
            if (h.collider.GetComponentInParent<RoadDriver>() != null) continue;
            if (h.point.y > best) best = h.point.y;
        }
        return best > float.MinValue ? best : fallback;
    }

    // ------------------------------------------------------------------ 3. arms out of his own body

    bool Busy(float t)
    {
        // Working on the boy (the IK owns the hands) or carrying him (BoyCarry reads the hands).
        return _reachW > 0.01f || (t > 43.0f && t < 47.4f);
    }

    float[] _outW = new float[2];
    [System.NonSerialized] public string debugArms = "";

    /// <summary>A hand inside his chest/belly is moved out to the side of it (arm straightens a bit).</summary>
    void HandsOutOfBody(float t)
    {
        Transform root = transform;
        Torso(out Vector3 sp, out Vector3 up, out Vector3 lr, out Vector3 fw);
        Vector3 kit = _kit != null ? RootSpace.Pos(root, _kit) : new Vector3(1e6f, 0, 0);
        bool busy = Busy(t);
        debugArms = $"busy={busy} reachW={_reachW:0.00}";
        for (int i = 0; i < 2; i++)
        {
            if (_arm[i] == null || _fore[i] == null || _hand[i] == null) continue;
            Vector3 outSide = i == 0 ? lr : -lr;
            Vector3 h = RootSpace.Pos(root, _hand[i]);
            { Vector3 dd = h - sp; debugArms += $" | {(i == 0 ? "L" : "R")} lat={Vector3.Dot(dd, outSide):0.00} f={Vector3.Dot(dd, fw):0.00} u={Vector3.Dot(dd, up):0.00} w={_outW[i]:0.00}"; }
            Vector3 d = h - sp;
            float lat = Vector3.Dot(d, outSide), f = Vector3.Dot(d, fw), u = Vector3.Dot(d, up);
            bool carryWindow = (t > 11.8f && t < 13.5f) || (t > 48.0f && t < 51.3f);
            bool holdingKit = carryWindow && (h - kit).magnitude < 0.35f;
            // Carrying the bag: the bag needs room beside his leg too, so keep that hand further out.
            float clear = holdingKit ? bodyHalfWidth + kitClearance : bodyHalfWidth + 0.06f;
            // A walking arm swings in and out of this zone every step; once caught, it is held
            // out a little longer (wider zone) so it does not flicker in and out of his side.
            float fMax = _outW[i] > 0f ? 0.32f : 0.22f, latMax = _outW[i] > 0f ? clear + 0.04f : clear - 0.02f;
            bool inside = lat < latMax && f > -0.22f && f < fMax && u > -0.8f && u < 0.15f;
            float want = (inside && !busy) ? 1f : 0f;
            _outW[i] = Mathf.MoveTowards(_outW[i], want, Time.deltaTime * (want > 0f ? 8f : 2f));
            if (_outW[i] <= 0.001f) continue;
            // Out to the side of the body, a touch forward; height kept.
            Vector3 target = h + outSide * Mathf.Max(0f, clear - lat) + fw * Mathf.Max(0f, 0.04f - f) * 0.5f;
            Quaternion q = RootSpace.Rot(root, _hand[i]);
            Vector3 before = _hand[i].position;
            RootSpace.TwoBoneIK(root, _arm[i], _fore[i], _hand[i], target, RootSpace.Smooth01(_outW[i]));
            RootSpace.SetRot(root, _hand[i], q);
            // The bag goes with the hand that holds it.
            if (holdingKit && _kit != null) _kit.position += _hand[i].position - before;
        }
    }

    /// <summary>
    /// Elbows tucked into his ribs: turn each upper arm about the shoulder-to-hand line so the
    /// elbow swings outwards. The hand does not move, so grips (stretcher, bag, the boy) hold.
    /// </summary>
    void ElbowsOut()
    {
        Transform root = transform;
        Torso(out Vector3 sp, out Vector3 up, out Vector3 lr, out Vector3 fw);
        for (int i = 0; i < 2; i++)
        {
            if (_arm[i] == null || _fore[i] == null || _hand[i] == null) continue;
            Vector3 outSide = i == 0 ? lr : -lr;
            Vector3 s = RootSpace.Pos(root, _arm[i]), e = RootSpace.Pos(root, _fore[i]), h = RootSpace.Pos(root, _hand[i]);
            Vector3 axis = h - s;
            if (axis.sqrMagnitude < 1e-6f) continue;
            axis.Normalize();
            Vector3 v = (e - s) - axis * Vector3.Dot(e - s, axis);         // elbow off the shoulder-hand line
            if (v.sqrMagnitude < 1e-6f) continue;                           // arm straight: nothing to swing

            // How close the elbow is to his side: full effect when it is in the body, none when clear.
            float elbowLat = Vector3.Dot(e - sp, outSide), elbowF = Vector3.Dot(e - sp, fw);
            float w = Mathf.InverseLerp(bodyHalfWidth + 0.10f, bodyHalfWidth - 0.02f, elbowLat)
                    * Mathf.InverseLerp(0.26f, 0.14f, Mathf.Abs(elbowF));       // only where it is in his body, not out in front
            if (w <= 0.001f) continue;

            Vector3 want = outSide - axis * Vector3.Dot(outSide, axis);
            if (want.sqrMagnitude < 1e-6f) continue;
            float toOut = Vector3.SignedAngle(v, want, axis);
            float ang = Mathf.Clamp(toOut, -elbowOut, elbowOut) * w;
            if (Mathf.Abs(ang) < 0.05f) continue;

            Quaternion qh = RootSpace.Rot(root, _hand[i]);
            RootSpace.SetRot(root, _arm[i], Quaternion.AngleAxis(ang, axis) * RootSpace.Rot(root, _arm[i]));
            RootSpace.SetRot(root, _hand[i], qh);
        }
    }

    // ------------------------------------------------------------------ torso frame (root space)

    void Torso(out Vector3 sp, out Vector3 up, out Vector3 lr, out Vector3 fw)
    {
        Transform root = transform;
        Vector3 hips = RootSpace.Pos(root, _hips);
        sp = RootSpace.Pos(root, _spine2 != null ? _spine2 : _neck);
        up = (RootSpace.Pos(root, _neck) - hips).normalized;
        lr = RootSpace.Pos(root, _lSh) - RootSpace.Pos(root, _rSh);
        lr = (lr - up * Vector3.Dot(lr, up)).normalized;           // points to his left
        fw = Vector3.Cross(up, lr).normalized;
        // Which way is "front": the toes say so.
        if (_lToe != null && _lFoot != null)
        {
            Vector3 toe = RootSpace.Pos(root, _lToe) - RootSpace.Pos(root, _lFoot);
            if (Vector3.Dot(toe, fw) < 0f) fw = -fw;
        }
    }

    float _uncrossW;

    // ------------------------------------------------------------------ 0. crossed arms

    /// <summary>
    /// Both hands in front of him, each on the far side of his body: the arms cross over the chest.
    /// Mirror each hand across his centre line - the stretcher handles are symmetric, so that is
    /// the handle on his own side - and reach it with two-bone IK.
    /// </summary>
    void UncrossArms()
    {
        Transform root = transform;
        if (_hand[0] == null || _hand[1] == null) return;
        Torso(out Vector3 sp, out Vector3 up, out Vector3 lr, out Vector3 fw);
        Vector3 hl = RootSpace.Pos(root, _hand[0]), hr = RootSpace.Pos(root, _hand[1]);
        float ll = Vector3.Dot(hl - sp, lr), lrr = Vector3.Dot(hr - sp, lr);
        bool inFront = Vector3.Dot(hl - sp, fw) > 0.03f && Vector3.Dot(hr - sp, fw) > 0.03f;
        float crossed = inFront ? Mathf.Min(-ll, lrr) : 0f;           // > 0 when both are on the wrong side

        _uncrossW = Mathf.MoveTowards(_uncrossW, Mathf.InverseLerp(0.03f, 0.09f, crossed), Time.deltaTime * 4f);
        if (_uncrossW <= 0.001f) return;
        float w = RootSpace.Smooth01(_uncrossW);

        Vector3 tl = hl - lr * (2f * ll), tr = hr - lr * (2f * lrr);
        Quaternion qL = RootSpace.Rot(root, _hand[0]), qR = RootSpace.Rot(root, _hand[1]);
        RootSpace.TwoBoneIK(root, _arm[0], _fore[0], _hand[0], tl, w);
        RootSpace.TwoBoneIK(root, _arm[1], _fore[1], _hand[1], tr, w);
        RootSpace.SetRot(root, _hand[0], Quaternion.Slerp(RootSpace.Rot(root, _hand[0]), qL, w * 0.5f));
        RootSpace.SetRot(root, _hand[1], Quaternion.Slerp(RootSpace.Rot(root, _hand[1]), qR, w * 0.5f));
    }

    // ------------------------------------------------------------------ 1. relaxed arms

    /// <summary>
    /// The clip holds his hands behind his back whenever he is not doing something with them.
    /// When a hand goes behind the chest (and is not holding the bag), the arm is swung down to
    /// hang at his side, elbow a little bent - a person standing and watching.
    /// </summary>
    void RelaxArms()
    {
        Transform root = transform;
        Torso(out Vector3 sp, out Vector3 up, out Vector3 lr, out Vector3 fw);
        Vector3 kit = _kit != null ? RootSpace.Pos(root, _kit) : new Vector3(1e6f, 0, 0);

        for (int i = 0; i < 2; i++)
        {
            if (_arm[i] == null || _fore[i] == null || _hand[i] == null) continue;
            Vector3 outSide = i == 0 ? lr : -lr;
            Vector3 h = RootSpace.Pos(root, _hand[i]);
            Vector3 d = h - sp;
            float f = Vector3.Dot(d, fw), u = Vector3.Dot(d, up);

            bool holdingKit = (h - kit).magnitude < 0.35f;
            float behind = Mathf.InverseLerp(behindLimit, behindLimit - 0.08f, f);    // 0 in front .. 1 well behind
            float want = holdingKit || u > 0.1f ? 0f : behind;
            _relaxW[i] = Mathf.MoveTowards(_relaxW[i], want, Time.deltaTime * 3f);
            if (_relaxW[i] <= 0.001f) continue;

            float w = RootSpace.Smooth01(_relaxW[i]);
            RootSpace.Aim(root, _arm[i], _fore[i], (-up + outSide * 0.14f + fw * 0.06f).normalized, w);
            RootSpace.Aim(root, _fore[i], _hand[i], (-up + fw * 0.30f + outSide * 0.04f).normalized, w);
        }
    }

    // ------------------------------------------------------------------ 2. reach to the patient

    void Reach(float t)
    {
        Transform root = transform;

        bool window = t > reachFrom && t < reachTo;
        _reachW = Mathf.MoveTowards(_reachW, window ? 1f : 0f, Time.deltaTime * 2f);
        if (_reachW <= 0f) return;

        float H = BoyScale();
        Vector3 chestW = CprPoint(victim, cprPoint) + Vector3.up * cprHandHeight + BodyUp() * handsOnBelly;
        Vector3 faceW = (_vHead != null ? _vHead.position : chestW) + Vector3.up * 0.05f * H;

        // Where the clip thinks the chest and face are, and where they really are - in root space.
        Vector3 rigChest = kChest, rigFace = kFace;
        Vector3 dChest = RootSpace.Pos(root, chestW) - rigChest;
        Vector3 dFace = RootSpace.Pos(root, faceW) - rigFace;

        // The mask: while the clip has it on the face, it is put on the REAL face - over nose and
        // mouth, facing out of the face, nose end towards the top of the head.
        if (_mask != null) FitMask(rigFace);

        // Per hand: how much it is "working on" the chest / face.
        var targets = new Vector3[2];
        var weights = new float[2];
        float needFlex = 0f;

        // Centre the two hands on the stomach point sideways (the clip's compressions sit a bit
        // towards the legs of where our boy's belly is), keep the clip's up/down pumping.
        Vector3 pBelly = RootSpace.Pos(root, chestW);
        Vector3 hc = Vector3.zero; int hn = 0;
        for (int i = 0; i < 2; i++)
            if (_hand[i] != null) { hc += RootSpace.Pos(root, _hand[i]); hn++; }
        if (hn > 0) hc /= hn;
        Vector3 dCentre = pBelly - hc; dCentre.y = dChest.y;

        for (int i = 0; i < 2; i++)
        {
            if (_hand[i] == null) continue;
            Vector3 h = RootSpace.Pos(root, _hand[i]);
            float wc = 1f - Mathf.InverseLerp(0.28f, 0.50f, (hc - rigChest).magnitude);
            // Hands follow the belly only; the clip's face point sits right next to its chest point,
            // so blending the two pulled the compressions up onto the face. The mask alone goes
            // to the face (above).
            float wf = 0f;
            float sum = wc + wf;
            if (sum <= 0.001f) continue;
            Vector3 delta = (dCentre * wc + dFace * wf) / Mathf.Max(1f, sum);
            weights[i] = Mathf.Clamp01(sum) * _reachW;
            targets[i] = h + delta;

            float reach = (RootSpace.Pos(root, _fore[i]) - RootSpace.Pos(root, _arm[i])).magnitude +
                          (h - RootSpace.Pos(root, _fore[i])).magnitude;
            float dist = (targets[i] - RootSpace.Pos(root, _arm[i])).magnitude;
            needFlex = Mathf.Max(needFlex, weights[i] * Mathf.Clamp01((dist - reach * 0.92f) / 0.25f));
        }

        // Bend forward at the lower spine when the arms alone are too short.
        _flexW = Mathf.MoveTowards(_flexW, needFlex, Time.deltaTime * 1.5f);
        if (_flexW > 0.001f && _spine != null)
        {
            Torso(out _, out Vector3 up, out Vector3 lr, out Vector3 fw);
            Quaternion bend = Quaternion.AngleAxis(maxBend * _flexW, lr);
            if (Vector3.Dot(bend * up, fw) < 0f) bend = Quaternion.AngleAxis(-maxBend * _flexW, lr);
            RootSpace.SetRot(root, _spine, bend * RootSpace.Rot(root, _spine));
        }

        for (int i = 0; i < 2; i++)
            if (weights[i] > 0f)
            {
                // Keep the hand turned the way the clip has it (palm on the chest): the IK only
                // moves the arm, it should not twist the wrist.
                Quaternion handRS = RootSpace.Rot(root, _hand[i]);
                RootSpace.TwoBoneIK(root, _arm[i], _fore[i], _hand[i], targets[i], weights[i]);
                RootSpace.SetRot(root, _hand[i], Quaternion.Slerp(RootSpace.Rot(root, _hand[i]), handRS, weights[i]));
            }
    }

    void FitMask(Vector3 rigFace)
    {
        Transform root = transform;
        Vector3 m = RootSpace.Pos(root, _mask);
        float wm = (1f - Mathf.InverseLerp(0.15f, 0.30f, (m - rigFace).magnitude)) * _reachW;
        if (wm <= 0f) return;

        // The exact pose chosen for the mask on the face, if set.
        var om = _mask.GetComponent<OxygenMask>();
        if (om != null && om.useFacePose)
        {
            om.ApplyFacePose(wm);
            // That pose was chosen with the CPR point on his lower spine bone. If the CPR point
            // has moved since, the whole rig moved with it - move the mask back by the same amount
            // so it stays on his face.
            var spine = RootSpace.Find(victim, ":spine");
            if (spine != null)
            {
                Vector3 shift = spine.position - CprPoint(victim, cprPoint); shift.y = 0f;
                _mask.position += shift * wm;
            }
            return;
        }
        if (_vHead == null) return;

        Transform top = RootSpace.Find(victim, "headtop_end");
        Vector3 crown = top != null ? (top.position - _vHead.position).normalized : Vector3.forward;
        Vector3 outOfFace = Vector3.up - crown * Vector3.Dot(Vector3.up, crown);
        outOfFace = outOfFace.sqrMagnitude > 1e-4f ? outOfFace.normalized : Vector3.up;

        Vector3 pos = _vHead.position + outOfFace * maskOut - crown * maskDown;
        _mask.position = Vector3.Lerp(_mask.position, pos, wm);

        if (OxygenMaskModel.Axes.TryGetValue(_mask, out var ax) && _mask.lossyScale.x > 0f)
        {
            Quaternion local = Quaternion.LookRotation(ax.outLocal, ax.noseLocal);
            Quaternion world = Quaternion.LookRotation(outOfFace, crown);
            _mask.rotation = Quaternion.Slerp(_mask.rotation, world * Quaternion.Inverse(local), wm);
        }

        // The fine placement set on the mask (On Face Rotation / Position).
        if (om != null) om.ApplyOnFaceOffset(wm);
    }

    /// <summary>A point on the boy's body between his hips (0) and chest (1).</summary>
    public static Vector3 CprPoint(Transform boy, float k)
    {
        var hips = RootSpace.Find(boy, "hips");
        var chest = RootSpace.Find(boy, "spine2");
        if (hips == null) return boy.position;
        if (chest == null) return hips.position;
        return Vector3.Lerp(hips.position, chest.position, k);
    }

    /// <summary>Flat direction along the boy's body from his hips to his head.</summary>
    Vector3 BodyUp()
    {
        var hips = RootSpace.Find(victim, "hips");
        Transform head = _vHead != null ? _vHead : RootSpace.Find(victim, ":head");
        if (hips == null || head == null) return Vector3.zero;
        Vector3 d = head.position - hips.position; d.y = 0f;
        return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.zero;
    }

    float BoyScale()
    {
        // About a boy's height in world units: the scale that the 0.05 offsets above are relative to.
        return victim != null ? Mathf.Abs(victim.lossyScale.y) * 1.8f : 2f;
    }
}
