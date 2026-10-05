using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Animations;
using UnityEngine.Playables;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Normal (non-VR) player: the character from Assets/player stands in front of the camera,
/// the camera keeps EXACTLY the angle it had in the scene and just follows the character.
///
///   - On-screen joystick (left half of the screen): forward / back / left / right.
///   - JUMP button (bottom-right).
///   - Animations: Run (looping), Idle (standing), Jump. Idle and Jump come from Mixamo -
///     put them in Assets/player and run Tools > Emergency > Setup Player Animations.
///     Without an Idle clip the character stands in a built-in straight pose.
///   - Editor: WASD / arrows to move, Space to jump.
///
/// When this is in the scene, the stereo VR view, the VR hands, the head-tilt locomotion and
/// the second-phone controller are all switched off.
/// </summary>
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(CharacterController))]
public class ThirdPersonPlayer : MonoBehaviour
{
    [Header("References")]
    public Camera followCamera;
    [Tooltip("The character model (child of this object).")]
    public Transform model;
    [Tooltip("Object the animation paths start from (the parent of mixamorig:Hips). " +
             "Set by Tools > Emergency > Setup Player Animations. Empty = the model.")]
    public Transform animatorRoot;

    [Header("Animations")]
    [Tooltip("Run animation (loops) - full joystick.")]
    public AnimationClip moveClip;
    [Tooltip("Walk animation (loops) - joystick pushed a little. Empty = run only.")]
    public AnimationClip walkClip;
    [Tooltip("Joystick amount (0-1) where walking turns into running.")]
    [Range(0.2f, 0.9f)] public float runThreshold = 0.6f;
    [Tooltip("Standing animation from Mixamo (loops). Empty = built-in straight pose.")]
    public AnimationClip idleClip;
    [Tooltip("Jump animation from Mixamo (plays once). Empty = run pose held in the air.")]
    public AnimationClip jumpClip;
    [Tooltip("Animation speed for running.")]
    public float animationSpeed = 1f;
    [Tooltip("Keeps the animation from pushing the body forward and snapping back each loop " +
             "(a Mixamo clip downloaded without 'In Place').")]
    public bool keepAnimationInPlace = true;

    [Header("Movement")]
    public float moveSpeed = 3f;
    public float turnSpeed = 720f;
    public float gravity = -9.81f;
    [Tooltip("Turn the model if it faces the wrong way.")]
    public float modelYawOffset = 0f;

    [Header("Jump")]
    public float jumpHeight = 1.0f;
    public float jumpCooldown = 0.25f;
    [Tooltip("Speed of the jump animation.")]
    public float jumpAnimationSpeed = 1f;
    [Tooltip("Second in the jump clip where the feet leave the ground. Set by the setup tool.")]
    public float jumpTakeoffTime = 0f;
    [Tooltip("Second in the jump clip where the feet touch down again. Set by the setup tool.")]
    public float jumpLandTime = 0f;
    [Tooltip("How long the crouch before takeoff lasts after pressing. Short = jumps right away.")]
    [Range(0f, 0.5f)] public float jumpWindup = 0.12f;

    [Header("Smoothness")]
    [Tooltip("Seconds to reach full speed. Higher = softer start.")]
    [Range(0.01f, 1f)] public float accelerationTime = 0.18f;
    [Tooltip("Seconds to come to a stop. Higher = softer stop.")]
    [Range(0.01f, 1f)] public float decelerationTime = 0.12f;
    [Tooltip("Seconds to turn toward the new direction. Higher = softer turns.")]
    [Range(0.01f, 0.5f)] public float turnSmoothTime = 0.1f;
    [Tooltip("Camera follow softness. 0 = rigid, 0.1-0.2 = smooth.")]
    [Range(0f, 0.5f)] public float cameraSmoothTime = 0.12f;
    [Tooltip("Seconds to blend between standing, running and jumping.")]
    [Range(0.01f, 0.6f)] public float animationBlendTime = 0.2f;

    [Header("Screen")]
    public bool forceLandscape = true;
    public int targetFrameRate = 60;

    // ------------------------------------------------------------------ state

    CharacterController _cc;
    Vector3 _camOffset, _camVel;
    Quaternion _camRotation;
    float _vy;
    Vector3 _planarVel, _accelRef;
    float _yawVel;

    bool _jumpRequested, _jumping, _launchPending;
    float _jumpCd, _jumpTime, _airTime, _jumpClipStart;

    PlayableGraph _graph;
    AnimationMixerPlayable _mixer;
    AnimationClipPlayable _runP, _idleP, _jumpP, _walkP;
    bool _hasAnim;
    float _runTime, _idleTime, _walkTime;
    float _runW, _jumpW, _walkShare;   // smoothed blend weights

    VirtualJoystick _joystick;

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        _cc = GetComponent<CharacterController>();
        if (followCamera == null) followCamera = Camera.main;
        DisableVR();
    }

    void Start()
    {
        if (forceLandscape) Screen.orientation = ScreenOrientation.LandscapeLeft;
        if (targetFrameRate > 0) Application.targetFrameRate = targetFrameRate;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;

        if (followCamera != null)
        {
            // Keep the camera exactly as it is in the scene - same angle, same distance.
            _camOffset = followCamera.transform.position - transform.position;
            _camRotation = followCamera.transform.rotation;
        }

        CaptureRestPose();        // before any animation touches the bones
        SetupAnimation();
        BuildControls();
    }

    void OnDestroy()
    {
        if (_graph.IsValid()) _graph.Destroy();
    }

    /// <summary>Turns off everything that made this a phone-VR scene.</summary>
    void DisableVR()
    {
        foreach (var vr in FindObjectsByType<SimpleStereoVR>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            vr.enabled = false;
        foreach (var l in FindObjectsByType<PhoneVRLocomotion>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            l.enabled = false;
        foreach (var r in FindObjectsByType<VRLinkReceiver>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            r.enabled = false;

        if (followCamera == null) return;

        var cam = followCamera;
        cam.enabled = true;
        cam.rect = new Rect(0f, 0f, 1f, 1f);
        cam.targetTexture = null;

        foreach (var mb in cam.GetComponents<MonoBehaviour>())
            if (mb != null && mb != this && mb.GetType().Name.Contains("TrackedPoseDriver"))
                mb.enabled = false;

        // The old VR rig's own capsule would be an invisible wall where the rig was standing.
        Transform rig = cam.transform;
        while (rig.parent != null) rig = rig.parent;
        if (rig != transform && !transform.IsChildOf(rig))
            foreach (var cc in rig.GetComponentsInChildren<CharacterController>(true))
                cc.enabled = false;
    }

    // ------------------------------------------------------------------ update

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector2 input = ReadMove();

        // Directions relative to the camera, flattened to the ground.
        Vector3 fwd = Vector3.forward, right = Vector3.right;
        if (followCamera != null)
        {
            fwd = Vector3.ProjectOnPlane(followCamera.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.ProjectOnPlane(followCamera.transform.up, Vector3.up);
            fwd.Normalize();
            right = Vector3.Cross(Vector3.up, fwd);
        }

        Vector3 move = fwd * input.y + right * input.x;
        if (move.sqrMagnitude > 1f) move.Normalize();

        // Ease the speed in and out instead of jumping to it.
        Vector3 targetVel = move * moveSpeed;
        float ease = targetVel.sqrMagnitude > _planarVel.sqrMagnitude ? accelerationTime : decelerationTime;
        if (!_cc.isGrounded) ease *= 2.5f;             // less control in the air, feels natural
        _planarVel = Vector3.SmoothDamp(_planarVel, targetVel, ref _accelRef, ease);
        if (targetVel == Vector3.zero && _planarVel.sqrMagnitude < 0.0004f) _planarVel = Vector3.zero;

        if (move.sqrMagnitude > 0.0025f)
        {
            float targetYaw = Mathf.Atan2(move.x, move.z) * Mathf.Rad2Deg + modelYawOffset;
            float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetYaw, ref _yawVel,
                                              turnSmoothTime, turnSpeed);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        // ---- jump
        _jumpCd -= dt;
        bool grounded = _cc.isGrounded;
        if (grounded) _airTime = 0f; else _airTime += dt;

        if ((_jumpRequested || ReadJumpKey()) && _jumpCd <= 0f && (grounded || _airTime < 0.15f) && !_jumping)
        {
            // The animation starts at the press, skipping most of the clip's slow crouch so the
            // jump answers immediately; the body leaves the ground exactly when the clip does.
            _jumping = true;
            _launchPending = true;
            _jumpTime = 0f;
            _jumpCd = jumpCooldown;
            _jumpClipStart = jumpClip != null ? Mathf.Max(0f, jumpTakeoffTime - jumpWindup) : 0f;
        }
        _jumpRequested = false;

        if (_jumping)
        {
            _jumpTime += dt;
            float clipT = _jumpClipStart + _jumpTime * jumpAnimationSpeed;
            if (_launchPending && (jumpClip == null || clipT >= jumpTakeoffTime))
            {
                _vy = Mathf.Sqrt(jumpHeight * 2f * Mathf.Abs(gravity));
                _launchPending = false;
                grounded = false;
            }
        }

        if (grounded && _vy < 0f) _vy = -2f;
        else _vy += gravity * dt;

        Vector3 velocity = _planarVel;
        velocity.y = _vy;
        if (_cc.enabled) _cc.Move(velocity * dt);

        if (_jumping && !_launchPending && _cc.isGrounded && _vy <= 0f)
        {
            // Landed. Let the clip's landing play out a little, then blend back to stand/run.
            float clipT = _jumpClipStart + _jumpTime * jumpAnimationSpeed;
            float landEnd = jumpLandTime > 0f ? jumpLandTime + 0.15f : 0f;
            if (jumpClip == null || clipT >= landEnd || _planarVel.sqrMagnitude > 1f) _jumping = false;
        }

        UpdateAnimation(_planarVel.magnitude / Mathf.Max(0.01f, moveSpeed), dt);
    }

    void LateUpdate()
    {
        FixPoseAfterAnimation();

        if (followCamera == null) return;
        Vector3 target = transform.position + _camOffset;
        followCamera.transform.position = cameraSmoothTime <= 0.001f
            ? target
            : Vector3.SmoothDamp(followCamera.transform.position, target, ref _camVel, cameraSmoothTime);
        followCamera.transform.rotation = _camRotation;   // angle never changes
    }

    // ------------------------------------------------------------------ input

    Vector2 ReadMove()
    {
        Vector2 v = _joystick != null ? _joystick.Value : Vector2.zero;

#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        if (k != null)
        {
            if (k.wKey.isPressed || k.upArrowKey.isPressed) v.y += 1f;
            if (k.sKey.isPressed || k.downArrowKey.isPressed) v.y -= 1f;
            if (k.dKey.isPressed || k.rightArrowKey.isPressed) v.x += 1f;
            if (k.aKey.isPressed || k.leftArrowKey.isPressed) v.x -= 1f;
        }
        if (Gamepad.current != null)
        {
            Vector2 s = Gamepad.current.leftStick.ReadValue();
            if (s.magnitude > 0.2f) v += s;
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        v += new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
#endif
        return Vector2.ClampMagnitude(v, 1f);
    }

    bool ReadJumpKey()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) return true;
        if (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame) return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.Space)) return true;
#endif
        return false;
    }

    public void RequestJump() => _jumpRequested = true;

    // ------------------------------------------------------------------ animation

    void SetupAnimation()
    {
        if (model == null || moveClip == null) return;

        // The animation paths start at animatorRoot (e.g. "mixamorig:Hips/..."), so that is
        // where the Animator has to be. Any other Animator on the model is switched off so
        // two of them do not fight over the bones.
        Transform root = animatorRoot != null ? animatorRoot : model;
        foreach (var other in model.GetComponentsInChildren<Animator>(true))
            if (other.transform != root) other.enabled = false;

        var animator = root.GetComponent<Animator>();
        if (animator == null) animator = root.gameObject.AddComponent<Animator>();
        animator.enabled = true;
        animator.runtimeAnimatorController = null;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        _graph = PlayableGraph.Create("PlayerAnimation");
        var output = AnimationPlayableOutput.Create(_graph, "Animation", animator);

        // 0 = run, 1 = idle, 2 = jump, 3 = walk. A missing clip's slot is filled with the run
        // clip and kept at weight 0 (idle is then the built-in pose, applied after animation).
        _runP = MakeClip(moveClip);
        _idleP = MakeClip(idleClip != null ? idleClip : moveClip);
        _jumpP = MakeClip(jumpClip != null ? jumpClip : moveClip);
        _walkP = MakeClip(walkClip != null ? walkClip : moveClip);

        _mixer = AnimationMixerPlayable.Create(_graph, 4);
        _graph.Connect(_runP, 0, _mixer, 0);
        _graph.Connect(_idleP, 0, _mixer, 1);
        _graph.Connect(_jumpP, 0, _mixer, 2);
        _graph.Connect(_walkP, 0, _mixer, 3);

        output.SetSourcePlayable(_mixer);
        _graph.Play();
        _hasAnim = true;
        UpdateAnimation(0f, 0f);
    }

    AnimationClipPlayable MakeClip(AnimationClip clip)
    {
        var p = AnimationClipPlayable.Create(_graph, clip);
        p.SetApplyFootIK(false);
        p.SetSpeed(0);          // time is driven by hand below, so loops are exact
        p.SetTime(0);
        return p;
    }

    void UpdateAnimation(float amount, float dt)
    {
        if (!_hasAnim) return;

        float k = dt / animationBlendTime;
        _runW = Mathf.MoveTowards(_runW, Mathf.Clamp01(amount * 1.5f), k);
        _jumpW = Mathf.MoveTowards(_jumpW, _jumping ? 1f : 0f, _jumping ? k * 2f : k);

        // Walk vs run: a little joystick walks, a lot runs, blended in between.
        float wantWalk = walkClip != null && amount < runThreshold ? 1f : 0f;
        _walkShare = Mathf.MoveTowards(_walkShare, wantWalk, k);

        // Run: loops forever, legs keep pace with the body.
        float runLen = Mathf.Max(0.01f, moveClip.length);
        float runSpeed = animationSpeed * Mathf.Clamp(amount, 0.6f, 1.1f);
        _runTime = Mathf.Repeat(_runTime + dt * runSpeed, runLen);
        _runP.SetTime(_runTime);

        if (walkClip != null)
        {
            float walkSpeed = animationSpeed * Mathf.Clamp(amount / runThreshold, 0.5f, 1.2f);
            _walkTime = Mathf.Repeat(_walkTime + dt * walkSpeed, Mathf.Max(0.01f, walkClip.length));
            _walkP.SetTime(_walkTime);
        }

        // Idle: loops.
        if (idleClip != null)
        {
            _idleTime = Mathf.Repeat(_idleTime + dt, Mathf.Max(0.01f, idleClip.length));
            _idleP.SetTime(_idleTime);
        }

        // Jump: plays once from the press, holds its last frame.
        if (jumpClip != null)
            _jumpP.SetTime(Mathf.Min(_jumpClipStart + _jumpTime * jumpAnimationSpeed, jumpClip.length - 0.01f));

        // Eased (S-curve) weights: transitions start and finish softly instead of linearly.
        float jw = jumpClip != null ? Ease(_jumpW) : 0f;    // no jump clip: run pose in the air
        float moveW = (1f - jw) * (_jumping && jumpClip == null ? 1f : Ease(_runW));
        float idleW = 1f - jw - moveW;
        float walkW = moveW * Ease(_walkShare);
        float runW = moveW - walkW;

        _mixer.SetInputWeight(0, runW);
        _mixer.SetInputWeight(1, idleClip != null ? idleW : 0f);
        _mixer.SetInputWeight(2, jw);
        _mixer.SetInputWeight(3, walkW);

        // With no idle clip, the run slot carries the idle share and the built-in pose
        // replaces it after the animation (see FixPoseAfterAnimation).
        if (idleClip == null) _mixer.SetInputWeight(0, runW + idleW);
        _builtInIdleWeight = idleClip == null ? idleW : 0f;
    }

    static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

    // ------------------------------------------------------------------ pose fixes

    /// <summary>
    /// Runs after the animation each frame:
    ///   1. Holds the hips over the feet horizontally. A Mixamo run downloaded without
    ///      "In Place" carries the body forward through the clip and snaps it back when the clip
    ///      restarts - that snap is what made the run look like it was not looping.
    ///   2. Without an Idle clip, blends in a built-in standing pose (rest pose, arms down).
    /// </summary>
    Transform[] _bones;
    Quaternion[] _standRot;
    Transform _hips;
    Vector3 _hipsRestInModel;
    float _builtInIdleWeight;

    void CaptureRestPose()
    {
        if (model == null) return;

        _bones = model.GetComponentsInChildren<Transform>(true);
        _standRot = new Quaternion[_bones.Length];
        for (int i = 0; i < _bones.Length; i++)
        {
            _standRot[i] = _bones[i].localRotation;
            if (_hips == null && BoneIs(_bones[i], "Hips")) _hips = _bones[i];
        }
        if (_hips != null) _hipsRestInModel = model.InverseTransformPoint(_hips.position);

        LowerArm("LeftArm", "LeftForeArm");
        LowerArm("RightArm", "RightForeArm");
    }

    void LowerArm(string armName, string foreArmName)
    {
        int ai = System.Array.FindIndex(_bones, b => BoneIs(b, armName));
        Transform fore = System.Array.Find(_bones, b => BoneIs(b, foreArmName));
        if (ai < 0 || fore == null) return;

        Transform arm = _bones[ai];
        Vector3 dir = fore.position - arm.position;
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();

        float side = Mathf.Sign(Vector3.Dot(dir, model.right));
        Vector3 down = (-model.up + model.right * side * 0.12f + model.forward * 0.05f).normalized;
        Quaternion world = Quaternion.FromToRotation(dir, down) * arm.rotation;
        _standRot[ai] = Quaternion.Inverse(arm.parent.rotation) * world;
    }

    static bool BoneIs(Transform t, string bone)
    {
        string n = t.name;
        return n == bone || n.EndsWith(":" + bone) || n.EndsWith("_" + bone);
    }

    void FixPoseAfterAnimation()
    {
        if (_bones == null || !_hasAnim) return;

        // 2. built-in standing pose
        float w = _builtInIdleWeight;
        if (w > 0.001f)
        {
            for (int i = 1; i < _bones.Length; i++)
            {
                var b = _bones[i];
                if (b == null) continue;
                b.localRotation = w >= 0.999f ? _standRot[i] : Quaternion.Slerp(b.localRotation, _standRot[i], w);
            }
            if (_hips != null)
            {
                Vector3 cur = model.InverseTransformPoint(_hips.position);
                _hips.position = model.TransformPoint(Vector3.Lerp(cur, _hipsRestInModel, w));
            }
        }

        // 1. keep the body in place over the feet (forward/sideways only, bounce stays)
        if (keepAnimationInPlace && _hips != null)
        {
            Vector3 p = model.InverseTransformPoint(_hips.position);
            p.x = _hipsRestInModel.x;
            p.z = _hipsRestInModel.z;
            // The jump's height comes from the physics jump; drop the clip's own rise so the
            // body does not go up twice (its crouch, below the rest height, is kept).
            if (jumpClip != null && _jumpW > 0f)
                p.y = Mathf.Lerp(p.y, Mathf.Min(p.y, _hipsRestInModel.y), _jumpW);
            _hips.position = model.TransformPoint(p);
        }
    }

    // ------------------------------------------------------------------ on-screen controls

    void BuildControls()
    {
        EnsureTouchEventSystem();

        var canvasGo = new GameObject("Player Controls");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600f, 800f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        Sprite circle = MakeCircleSprite(128);

        // Touch zone: the left half of the screen. Put your thumb anywhere there and the
        // joystick jumps under it.
        var zoneGo = new GameObject("Joystick Zone");
        zoneGo.transform.SetParent(canvasGo.transform, false);
        var zoneRt = zoneGo.AddComponent<RectTransform>();
        zoneRt.anchorMin = new Vector2(0f, 0f);
        zoneRt.anchorMax = new Vector2(0.5f, 0.85f);
        zoneRt.offsetMin = zoneRt.offsetMax = Vector2.zero;
        var zoneImg = zoneGo.AddComponent<Image>();
        zoneImg.color = new Color(0f, 0f, 0f, 0f);

        var baseGo = new GameObject("Joystick");
        baseGo.transform.SetParent(zoneGo.transform, false);
        var baseRt = baseGo.AddComponent<RectTransform>();
        baseRt.anchorMin = baseRt.anchorMax = new Vector2(0f, 0f);
        baseRt.pivot = new Vector2(0.5f, 0.5f);
        baseRt.anchoredPosition = new Vector2(210f, 210f);
        baseRt.sizeDelta = new Vector2(280f, 280f);
        var baseImg = baseGo.AddComponent<Image>();
        baseImg.sprite = circle;
        baseImg.color = new Color(1f, 1f, 1f, 0.25f);
        baseImg.raycastTarget = false;

        var knobGo = new GameObject("Knob");
        knobGo.transform.SetParent(baseGo.transform, false);
        var knobRt = knobGo.AddComponent<RectTransform>();
        knobRt.anchorMin = knobRt.anchorMax = new Vector2(0.5f, 0.5f);
        knobRt.sizeDelta = new Vector2(120f, 120f);
        var knobImg = knobGo.AddComponent<Image>();
        knobImg.sprite = circle;
        knobImg.color = new Color(1f, 1f, 1f, 0.75f);
        knobImg.raycastTarget = false;

        _joystick = zoneGo.AddComponent<VirtualJoystick>();
        _joystick.background = baseRt;
        _joystick.knob = knobRt;

        // Jump button.
        var btnGo = new GameObject("Jump Button");
        btnGo.transform.SetParent(canvasGo.transform, false);
        var btnRt = btnGo.AddComponent<RectTransform>();
        btnRt.anchorMin = btnRt.anchorMax = new Vector2(1f, 0f);
        btnRt.pivot = new Vector2(0.5f, 0.5f);
        btnRt.anchoredPosition = new Vector2(-190f, 190f);
        btnRt.sizeDelta = new Vector2(180f, 180f);
        var btnImg = btnGo.AddComponent<Image>();
        btnImg.sprite = circle;
        btnImg.color = new Color(0.15f, 0.55f, 0.95f, 0.75f);
        var press = btnGo.AddComponent<PressButton>();
        press.onPress = RequestJump;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(btnGo.transform, false);
        var lrt = labelGo.AddComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
        var t = labelGo.AddComponent<Text>();
        t.font = UIFont();
        t.fontSize = 36;
        t.fontStyle = FontStyle.Bold;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.text = "JUMP";
        t.raycastTarget = false;
    }

    /// <summary>
    /// Makes sure phone touches reach the joystick and the button. The scene came from the VR
    /// template, whose EventSystem uses the XR UI module; that one is switched off and the
    /// normal touch module used instead.
    /// </summary>
    static void EnsureTouchEventSystem()
    {
        var es = FindFirstObjectByType<EventSystem>();
        if (es == null) es = new GameObject("EventSystem").AddComponent<EventSystem>();

        foreach (var m in es.GetComponents<BaseInputModule>())
            if (m.GetType().Name.Contains("XRUIInputModule")) m.enabled = false;

#if ENABLE_INPUT_SYSTEM
        var touch = es.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        if (touch == null) touch = es.gameObject.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        touch.enabled = true;
#else
        var touch = es.GetComponent<StandaloneInputModule>();
        if (touch == null) touch = es.gameObject.AddComponent<StandaloneInputModule>();
        touch.enabled = true;
#endif
        es.enabled = true;
        es.gameObject.SetActive(true);
    }

    static Font UIFont()
    {
        Font f = null;
        try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (f == null) { try { f = Font.CreateDynamicFontFromOSFont("Arial", 28); } catch { } }
        return f;
    }

    static Sprite MakeCircleSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float r = size * 0.5f;
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                byte a = (byte)(Mathf.Clamp01(r - d) * 255f);
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }
}

/// <summary>
/// Floating on-screen joystick (touch on the phone, mouse in the Editor).
/// Touch anywhere in its zone: the circle moves under the thumb, drag the ball to move.
/// Tracks one finger only, so jumping with the other thumb never disturbs it.
/// </summary>
public class VirtualJoystick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    public RectTransform background;
    public RectTransform knob;
    [Range(0f, 0.4f)] public float deadZone = 0.1f;
    public float knobSmoothing = 30f;

    public Vector2 Value { get; private set; }

    Vector2 _home, _knobTarget;
    int _pointer = int.MinValue;

    void Start()
    {
        if (background != null) _home = background.anchoredPosition;
    }

    public void OnPointerDown(PointerEventData e)
    {
        if (_pointer != int.MinValue) return;
        _pointer = e.pointerId;

        if (background != null)
        {
            var parent = (RectTransform)background.parent;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, e.position, e.pressEventCamera, out Vector2 p))
            {
                Vector2 anchorPoint = new Vector2(
                    (background.anchorMin.x - parent.pivot.x) * parent.rect.width,
                    (background.anchorMin.y - parent.pivot.y) * parent.rect.height);
                background.anchoredPosition = p - anchorPoint;
            }
        }

        OnDrag(e);
    }

    public void OnDrag(PointerEventData e)
    {
        if (e.pointerId != _pointer || background == null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(background, e.position,
                e.pressEventCamera, out Vector2 local))
            return;

        float radius = background.rect.width * 0.5f;
        Vector2 v = Vector2.ClampMagnitude(local / radius, 1f);
        _knobTarget = v * radius;

        float m = v.magnitude;
        if (m < deadZone) { Value = Vector2.zero; return; }
        float t = (m - deadZone) / (1f - deadZone);
        Value = v / m * Mathf.SmoothStep(0f, 1f, t);
    }

    public void OnPointerUp(PointerEventData e)
    {
        if (e.pointerId != _pointer) return;
        Release();
    }

    void Release()
    {
        _pointer = int.MinValue;
        Value = Vector2.zero;
        _knobTarget = Vector2.zero;
        if (background != null) background.anchoredPosition = _home;
    }

    void Update()
    {
        if (knob != null)
            knob.anchoredPosition = Vector2.Lerp(knob.anchoredPosition, _knobTarget,
                                                 1f - Mathf.Exp(-knobSmoothing * Time.unscaledDeltaTime));
    }

    void OnDisable() { Release(); }
}

/// <summary>Fires on touch-down (not on release) so it feels instant.</summary>
public class PressButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public System.Action onPress;
    Vector3 _scale = Vector3.one;

    public void OnPointerDown(PointerEventData e)
    {
        _scale = transform.localScale;
        transform.localScale = _scale * 0.9f;
        onPress?.Invoke();
    }

    public void OnPointerUp(PointerEventData e) { transform.localScale = _scale; }
}
