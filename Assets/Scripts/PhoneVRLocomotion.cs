using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Hands-free locomotion for phone-in-viewer VR (Cardboard / VR Box).
///
/// The viewer is 3DOF: the gyro reports head ROTATION only, never body POSITION. There is no
/// tracking of the player walking around a room and no plugin can add it - the phone has no
/// outward camera view inside the viewer. So the wearer's head has to drive everything, and it
/// does, with nothing to press:
///
///   Look around / turn 360   - your head. Physically turn; the world turns with you.
///   Walk forward             - tip your head DOWN past walkTiltAngle. The further down, the
///                              faster, up to moveSpeed. Raise your head level to stop.
///   Walk backward            - hold your head UP past backTiltAngle for backHoldTime, so a
///                              glance at the sky does not reverse you into a wall.
///   Jump                     - a quick upward flick of the head (a nod), faster than anyone
///                              looks up on purpose.
///
/// Movement always follows where you are LOOKING, flattened to the ground plane, so looking
/// down to walk never drives the camera into the road.
///
/// A Bluetooth gamepad still works if one is paired - left stick walks, right stick turns,
/// A jumps - but nothing here needs it, and nothing here reads the touchscreen. The viewer's
/// trigger button is deliberately ignored: tapping the screen to move was the thing that made
/// this feel broken.
///
/// Attach to the rig root that owns the CharacterController. Disable SimpleVRMovement on the
/// same object - two scripts writing one CharacterController fight each other.
/// </summary>
public class PhoneVRLocomotion : MonoBehaviour
{
    public enum TurnStyle
    {
        HeadOnly,   // Turn your body. Full 360, zero nausea, needs room to spin.
        Smooth,     // Right stick turns continuously. Gamepad only.
        Snap        // Right stick turns in fixed steps. Gamepad only, gentler than Smooth.
    }

    [Header("References")]
    [Tooltip("The VR camera. Auto-detected from children if left empty.")]
    public Transform headTransform;

    [Header("Movement")]
    public float moveSpeed = 3.0f;
    public float jumpHeight = 1.1f;
    public float gravity = -9.81f;

    [Tooltip("Seconds to reach full speed. Stops the world lurching when you tip your head.")]
    [Range(0f, 2f)] public float acceleration = 0.35f;

    [Header("Walk by walking")]
    /// <summary>
    /// You walk on the spot, the view walks with you; you stop, it stops.
    ///
    /// A phone in a viewer cannot track where your body is - there is no camera looking out and
    /// no second sensor to triangulate with, so position is simply not measurable. What IS
    /// measurable is the jolt of each footfall: walking shakes the phone about twice a second,
    /// in a way nothing else does. Counting those shakes gives "is this person walking right
    /// now", which is the part that actually matters. Steps arriving means move; steps stopping
    /// means stop.
    ///
    /// Direction comes from where you are looking, so you steer by turning your head.
    /// </summary>
    [Tooltip("Move while you are actually walking, measured from the accelerometer.")]
    public bool walkByStepping = true;

    [Tooltip("How hard a jolt counts as a footstep, in g. Raise it if standing still creeps " +
             "forward, lower it if gentle walking is missed.")]
    [Range(0.03f, 0.6f)] public float stepThreshold = 0.12f;

    [Tooltip("Shortest gap between two steps. Stops one footfall being counted several times.")]
    [Range(0.15f, 0.6f)] public float stepCooldown = 0.28f;

    [Tooltip("Keep moving this long after the last step before deciding you have stopped. " +
             "Shorter feels more responsive, longer rides out an uneven stride.")]
    [Range(0.3f, 2f)] public float stopAfterSeconds = 0.8f;

    [Header("Head steering (alternative to stepping)")]
    [Tooltip("Tip your head down to walk. Off by default now that stepping does the same job " +
             "without tiring your neck - turn it on if you want to move while seated.")]
    public bool useHeadTilt = false;

    [Tooltip("Tip your head down past this many degrees to start walking.")]
    [Range(5f, 45f)] public float walkTiltAngle = 18f;

    [Tooltip("Angle at which you are at full speed.")]
    [Range(10f, 80f)] public float fullSpeedTiltAngle = 45f;

    [Header("Head steering - walk backward")]
    public bool allowBackward = false;

    [Tooltip("Hold your head above this angle to reverse.")]
    [Range(10f, 70f)] public float backTiltAngle = 30f;

    [Tooltip("How long you must hold it, so a glance upward does not reverse you.")]
    [Range(0f, 2f)] public float backHoldTime = 0.5f;

    [Range(0.1f, 1f)] public float backSpeedFactor = 0.5f;

    [Header("Head steering - jump")]
    [Tooltip("A quick upward flick of the head jumps. No button, no tap.")]
    public bool nodToJump = true;

    [Tooltip("Degrees per second of upward head movement that counts as a flick. Raise it if " +
             "you jump by accident, lower it if flicks are being missed.")]
    [Range(60f, 500f)] public float nodJumpSpeed = 190f;

    [Tooltip("Minimum seconds between two nod jumps.")]
    public float jumpCooldown = 0.6f;

    [Header("Second phone as a controller")]
    /// <summary>
    /// The reason this exists: a phone in a viewer can measure which way you are FACING and
    /// nothing else. Rotation is a sensor reading; position is not measurable at all, because
    /// there is no outward camera and no second reference point to triangulate against. So every
    /// way of walking without a controller is a guess dressed up as input - tip your head, or
    /// count footfalls - and each of them fails in a way you can feel.
    ///
    /// Four arrows on a second phone is not a compromise, it is the only input here that says
    /// exactly what it means.
    /// </summary>
    [Tooltip("Accept the four arrows from the other phone. Add a VR Link Receiver to this rig; " +
             "found automatically if it is anywhere in the scene.")]
    public bool useRemote = true;

    [Tooltip("Leave empty to find one in the scene.")]
    public VRLinkReceiver remote;

    [Header("Optional Bluetooth gamepad")]
    public bool useGamepad = true;
    [Range(0f, 0.9f)] public float stickDeadzone = 0.2f;
    public float sprintMultiplier = 1.8f;

    [Header("Turning (gamepad only - your head turns you otherwise)")]
    public TurnStyle turnStyle = TurnStyle.Snap;
    public float smoothTurnSpeed = 90f;
    public float snapTurnAngle = 30f;
    public float snapTurnCooldown = 0.25f;

    [Header("Walkable area")]
    [Tooltip("Keeps the wearer inside the VRSafeZone boxes in the scene. With no zones present " +
             "this does nothing, so it is safe to leave on.")]
    public bool useSafeZone = true;

    [Tooltip("How far inside the zone edge to stop, so you do not stand with your face in a wall.")]
    [Range(0f, 3f)] public float safeZoneMargin = 0.5f;

    [Header("Editor Testing")]
    public bool useKeyboardInEditor = true;
    public bool useMouseLookInEditor = true;
    public float mouseSensitivity = 2.0f;

    [Header("Debug")]
    public bool debugInput = false;

    /// <summary>
    /// Prints what every input source is actually reporting, on the phone, in the corner of the
    /// screen.
    ///
    /// "It does not move" has four completely different causes that look identical from inside a
    /// viewer: no accelerometer exposed to the app, a step threshold that is never crossed, no
    /// CharacterController to move, or a controller that is not connected. Guessing between them
    /// costs a rebuild each time. This says which it is.
    /// </summary>
    [Tooltip("Shows the live input readings on the phone, so a rebuild is not needed to find " +
             "out why nothing is moving. Turn off once it works.")]
    public bool showInputOnScreen = true;

    // ---------------------------------------------------------------- state

    CharacterController controller;
    float verticalVelocity;
    float snapCooldownLeft;

    float currentSpeed;          // smoothed, so tipping the head does not snap to full pace
    float backHeldFor;
    float lastPitch;
    float jumpCooldownLeft;
    bool havePitchSample;

    float editorPitch;
    Quaternion initialHeadLocalRotation;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        if (controller == null) controller = GetComponentInParent<CharacterController>();
        if (controller == null) controller = GetComponentInChildren<CharacterController>();

        if (headTransform == null)
        {
            Camera cam = GetComponentInChildren<Camera>(true);
            if (cam != null) headTransform = cam.transform;
        }

        if (headTransform != null) initialHeadLocalRotation = headTransform.localRotation;

        if (remote == null) remote = Object.FindFirstObjectByType<VRLinkReceiver>();

        EnableMotionSensors();

        if (controller == null)
        {
            Debug.LogError($"[PhoneVRLocomotion] '{name}': no CharacterController found. " +
                           "Add one to the rig root, otherwise there is nothing to move.", this);
            enabled = false;
        }
    }

    void Start()
    {
        if (showInputOnScreen) BuildStatusLabel();
    }

    /// <summary>
    /// Wakes the motion sensors before the first read.
    ///
    /// The Input System ships every sensor disabled. An app that reads Accelerometer.current
    /// without enabling it gets a valid device object that reports exactly zero, forever - which
    /// is the difference between "this phone has no accelerometer" and "nobody switched it on",
    /// and the two are indistinguishable from the reading alone. Enabling here, once, at startup,
    /// rather than on the first read, also means the first reading is real rather than the zero
    /// that a just-enabled sensor returns for a frame or two.
    /// </summary>
    void EnableMotionSensors()
    {
#if ENABLE_INPUT_SYSTEM
        TryEnable(UnityEngine.InputSystem.Accelerometer.current);
        TryEnable(UnityEngine.InputSystem.LinearAccelerationSensor.current);
        TryEnable(UnityEngine.InputSystem.GravitySensor.current);
#endif
    }

#if ENABLE_INPUT_SYSTEM
    static void TryEnable(UnityEngine.InputSystem.InputDevice device)
    {
        if (device == null || device.enabled) return;
        try { UnityEngine.InputSystem.InputSystem.EnableDevice(device); } catch { }
    }
#endif

    void Update()
    {
        if (controller == null) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        float pitch = HeadPitchDown();          // + looking down, - looking up

        // Head yaw defines "forward". Pitch is stripped so looking down never digs into the road.
        Vector3 forward = HeadForwardFlat();
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        // --- am I walking? ---------------------------------------------------
        float headDrive = 0f;

        if (walkByStepping && DetectStepping(dt)) headDrive = 1f;
        if (useHeadTilt) headDrive += HeadDrive(pitch, dt);

        headDrive = Mathf.Clamp(headDrive, -1f, 1f);

        // --- gamepad and the second phone ----------------------------------
        Vector2 stick = ReadMoveStick();
        Vector2 pad = ReadRemoteStick();

        stick = Vector2.ClampMagnitude(stick + pad, 1f);

        Vector3 wish = forward * (headDrive + stick.y) + right * stick.x;
        if (wish.sqrMagnitude > 1f) wish.Normalize();

        float target = moveSpeed * (ReadSprint() ? sprintMultiplier : 1f) * wish.magnitude;
        currentSpeed = acceleration <= 0.01f
            ? target
            : Mathf.MoveTowards(currentSpeed, target, (moveSpeed / acceleration) * dt);

        Vector3 dir = wish.sqrMagnitude > 0.0001f ? wish.normalized : Vector3.zero;
        Vector3 velocity = dir * currentSpeed;

        // --- gravity and jump ----------------------------------------------
        jumpCooldownLeft -= dt;
        bool jump = ReadGamepadJump() || ReadRemoteJump() || NodJump(pitch, dt);

        if (controller.isGrounded)
        {
            if (verticalVelocity < 0f) verticalVelocity = -2f;
            if (jump && jumpCooldownLeft <= 0f)
            {
                verticalVelocity = Mathf.Sqrt(jumpHeight * 2f * Mathf.Abs(gravity));
                jumpCooldownLeft = jumpCooldown;
            }
        }
        else
        {
            verticalVelocity += gravity * dt;
        }
        velocity.y = verticalVelocity;

        if (controller.enabled) controller.Move(velocity * dt);

        ApplySafeZone();

        ApplyTurn(ReadTurnAxis());
        ApplyEditorMouseLook();

        lastPitch = pitch;
        havePitchSample = true;

        UpdateStatusLabel(pad, headDrive, pitch);

        if (debugInput && Time.frameCount % 60 == 0)
        {
            Debug.Log($"[PhoneVRLocomotion] steps {StepCount}, walking {headDrive > 0.01f}, " +
                      $"pitch {pitch:F0}deg, speed {currentSpeed:F2}, " +
                      $"grounded {controller.isGrounded}", this);
        }
    }

    // ---------------------------------------------------------------- second phone

    Vector2 ReadRemoteStick()
    {
        if (!useRemote) return Vector2.zero;
        if (remote == null) remote = Object.FindFirstObjectByType<VRLinkReceiver>();
        if (remote == null || !remote.Connected) return Vector2.zero;

        return remote.Move;
    }

    bool ReadRemoteJump()
    {
        return useRemote && remote != null && remote.JumpPressed;
    }

    // ---------------------------------------------------------------- on-screen status

    Text _statusLabel;

    void BuildStatusLabel()
    {
        var go = new GameObject("Locomotion Status");
        go.transform.SetParent(transform, false);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32040;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600f, 800f);
        scaler.matchWidthOrHeight = 0.5f;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);

        var rt = textGo.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(18f, 14f);
        rt.sizeDelta = new Vector2(620f, 130f);

        _statusLabel = textGo.AddComponent<Text>();
        _statusLabel.font = VRLinkReceiver.LinkFont();
        _statusLabel.fontSize = 20;
        _statusLabel.alignment = TextAnchor.LowerLeft;
        _statusLabel.color = new Color(1f, 1f, 1f, 0.8f);
        _statusLabel.raycastTarget = false;
        _statusLabel.text = "";
    }

    void UpdateStatusLabel(Vector2 pad, float headDrive, float pitch)
    {
        if (_statusLabel == null || Time.frameCount % 6 != 0) return;

        string padState = !useRemote
            ? "off"
            : remote == null
                ? "no receiver in scene"
                : remote.Connected
                    ? $"{pad.x:F1},{pad.y:F1}  ({remote.PacketsReceived} pkt)"
                    : $"waiting - type {remote.Address}";

        string steps = walkByStepping
            ? $"{StepCount} steps, jolt {_lastDeviation:F2}/{stepThreshold:F2} [{_accelSource}]"
            : "off";

        _statusLabel.text =
            $"pad    {padState}\n" +
            $"steps  {steps}\n" +
            $"tilt   {(useHeadTilt ? $"{pitch:F0}deg drive {headDrive:F2}" : "off")}\n" +
            $"speed  {currentSpeed:F2} m/s   grounded {(controller != null && controller.isGrounded ? "yes" : "no")}";
    }

    // ---------------------------------------------------------------- walkable area

    /// <summary>
    /// Pulls the rig back inside the walkable boxes after the move, rather than blocking the
    /// move beforehand. Correcting afterwards is what lets you slide along a wall instead of
    /// sticking to it, and it costs one Vector3 comparison per frame when you are in the open.
    ///
    /// The CharacterController is disabled for the correction: it is the thing that owns the
    /// transform, and writing position while it is enabled is silently ignored on some frames.
    /// </summary>
    void ApplySafeZone()
    {
        if (!useSafeZone || !VRSafeZone.AnyZoneExists()) return;

        Vector3 p = transform.position;
        Vector3 clamped = VRSafeZone.Clamp(p);

        if (safeZoneMargin > 0f && clamped != p)
        {
            // Step back from the edge along the direction we were pushed, so the wearer stops
            // short of the boundary instead of pressed against it.
            Vector3 inward = clamped - p;
            inward.y = 0f;
            if (inward.sqrMagnitude > 0.0001f)
                clamped += inward.normalized * safeZoneMargin;
        }

        if ((clamped - p).sqrMagnitude < 0.000001f) return;

        clamped.y = p.y;

        bool wasEnabled = controller.enabled;
        controller.enabled = false;
        transform.position = clamped;
        controller.enabled = wasEnabled;

        currentSpeed = 0f;   // do not keep accelerating into a wall
    }

    // ---------------------------------------------------------------- footsteps

    float _accelBaseline = 1f;
    float _lastStepTime = -10f;
    float _stepCooldownLeft;
    float _lastDeviation;
    string _accelSource = "none";
    public int StepCount { get; private set; }

    /// <summary>
    /// True while footsteps are still arriving.
    ///
    /// The accelerometer always reads about 1g from gravity alone, and which way that 1g points
    /// changes every time you turn your head - so the magnitude is compared against its own
    /// slow-moving average rather than against 1. That average tracks gravity and any steady
    /// tilt, leaving only the sharp changes, and a footfall is by far the sharpest thing a
    /// walking person produces.
    ///
    /// A cooldown after each detection stops the two halves of one impact counting twice.
    /// </summary>
    bool DetectStepping(float dt)
    {
        float magnitude = ReadAccelerationMagnitude();
        if (magnitude <= 0f) return false;

        // Slow average ~ gravity plus posture. Fast enough to follow you leaning, far too slow
        // to follow a footstep.
        _accelBaseline = Mathf.Lerp(_accelBaseline, magnitude, 1f - Mathf.Exp(-1.5f * dt));

        float deviation = Mathf.Abs(magnitude - _accelBaseline);
        _lastDeviation = Mathf.Max(deviation, _lastDeviation * 0.9f);   // a peak hold, so the
                                                                        // readout is legible
        _stepCooldownLeft -= dt;

        if (deviation > stepThreshold && _stepCooldownLeft <= 0f)
        {
            _stepCooldownLeft = stepCooldown;
            _lastStepTime = Time.time;
            StepCount++;
        }

        return Time.time - _lastStepTime < stopAfterSeconds;
    }

    /// <summary>
    /// The size of whatever the phone is being shaken by, from whichever sensor will report it.
    ///
    /// Four sources, tried in order, because which of them an Android phone exposes to an app is
    /// not something you can know in advance - it varies by manufacturer, by Android version, and
    /// by whether the project is built against the Input System, the old Input Manager, or both.
    /// The original version read one of them and returned zero when it was absent, and a zero
    /// reading is silent: no error, no warning, just a player who never moves. Naming the source
    /// that answered is what turns that silence into something you can read off the screen.
    ///
    /// The linear-acceleration sensor reports motion with gravity already subtracted, so it reads
    /// near zero at rest rather than near 1 g. That does not matter here: every reading is
    /// compared against its own slow average, and the average follows whichever convention the
    /// sensor uses.
    /// </summary>
    float ReadAccelerationMagnitude()
    {
#if ENABLE_INPUT_SYSTEM
        var acc = UnityEngine.InputSystem.Accelerometer.current;
        if (acc != null)
        {
            TryEnable(acc);

            if (acc.enabled)
            {
                _accelSource = "accel";
                return acc.acceleration.ReadValue().magnitude;
            }
        }

        var linear = UnityEngine.InputSystem.LinearAccelerationSensor.current;
        if (linear != null)
        {
            TryEnable(linear);

            if (linear.enabled)
            {
                _accelSource = "linear";
                // Offset by 1 so the baseline starts in the same place as the gravity-inclusive
                // reading, and the first second after launch is not one long false step.
                return linear.acceleration.ReadValue().magnitude + 1f;
            }
        }

        var gravitySensor = UnityEngine.InputSystem.GravitySensor.current;
        if (gravitySensor != null)
        {
            TryEnable(gravitySensor);

            if (gravitySensor.enabled)
            {
                // Gravity alone is smoothed by the OS and will not show a footfall. It is here
                // only so the readout can say the phone has sensors but not the right one.
                _accelSource = "gravity only";
                return gravitySensor.gravity.ReadValue().magnitude;
            }
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        float legacy = Input.acceleration.magnitude;
        if (legacy > 0f)
        {
            _accelSource = "legacy";
            return legacy;
        }
#endif

        _accelSource = "none";
        return 0f;
    }

    // ---------------------------------------------------------------- head steering

    /// <summary>
    /// -1..+1 along the gaze, from head pitch alone. Positive walks forward, negative reverses.
    /// The dead band around level is what lets you look at the ground and stand still.
    /// </summary>
    float HeadDrive(float pitch, float dt)
    {
        if (pitch >= walkTiltAngle)
        {
            backHeldFor = 0f;
            float span = Mathf.Max(1f, fullSpeedTiltAngle - walkTiltAngle);
            return Mathf.Clamp01((pitch - walkTiltAngle) / span);
        }

        if (allowBackward && pitch <= -backTiltAngle)
        {
            backHeldFor += dt;
            return backHeldFor >= backHoldTime ? -backSpeedFactor : 0f;
        }

        backHeldFor = 0f;
        return 0f;
    }

    /// <summary>A quick upward flick, faster than anyone raises their head deliberately.</summary>
    bool NodJump(float pitch, float dt)
    {
        if (!nodToJump || !havePitchSample) return false;

        // pitch falls as the head comes up, so an upward flick is a large negative rate.
        float rate = (pitch - lastPitch) / dt;
        return rate <= -nodJumpSpeed;
    }

    Vector3 HeadForwardFlat()
    {
        Transform t = headTransform != null ? headTransform : transform;
        Vector3 flat = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        if (flat.sqrMagnitude < 0.0001f) flat = Vector3.ProjectOnPlane(t.up, Vector3.up);
        if (flat.sqrMagnitude < 0.0001f) return transform.forward;
        return flat.normalized;
    }

    /// <summary>Degrees the head is pitched below the horizon. Negative when looking up.</summary>
    float HeadPitchDown()
    {
        Transform t = headTransform != null ? headTransform : transform;
        return -Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
    }

    void ApplyTurn(float axis)
    {
        if (turnStyle == TurnStyle.HeadOnly) return;

        if (turnStyle == TurnStyle.Smooth)
        {
            if (Mathf.Abs(axis) > stickDeadzone)
                transform.Rotate(Vector3.up, axis * smoothTurnSpeed * Time.deltaTime, Space.World);
            return;
        }

        snapCooldownLeft -= Time.deltaTime;
        if (Mathf.Abs(axis) > 0.6f && snapCooldownLeft <= 0f)
        {
            transform.Rotate(Vector3.up, Mathf.Sign(axis) * snapTurnAngle, Space.World);
            snapCooldownLeft = snapTurnCooldown;
        }
    }

    // ---------------------------------------------------------------- optional input

    Vector2 ReadMoveStick()
    {
        Vector2 v = Vector2.zero;

#if ENABLE_INPUT_SYSTEM
        if (useGamepad && Gamepad.current != null)
        {
            Vector2 s = Gamepad.current.leftStick.ReadValue();
            if (s.magnitude > stickDeadzone) v += s;
        }

        if (useKeyboardInEditor && Application.isEditor && Keyboard.current != null)
        {
            var k = Keyboard.current;
            if (k.wKey.isPressed || k.upArrowKey.isPressed) v.y += 1f;
            if (k.sKey.isPressed || k.downArrowKey.isPressed) v.y -= 1f;
            if (k.dKey.isPressed || k.rightArrowKey.isPressed) v.x += 1f;
            if (k.aKey.isPressed || k.leftArrowKey.isPressed) v.x -= 1f;
        }
#else
        if (useGamepad) v += new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
#endif

        return Vector2.ClampMagnitude(v, 1f);
    }

    float ReadTurnAxis()
    {
#if ENABLE_INPUT_SYSTEM
        if (useGamepad && Gamepad.current != null)
            return Gamepad.current.rightStick.ReadValue().x;

        if (Application.isEditor && Keyboard.current != null)
        {
            if (Keyboard.current.eKey.isPressed) return 1f;
            if (Keyboard.current.qKey.isPressed) return -1f;
        }
#endif
        return 0f;
    }

    bool ReadSprint()
    {
#if ENABLE_INPUT_SYSTEM
        if (useGamepad && Gamepad.current != null)
            return Gamepad.current.leftShoulder.isPressed || Gamepad.current.buttonEast.isPressed;

        if (Application.isEditor && Keyboard.current != null)
            return Keyboard.current.leftShiftKey.isPressed;
#endif
        return false;
    }

    bool ReadGamepadJump()
    {
#if ENABLE_INPUT_SYSTEM
        if (useGamepad && Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame)
            return true;

        if (Application.isEditor && Keyboard.current != null &&
            Keyboard.current.spaceKey.wasPressedThisFrame)
            return true;
#endif
        return false;
    }

    // ---------------------------------------------------------------- editor look

    void ApplyEditorMouseLook()
    {
        if (!Application.isEditor || !useMouseLookInEditor || headTransform == null) return;
        if (UnityEngine.XR.XRSettings.isDeviceActive) return; // the headset owns the pose

#if ENABLE_INPUT_SYSTEM
        if (Mouse.current == null || !Mouse.current.rightButton.isPressed) return;
        Vector2 d = Mouse.current.delta.ReadValue();
#else
        if (!Input.GetMouseButton(1)) return;
        Vector2 d = new Vector2(Input.GetAxis("Mouse X") * 10f, Input.GetAxis("Mouse Y") * 10f);
#endif

        transform.Rotate(Vector3.up, d.x * mouseSensitivity * 0.1f, Space.World);
        editorPitch = Mathf.Clamp(editorPitch - d.y * mouseSensitivity * 0.1f, -85f, 85f);
        headTransform.localRotation = initialHeadLocalRotation * Quaternion.Euler(editorPitch, 0f, 0f);
    }
}
