using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Side-by-side stereo VR for a phone in a Cardboard / VR Box viewer, with no XR plugin.
///
///     head camera (disabled, kept as the head transform + AudioListener)
///       - Eye Left  camera  -> RenderTexture  -\
///       - Eye Right camera  -> RenderTexture  --> overlay Canvas, two RawImages,
///                                                 each drawn through the barrel
///                                                 distortion shader with a round mask
///
/// Three things here exist because they were wrong on device and could not be fixed by rebuilding:
///
/// 1. The distortion material is loaded from Resources/StereoDistortion.mat, not Shader.Find.
///    A shader referenced only by runtime code is stripped from the build, which is why the
///    first APK showed two flat rectangles instead of two round lenses. An asset under a
///    Resources folder is always included, and it drags its shader in with it.
///
/// 2. The sensor-to-Unity conversion is no longer a hardcoded guess. The roll compensation is a
///    preset 0-3 (0/90/180/270 degrees) and the stand-up pitch can be inverted, both adjustable
///    from the on-screen gear panel and saved in PlayerPrefs. Getting this wrong is what made
///    the world appear upside down / looking at the sky, and it cannot be diagnosed from a
///    desk - it has to be dialled in with the phone in the viewer.
///
/// 3. There is on-screen chrome: X to quit, gear to tune, VR to drop back to a single flat
///    view, and the live FOV. Same controls the commercial Cardboard hubs put on screen.
///
/// Head rotation comes from the phone's attitude sensor. In the Editor there is no such sensor,
/// so the view stays locked forward and a warning is logged - expected, not a fault.
///
/// Attach to the rig root. Everything below it is built at runtime. Turn OFF every loader in
/// Project Settings > XR Plug-in Management for every platform, or XR initialises a display and
/// fights this script for the camera.
/// </summary>
[DefaultExecutionOrder(-50)]
public class SimpleStereoVR : MonoBehaviour
{
    public enum SplitMode
    {
        Auto,       // cut along the longer side of the surface
        LeftRight,  // force side by side
        TopBottom   // force one above the other
    }

    [Header("Head")]
    [Tooltip("The camera to split into two eyes. Auto-detected from children if empty.")]
    public Camera headCamera;

    [Tooltip("Interpupillary distance in metres. 0.063-0.065 covers most adults.")]
    public float ipd = 0.064f;

    [Tooltip("Field of view per eye, before distortion. 90-100 is normal for a viewer.")]
    public float eyeFieldOfView = 95f;
    public bool overrideFieldOfView = true;

    [Tooltip("Flip if left and right feel swapped - depth will look inside-out.")]
    public bool swapEyes = false;

    [Header("Screen split")]
    /// <summary>
    /// Which way the screen is cut in two.
    ///
    /// Auto cuts along the LONGER side of the surface, which is the one that runs left-to-right
    /// once the phone is in a landscape viewer. That matters because Android does not always
    /// give the app the landscape surface it asked for - some launchers and some Chinese OEM
    /// skins hand it a portrait surface and rotate the compositor instead. A hardcoded
    /// left/right split then arrives at the eye as one image above the other, which is exactly
    /// what the first APK did.
    /// </summary>
    public SplitMode splitMode = SplitMode.Auto;

    [Tooltip("Rotates what each eye shows, in 90 degree steps. Only needed when the split has " +
             "gone vertical and the world ends up on its side.")]
    [Range(0, 3)] public int viewRotationPreset = 0;

    [Header("Lens distortion")]
    [Tooltip("Off = one plain flat view. On = two round, barrel-warped lenses.")]
    public bool useDistortion = true;

    [Tooltip("Main radial term. Raise until straight lines look straight THROUGH the lens.")]
    [Range(-1f, 1f)] public float k1 = 0.34f;

    [Tooltip("Second radial term, corrects the far edge.")]
    [Range(-1f, 1f)] public float k2 = 0.18f;

    [Tooltip("Below 1 pulls the image in so the warp has margin to work with.")]
    [Range(0.5f, 1.5f)] public float zoom = 0.88f;

    [Tooltip("Shifts each eye's lens centre outward. Raise if the images will not fuse.")]
    [Range(0f, 0.3f)] public float lensCenterOffset = 0.06f;

    [Tooltip("Size of the round porthole. Below 1 shows black corners, which is correct.")]
    [Range(0.5f, 1.5f)] public float maskRadius = 0.95f;
    [Range(0.001f, 0.3f)] public float maskSoftness = 0.03f;

    [Tooltip("Supersampling per eye. The barrel warp resamples the image, which softens it, so " +
             "rendering above 1 and warping down is what keeps it sharp. 1.2 is the sweet spot; " +
             "drop to 1.0 if the frame rate is not holding.")]
    [Range(0.5f, 2f)] public float renderScale = 1.2f;

    [Header("Head tracking")]
    public bool useGyro = true;

    [Tooltip("Roll compensation, in 90 degree steps. The sensor reports relative to portrait " +
             "and the app runs landscape, so one of these four is correct for your phone. " +
             "Cycle it from the gear panel on the device - guessing it here is how the world " +
             "ends up upside down.")]
    [Range(0, 3)] public int orientationPreset = 1;

    [Tooltip("Flips the stand-up pitch. If the view looks at the sky when you look ahead, or " +
             "at the ground, turn this on.")]
    public bool invertPitch = false;

    [Tooltip("Fine pitch trim in degrees, after the preset.")]
    [Range(-45f, 45f)] public float pitchTrim = 0f;

    [Tooltip("0 = raw sensor, higher = smoother but laggier. Keep low; lag causes nausea.")]
    [Range(0f, 30f)] public float smoothing = 12f;

    [Tooltip("Recentres once shortly after launch, so forward is wherever you were facing.")]
    public bool recenterOnStart = true;

    /// <summary>
    /// Which way the city should be in front of you at launch.
    ///
    /// The recentre makes forward wherever your head happens to be pointing, which is right - but
    /// forward in the WORLD is wherever the rig was left facing in the scene, and if that is a
    /// wall you open the app looking at a wall. Nothing is broken when that happens; the view is
    /// simply aimed at the wrong part of the city.
    ///
    /// This turns the whole view without moving the rig or re-exporting, and it is saved, so it
    /// is set once from inside the viewer with the Aim row and then stays.
    /// </summary>
    [Tooltip("Turns the start view without moving the rig. Set it from the Aim row in the gear " +
             "panel while wearing the viewer - it saves, so it only has to be done once.")]
    [Range(0f, 359f)] public float startYaw = 0f;

    [Tooltip("Measures which way up the phone is sitting in the viewer and sets the rotation " +
             "preset from it. Far more reliable than guessing - turn it off only if you want " +
             "to pin the preset by hand.")]
    public bool autoDetectOrientation = true;

    [Header("On-screen controls")]
    [Tooltip("The X / gear / VR / FOV chrome, like the Cardboard game hubs.")]
    public bool showChrome = true;

    [Header("Screen")]
    public bool forceLandscape = true;
    public bool keepScreenAwake = true;
    public int targetFrameRate = 60;

    [Header("Debug")]
    public bool debugTracking = false;

    // ---------------------------------------------------------------- state

    const string kShaderName = "EmergencyVR/StereoBarrelDistortion";
    const string kMaterialResource = "StereoDistortion";
    const string kPrefPrefix = "EmergencyVR.Stereo.";

    Camera _left, _right;
    RenderTexture _leftRT, _rightRT;
    Material _leftMat, _rightMat;
    Canvas _canvas, _chrome;
    Transform _head;

    Quaternion _smoothed = Quaternion.identity;
    float _yawOffset;
    bool _sensorReady, _warnedNoSensor, _stereoOn = true;
    int _builtWidth, _builtHeight;
    float _startTime;
    bool _didAutoRecenter;
    float _settle = 1f;

    Text _hudFov;
    GameObject _panel;

    void Awake()
    {
        if (headCamera == null) headCamera = GetComponentInChildren<Camera>(true);

        // Unity calls Awake even on a disabled component. Switched off (or replaced by the
        // ThirdPersonPlayer) means: no stereo split, no lenses, no VR chrome - one normal view.
        if (!enabled || FindFirstObjectByType<ThirdPersonPlayer>() != null)
        {
            enabled = false;
            return;
        }
        if (headCamera == null)
        {
            Debug.LogError($"[SimpleStereoVR] '{name}': no Camera found under this object.", this);
            enabled = false;
            return;
        }

        _head = headCamera.transform;

        // With no XR loader running, the XRI pose driver pins the camera at identity and fights
        // the gyro. Matched by type name so this file needs no reference to the XR assemblies.
        foreach (var mb in headCamera.GetComponents<MonoBehaviour>())
        {
            if (mb == null || mb == this) continue;
            if (!mb.GetType().Name.Contains("TrackedPoseDriver")) continue;
            mb.enabled = false;
            Debug.Log($"[SimpleStereoVR] Disabled {mb.GetType().Name} on '{headCamera.name}'.", this);
        }

        LoadPrefs();
        Build();
        if (showChrome) BuildChrome();
    }

    void Start()
    {
        _startTime = Time.time;
        if (keepScreenAwake) Screen.sleepTimeout = SleepTimeout.NeverSleep;
        if (forceLandscape) Screen.orientation = ScreenOrientation.LandscapeLeft;
        if (targetFrameRate > 0) Application.targetFrameRate = targetFrameRate;

        EnableSensor();
    }

    void OnDestroy()
    {
        ReleaseTargets();
    }

    void LateUpdate()
    {
        if (Screen.width != _builtWidth || Screen.height != _builtHeight) Build();

        if (!useGyro) return;

        if (!TryReadAttitude(out Quaternion attitude))
        {
            if (!_warnedNoSensor)
            {
                _warnedNoSensor = true;
                Debug.LogWarning("[SimpleStereoVR] No attitude sensor. Head tracking is off; " +
                                 "the view will be locked forward. Expected in the Editor.", this);
            }
            return;
        }

        // The sensor needs a moment after launch before its first values mean anything, so the
        // automatic recentre waits rather than locking forward onto a garbage sample.
        if (recenterOnStart && !_didAutoRecenter && Time.time - _startTime > 1.0f)
        {
            // Measure the roll BEFORE recentring - the recentre is only meaningful once the
            // view is the right way up. The phone may still be upright in the hand while it is
            // being put into the viewer, so wait until it is really lying sideways and steady
            // (up to 12 s) instead of trusting the first reading.
            if (!autoDetectOrientation) { _didAutoRecenter = true; Recenter(); }
            else if (TryLandscapeRoll(out int preset))
            {
                _didAutoRecenter = true;
                orientationPreset = preset;
                SavePrefs();
                Recenter();
                Debug.Log($"[SimpleStereoVR] Phone is sideways and steady -> orientation preset {preset}.", this);
            }
            else if (Time.time - _startTime > 6f)
            {
                _didAutoRecenter = true;
                Recenter();
            }
        }

        // Until the start-up recentre has happened the sensor's numbers point anywhere (and the
        // orientation may still be switching), which showed as a shaking view at a wrong angle
        // right after the app opened. Hold a calm, level view straight ahead until then, and
        // follow the sensor exactly (no smoothing lag), then ease into head tracking.
        Quaternion level = Quaternion.Euler(0f, startYaw, 0f);
        if (recenterOnStart && !_didAutoRecenter)
        {
            _smoothed = attitude;
            _settle = 0f;
            _head.localRotation = level;
            return;
        }

        _smoothed = smoothing <= 0.01f
            ? attitude
            : Quaternion.Slerp(_smoothed, attitude, 1f - Mathf.Exp(-smoothing * Time.deltaTime));

        // _yawOffset is what the recentre measured; startYaw is the fixed aim on top of it, so
        // turning Aim in the panel swings the view there and then while the recentre still does
        // its job.
        Quaternion tracked = Quaternion.Euler(0f, _yawOffset + startYaw, 0f) * _smoothed;
        _settle = Mathf.MoveTowards(_settle, 1f, Time.deltaTime / 0.6f);
        _head.localRotation = _settle >= 1f ? tracked
            : Quaternion.Slerp(level, tracked, _settle * _settle * (3f - 2f * _settle));

        if (debugTracking && Time.frameCount % 60 == 0)
            Debug.Log($"[SimpleStereoVR] head euler {_head.localRotation.eulerAngles}", this);
    }

    // ---------------------------------------------------------------- build

    void Build()
    {
        _builtWidth = Mathf.Max(2, Screen.width);
        _builtHeight = Mathf.Max(2, Screen.height);

        ReleaseTargets();

        bool stereo = _stereoOn;

        if (!stereo)
        {
            DestroyCanvas();
            DestroyEye("Eye Left");
            DestroyEye("Eye Right");
            _left = _right = null;
            headCamera.enabled = true;
            headCamera.rect = new Rect(0f, 0f, 1f, 1f);
            if (overrideFieldOfView) headCamera.fieldOfView = eyeFieldOfView;
            return;
        }

        _left = CreateEye("Eye Left", -1f, ref _leftRT);
        _right = CreateEye("Eye Right", +1f, ref _rightRT);

        headCamera.enabled = false;

        if (useDistortion) BuildOverlay();
        else BuildDirectViewports();

        ApplyMaterialSettings();
    }

    void DestroyEye(string eyeName)
    {
        var existing = _head.Find(eyeName);
        if (existing != null) DestroyImmediate(existing.gameObject);
    }

    Camera CreateEye(string eyeName, float side, ref RenderTexture rt)
    {
        DestroyEye(eyeName);

        var go = new GameObject(eyeName);
        go.transform.SetParent(_head, false);

        var cam = go.AddComponent<Camera>();
        cam.CopyFrom(headCamera);
        cam.enabled = true;
        cam.targetTexture = null;
        cam.rect = new Rect(0f, 0f, 1f, 1f);

        if (overrideFieldOfView) cam.fieldOfView = eyeFieldOfView;

        float sign = swapEyes ? -side : side;
        go.transform.localPosition = new Vector3(sign * ipd * 0.5f, 0f, 0f);
        go.transform.localRotation = Quaternion.identity;

        var listener = go.GetComponent<AudioListener>();
        if (listener != null) Destroy(listener);

        if (useDistortion)
        {
            // The eye texture is sized to what the WEARER sees, not to the screen: half the
            // long side across, the short side down. With the split vertical those are the
            // screen's height and width the other way round, and getting this wrong squashes
            // the image even when the split itself is correct.
            int w = Mathf.Max(2, Mathf.RoundToInt(LongPixels() * 0.5f * renderScale));
            int h = Mathf.Max(2, Mathf.RoundToInt(ShortPixels() * renderScale));

            // Match the project's MSAA instead of hardcoding 1. Building edges against the sky
            // are the first thing that looks cheap in a viewer, and an unantialiased eye texture
            // threw away the MSAA the URP asset was already paying for.
            int aa = Mathf.Max(1, QualitySettings.antiAliasing);
            if (aa != 1 && aa != 2 && aa != 4 && aa != 8) aa = 4;

            rt = new RenderTexture(w, h, 24, RenderTextureFormat.Default)
            {
                name = eyeName + " RT",
                filterMode = FilterMode.Bilinear,
                antiAliasing = aa,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            cam.targetTexture = rt;
        }

        return cam;
    }

    /// <summary>Plain half-screen viewports. No warp, no round mask - the fallback.</summary>
    void BuildDirectViewports()
    {
        DestroyCanvas();

        if (SplitVertical())
        {
            _left.rect = new Rect(0f, 0.5f, 1f, 0.5f);
            _right.rect = new Rect(0f, 0f, 1f, 0.5f);
        }
        else
        {
            _left.rect = new Rect(0f, 0f, 0.5f, 1f);
            _right.rect = new Rect(0.5f, 0f, 0.5f, 1f);
        }
    }

    // ---------------------------------------------------------------- split geometry

    /// <summary>
    /// True when the two eyes are stacked in SCREEN space - which is what you want when the
    /// surface is portrait but the phone is lying on its side in the viewer.
    /// </summary>
    bool SplitVertical()
    {
        switch (splitMode)
        {
            case SplitMode.LeftRight: return false;
            case SplitMode.TopBottom: return true;
            default: return _builtHeight > _builtWidth;
        }
    }

    int LongPixels()  { return SplitVertical() ? _builtHeight : _builtWidth; }
    int ShortPixels() { return SplitVertical() ? _builtWidth : _builtHeight; }

    /// <summary>
    /// Degrees to spin each eye's image so the world stands upright for the wearer. A vertical
    /// split means the phone is turned 90 degrees inside the viewer, so the picture has to turn
    /// with it; the preset adds further quarter turns for the phones that turn the other way.
    /// </summary>
    float ViewRotation()
    {
        return (SplitVertical() ? 90f : 0f) + viewRotationPreset * 90f;
    }

    /// <summary>
    /// Resolves the distortion shader. Resources first: a material asset under a Resources
    /// folder survives build stripping, a Shader.Find on a shader nothing references does not.
    /// </summary>
    Shader ResolveShader()
    {
        var asset = Resources.Load<Material>(kMaterialResource);
        if (asset != null && asset.shader != null) return asset.shader;

        var found = Shader.Find(kShaderName);
        if (found != null && asset == null)
            Debug.LogWarning("[SimpleStereoVR] Resources/" + kMaterialResource + ".mat is missing. " +
                             "The shader was found in the Editor, but a build will strip it and " +
                             "fall back to flat rectangles. Run " +
                             "Tools > Emergency VR > Setup Simple Stereo VR to create it.", this);
        return found;
    }

    void BuildOverlay()
    {
        DestroyCanvas();

        var shader = ResolveShader();
        if (shader == null)
        {
            Debug.LogError($"[SimpleStereoVR] Shader '{kShaderName}' not found at all. Falling " +
                           "back to plain side-by-side. Check that " +
                           "Assets/Shaders/StereoBarrelDistortion.shader compiles.", this);
            useDistortion = false;
            _left.targetTexture = null;
            _right.targetTexture = null;
            BuildDirectViewports();
            return;
        }

        var canvasGo = new GameObject("Stereo Present Canvas");
        canvasGo.transform.SetParent(transform, false);

        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 32000;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

        _leftMat = new Material(shader) { name = "Eye Left Distortion" };
        _rightMat = new Material(shader) { name = "Eye Right Distortion" };
        _leftMat.mainTexture = _leftRT;
        _rightMat.mainTexture = _rightRT;

        CreateEyeImage(canvasGo.transform, "Left Eye Image", 0, _leftMat, _leftRT);
        CreateEyeImage(canvasGo.transform, "Right Eye Image", 1, _rightMat, _rightRT);
    }

    /// <summary>
    /// Each eye is a stretched slot covering its half of the screen, plus an image inside it
    /// that is sized in pixels and may be rotated. The two-object structure is what lets the
    /// image turn 90 degrees and still fill the slot exactly: a stretched RectTransform keeps
    /// its stretched size when you rotate it and spills over the edge.
    /// </summary>
    RawImage CreateEyeImage(Transform parent, string imageName, int half,
                            Material mat, RenderTexture rt)
    {
        bool vertical = SplitVertical();

        var slotGo = new GameObject(imageName + " Slot");
        slotGo.transform.SetParent(parent, false);

        var slot = slotGo.AddComponent<RectTransform>();
        if (vertical)
        {
            // half 0 = top of the screen.
            slot.anchorMin = new Vector2(0f, half == 0 ? 0.5f : 0f);
            slot.anchorMax = new Vector2(1f, half == 0 ? 1f : 0.5f);
        }
        else
        {
            slot.anchorMin = new Vector2(half == 0 ? 0f : 0.5f, 0f);
            slot.anchorMax = new Vector2(half == 0 ? 0.5f : 1f, 1f);
        }
        slot.offsetMin = Vector2.zero;
        slot.offsetMax = Vector2.zero;

        var go = new GameObject(imageName);
        go.transform.SetParent(slotGo.transform, false);

        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(LongPixels() * 0.5f, ShortPixels());
        rect.localEulerAngles = new Vector3(0f, 0f, ViewRotation());

        var img = go.AddComponent<RawImage>();
        img.texture = rt;
        img.material = mat;
        img.raycastTarget = false;
        return img;
    }

    void ApplyMaterialSettings()
    {
        SetMat(_leftMat, -1f);
        SetMat(_rightMat, +1f);
    }

    void SetMat(Material m, float side)
    {
        if (m == null) return;
        m.SetFloat("_K1", k1);
        m.SetFloat("_K2", k2);
        m.SetFloat("_Zoom", zoom);
        // Each lens sits outboard of its half's centre, so the centre shifts the opposite way.
        m.SetFloat("_CenterOffset", -side * lensCenterOffset);
        m.SetFloat("_MaskRadius", maskRadius);
        m.SetFloat("_MaskSoftness", maskSoftness);
    }

    void DestroyCanvas()
    {
        if (_canvas != null) Destroy(_canvas.gameObject);
        _canvas = null;
    }

    void ReleaseTargets()
    {
        if (_left != null) _left.targetTexture = null;
        if (_right != null) _right.targetTexture = null;

        if (_leftRT != null) { _leftRT.Release(); Destroy(_leftRT); _leftRT = null; }
        if (_rightRT != null) { _rightRT.Release(); Destroy(_rightRT); _rightRT = null; }

        if (_leftMat != null) { Destroy(_leftMat); _leftMat = null; }
        if (_rightMat != null) { Destroy(_rightMat); _rightMat = null; }
    }

    // ---------------------------------------------------------------- recentre

    /// <summary>Re-aims the view forward. Auto-called after launch, and on a 1.2s screen hold.</summary>
    public void Recenter()
    {
        if (!TryReadAttitude(out Quaternion attitude)) return;

        // Yaw taken from where the view POINTS, flattened, rather than from eulerAngles.y.
        // Euler decomposition is unstable near the poles, and a phone standing upright in a
        // headset sits close to one: eulerAngles.y flips between 0 and 360 from one frame to
        // the next, which put a random yaw offset into the recentre.
        Vector3 f = attitude * Vector3.forward;
        f.y = 0f;

        if (f.sqrMagnitude < 0.0001f)
        {
            // Looking straight up or down - no yaw to read. Use the head's up instead.
            f = attitude * Vector3.up;
            f.y = 0f;
            if (f.sqrMagnitude < 0.0001f) return;
        }

        _yawOffset = -Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        _smoothed = attitude;
    }

    /// <summary>
    /// Works out the roll compensation by MEASURING which way is down, instead of guessing.
    ///
    /// This is the setting that caused the worst of it. With the roll wrong by a quarter turn,
    /// the world appears on its side AND head pitch comes out as view yaw - so nodding spins the
    /// whole city, which is what "it rotates 360 degrees at any time" was.
    ///
    /// Gravity in device coordinates says exactly how the phone is lying in the viewer: for a
    /// phone held upright in portrait it reads about (0, -1, 0), and every quarter turn moves it
    /// a quarter turn round. Rounding that angle to the nearest 90 gives the preset directly.
    /// </summary>
    public void AutoDetectOrientation()
    {
        if (!TryReadGravity(out Vector3 g)) return;

        // Only the part of gravity lying in the screen plane says anything about roll.
        Vector2 inScreen = new Vector2(g.x, g.y);
        if (inScreen.sqrMagnitude < 0.2f) return;   // phone lying flat - nothing to measure

        float angle = Mathf.Atan2(g.x, -g.y) * Mathf.Rad2Deg;
        int preset = Mathf.RoundToInt(angle / 90f);

        orientationPreset = ((preset % 4) + 4) % 4;

        SavePrefs();
        Recenter();

        Debug.Log($"[SimpleStereoVR] Measured gravity {g}, roll {angle:0}deg " +
                  $"-> orientation preset {orientationPreset}.", this);
    }

    float _steadySince = -1f;
    int _steadyPreset = -1;

    /// <summary>
    /// The roll preset, but only once the phone is clearly in LANDSCAPE (gravity along the long
    /// side) and has stayed that way for half a second. Portrait readings are ignored: the app
    /// runs landscape, so a portrait reading only means the phone is still in the hand.
    /// </summary>
    bool TryLandscapeRoll(out int preset)
    {
        preset = orientationPreset;
        if (!TryReadGravity(out Vector3 g)) return false;

        float m = g.magnitude;
        if (m < 0.1f) return false;
        Vector3 n = g / m;

        bool sideways = Mathf.Abs(n.x) > 0.75f && Mathf.Abs(n.y) < 0.5f;
        if (!sideways) { _steadySince = -1f; return false; }

        int p = Mathf.RoundToInt(Mathf.Atan2(n.x, -n.y) * Mathf.Rad2Deg / 90f);
        p = ((p % 4) + 4) % 4;

        if (p != _steadyPreset) { _steadyPreset = p; _steadySince = Time.time; return false; }
        if (Time.time - _steadySince < 0.5f) return false;

        preset = p;
        return true;
    }

    bool TryReadGravity(out Vector3 g)
    {
        g = Vector3.zero;

#if ENABLE_INPUT_SYSTEM
        if (GravitySensor.current != null)
        {
            if (!GravitySensor.current.enabled) InputSystem.EnableDevice(GravitySensor.current);
            g = GravitySensor.current.gravity.ReadValue();
            if (g.sqrMagnitude > 0.1f) return true;
        }

        if (Accelerometer.current != null)
        {
            if (!Accelerometer.current.enabled) InputSystem.EnableDevice(Accelerometer.current);
            g = Accelerometer.current.acceleration.ReadValue();
            if (g.sqrMagnitude > 0.1f) return true;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        g = Input.acceleration;
        if (g.sqrMagnitude > 0.1f) return true;
#endif

        return false;
    }

    // ---------------------------------------------------------------- sensor

    void EnableSensor()
    {
#if ENABLE_INPUT_SYSTEM
        if (AttitudeSensor.current != null)
        {
            if (!AttitudeSensor.current.enabled) InputSystem.EnableDevice(AttitudeSensor.current);
            _sensorReady = true;
            Debug.Log("[SimpleStereoVR] Using AttitudeSensor for head tracking.", this);
            return;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        if (SystemInfo.supportsGyroscope)
        {
            Input.gyro.enabled = true;
            _sensorReady = true;
            Debug.Log("[SimpleStereoVR] Using legacy Input.gyro for head tracking.", this);
            return;
        }
#endif
        _sensorReady = false;
    }

    bool TryReadAttitude(out Quaternion result)
    {
        result = Quaternion.identity;
        if (!_sensorReady) return false;

        Quaternion raw;

#if ENABLE_INPUT_SYSTEM
        if (AttitudeSensor.current != null)
        {
            raw = AttitudeSensor.current.attitude.ReadValue();
        }
        else
#endif
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!SystemInfo.supportsGyroscope) return false;
            raw = Input.gyro.attitude;
#else
            return false;
#endif
        }

        // The sensor reports right-handed, relative to the portrait screen, with identity when
        // the phone lies flat. Flip handedness, stand it up, then roll into landscape.
        //
        // orientationPreset is the roll and it is genuinely per-device: Android reports the
        // attitude in the phone's NATURAL orientation, which is portrait on most phones and
        // landscape on some tablets, and Unity's forced LandscapeLeft does not rotate it.
        // Four discrete values, cycled from the gear panel, beats guessing one here.
        Quaternion unitySpace = new Quaternion(raw.x, raw.y, -raw.z, -raw.w);

        float standUp = (invertPitch ? -90f : 90f) + pitchTrim;

        result = Quaternion.Euler(standUp, 0f, 0f)
               * unitySpace
               * Quaternion.Euler(0f, 0f, orientationPreset * 90f);
        return true;
    }

    // ---------------------------------------------------------------- preferences

    void LoadPrefs()
    {
        orientationPreset = PlayerPrefs.GetInt(kPrefPrefix + "orient", orientationPreset);
        invertPitch = PlayerPrefs.GetInt(kPrefPrefix + "invertPitch", invertPitch ? 1 : 0) == 1;
        eyeFieldOfView = PlayerPrefs.GetFloat(kPrefPrefix + "fov", eyeFieldOfView);
        k1 = PlayerPrefs.GetFloat(kPrefPrefix + "k1", k1);
        ipd = PlayerPrefs.GetFloat(kPrefPrefix + "ipd", ipd);
        pitchTrim = PlayerPrefs.GetFloat(kPrefPrefix + "pitchTrim", pitchTrim);
        startYaw = PlayerPrefs.GetFloat(kPrefPrefix + "startYaw", startYaw);
        splitMode = (SplitMode)PlayerPrefs.GetInt(kPrefPrefix + "split", (int)splitMode);
        viewRotationPreset = PlayerPrefs.GetInt(kPrefPrefix + "viewRot", viewRotationPreset);
    }

    void SavePrefs()
    {
        PlayerPrefs.SetInt(kPrefPrefix + "orient", orientationPreset);
        PlayerPrefs.SetInt(kPrefPrefix + "invertPitch", invertPitch ? 1 : 0);
        PlayerPrefs.SetFloat(kPrefPrefix + "fov", eyeFieldOfView);
        PlayerPrefs.SetFloat(kPrefPrefix + "k1", k1);
        PlayerPrefs.SetFloat(kPrefPrefix + "ipd", ipd);
        PlayerPrefs.SetFloat(kPrefPrefix + "pitchTrim", pitchTrim);
        PlayerPrefs.SetFloat(kPrefPrefix + "startYaw", startYaw);
        PlayerPrefs.SetInt(kPrefPrefix + "split", (int)splitMode);
        PlayerPrefs.SetInt(kPrefPrefix + "viewRot", viewRotationPreset);
        PlayerPrefs.Save();
    }

    // ---------------------------------------------------------------- on-screen chrome

    static Font ChromeFont()
    {
        Font f = null;
        try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
        if (f == null) { try { f = Font.CreateDynamicFontFromOSFont("Arial", 28); } catch { } }
        return f;
    }

    void BuildChrome()
    {
        EnsureEventSystem();

        var go = new GameObject("VR Chrome Canvas");
        go.transform.SetParent(transform, false);

        _chrome = go.AddComponent<Canvas>();
        _chrome.renderMode = RenderMode.ScreenSpaceOverlay;
        _chrome.sortingOrder = 32100;          // above the two eye images

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600f, 800f);
        scaler.matchWidthOrHeight = 0.5f;

        go.AddComponent<GraphicRaycaster>();

        _hudFov = MakeLabel(go.transform, "FOV", Mathf.RoundToInt(eyeFieldOfView).ToString(),
                              new Vector2(0.5f, 1f), new Vector2(0f, -34f), new Vector2(120f, 46f), 30);

        MakeButton(go.transform, "Close", "X", new Vector2(1f, 1f), new Vector2(-40f, -34f),
                   new Vector2(52f, 46f), () => Application.Quit());

        MakeButton(go.transform, "Settings", "SET", new Vector2(0f, 1f), new Vector2(46f, -34f),
                   new Vector2(72f, 46f), TogglePanel);

        MakeButton(go.transform, "VRToggle", "VR", new Vector2(0.5f, 0f), new Vector2(0f, 38f),
                   new Vector2(78f, 46f), ToggleStereo);

        BuildPanel(go.transform);
    }

    static void EnsureEventSystem()
    {
        if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() != null) return;

        var es = new GameObject("EventSystem");
        es.AddComponent<UnityEngine.EventSystems.EventSystem>();
#if ENABLE_INPUT_SYSTEM
        es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
        es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
#endif
    }

    static RectTransform Place(GameObject go, Vector2 anchor, Vector2 offset, Vector2 size)
    {
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = offset;
        rt.sizeDelta = size;
        return rt;
    }

    Text MakeLabel(Transform parent, string goName, string content, Vector2 anchor,
                   Vector2 offset, Vector2 size, int fontSize)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(parent, false);
        Place(go, anchor, offset, size);

        var t = go.AddComponent<Text>();
        t.font = ChromeFont();
        t.fontSize = fontSize;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.text = content;
        t.raycastTarget = false;
        return t;
    }

    Button MakeButton(Transform parent, string goName, string content, Vector2 anchor,
                      Vector2 offset, Vector2 size, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(parent, false);
        Place(go, anchor, offset, size);

        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.55f);

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);

        var label = new GameObject("Label");
        label.transform.SetParent(go.transform, false);
        var lrt = label.AddComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;

        var t = label.AddComponent<Text>();
        t.font = ChromeFont();
        t.fontSize = 26;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.text = content;
        t.raycastTarget = false;

        return btn;
    }

    void BuildPanel(Transform parent)
    {
        _panel = new GameObject("Settings Panel");
        _panel.transform.SetParent(parent, false);
        Place(_panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 700f));

        var bg = _panel.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.85f);

        float y = 300f;
        const float step = 58f;

        MakeLabel(_panel.transform, "Title", "VIEWER SETUP", new Vector2(0.5f, 0.5f),
                  new Vector2(0f, y), new Vector2(520f, 44f), 28);
        y -= step;

        MakeRow("Rotation", ref y,
            () => { orientationPreset = (orientationPreset + 3) % 4; AfterTweak(); },
            () => { orientationPreset = (orientationPreset + 1) % 4; AfterTweak(); },
            () => (orientationPreset * 90) + "°");

        MakeRow("Aim", ref y,
            () => { startYaw = Mathf.Repeat(startYaw - 15f, 360f); AfterTweak(); },
            () => { startYaw = Mathf.Repeat(startYaw + 15f, 360f); AfterTweak(); },
            () => Mathf.RoundToInt(startYaw) + "°");

        MakeRow("Tilt", ref y,
            () => { pitchTrim = Mathf.Clamp(pitchTrim - 5f, -45f, 45f); AfterTweak(); },
            () => { pitchTrim = Mathf.Clamp(pitchTrim + 5f, -45f, 45f); AfterTweak(); },
            () => Mathf.RoundToInt(pitchTrim) + "°");

        MakeRow("FOV", ref y,
            () => { eyeFieldOfView = Mathf.Clamp(eyeFieldOfView - 5f, 60f, 120f); AfterTweak(true); },
            () => { eyeFieldOfView = Mathf.Clamp(eyeFieldOfView + 5f, 60f, 120f); AfterTweak(true); },
            () => Mathf.RoundToInt(eyeFieldOfView).ToString());

        MakeRow("Lens", ref y,
            () => { k1 = Mathf.Clamp(k1 - 0.04f, -0.2f, 0.9f); AfterTweak(); },
            () => { k1 = Mathf.Clamp(k1 + 0.04f, -0.2f, 0.9f); AfterTweak(); },
            () => k1.ToString("0.00"));

        MakeRow("IPD", ref y,
            () => { ipd = Mathf.Clamp(ipd - 0.002f, 0.045f, 0.085f); AfterTweak(true); },
            () => { ipd = Mathf.Clamp(ipd + 0.002f, 0.045f, 0.085f); AfterTweak(true); },
            () => (ipd * 1000f).ToString("0") + "mm");

        MakeRow("Split", ref y,
            () => { splitMode = (SplitMode)(((int)splitMode + 2) % 3); AfterTweak(true); },
            () => { splitMode = (SplitMode)(((int)splitMode + 1) % 3); AfterTweak(true); },
            () => splitMode == SplitMode.Auto ? "AUTO"
                 : splitMode == SplitMode.LeftRight ? "L | R" : "T / B");

        MakeRow("View", ref y,
            () => { viewRotationPreset = (viewRotationPreset + 3) % 4; AfterTweak(true); },
            () => { viewRotationPreset = (viewRotationPreset + 1) % 4; AfterTweak(true); },
            () => Mathf.RoundToInt(ViewRotation()) + "°");

        MakeButton(_panel.transform, "Auto", "AUTO LEVEL", new Vector2(0.5f, 0.5f),
                   new Vector2(-140f, y), new Vector2(250f, 46f),
                   () => { AutoDetectOrientation(); AfterTweak(); });

        MakeButton(_panel.transform, "Center", "RECENTRE", new Vector2(0.5f, 0.5f),
                   new Vector2(140f, y), new Vector2(250f, 46f), Recenter);
        y -= step;

        MakeButton(_panel.transform, "Flip", "FLIP UP/DOWN", new Vector2(0.5f, 0.5f),
                   new Vector2(0f, y), new Vector2(250f, 46f),
                   () => { invertPitch = !invertPitch; AfterTweak(); });
        y -= step;

        MakeButton(_panel.transform, "Done", "DONE", new Vector2(0.5f, 0.5f),
                   new Vector2(0f, y), new Vector2(250f, 46f), TogglePanel);

        _panel.SetActive(false);
    }

    /// <summary>One settings row: label, [-], value, [+]. Returns the value Text to refresh.</summary>
    Text MakeRow(string rowLabel, ref float y,
                 UnityEngine.Events.UnityAction minus, UnityEngine.Events.UnityAction plus,
                 System.Func<string> read)
    {
        MakeLabel(_panel.transform, rowLabel + " Name", rowLabel, new Vector2(0.5f, 0.5f),
                  new Vector2(-190f, y), new Vector2(160f, 44f), 24);

        MakeButton(_panel.transform, rowLabel + " Minus", "<", new Vector2(0.5f, 0.5f),
                   new Vector2(-70f, y), new Vector2(52f, 44f), minus);

        var value = MakeLabel(_panel.transform, rowLabel + " Value", read(), new Vector2(0.5f, 0.5f),
                              new Vector2(30f, y), new Vector2(140f, 44f), 24);

        MakeButton(_panel.transform, rowLabel + " Plus", ">", new Vector2(0.5f, 0.5f),
                   new Vector2(130f, y), new Vector2(52f, 44f), plus);

        _rowReaders.Add(new Row { text = value, read = read });

        y -= 58f;
        return value;
    }

    struct Row { public Text text; public System.Func<string> read; }
    readonly System.Collections.Generic.List<Row> _rowReaders =
        new System.Collections.Generic.List<Row>();

    void AfterTweak(bool rebuild = false)
    {
        SavePrefs();

        if (rebuild) Build();
        else ApplyMaterialSettings();

        if (!rebuild && _left != null && _right != null && overrideFieldOfView)
        {
            _left.fieldOfView = eyeFieldOfView;
            _right.fieldOfView = eyeFieldOfView;
        }

        foreach (var row in _rowReaders)
            if (row.text != null) row.text.text = row.read();

        if (_hudFov != null) _hudFov.text = Mathf.RoundToInt(eyeFieldOfView).ToString();
    }

    void TogglePanel()
    {
        if (_panel == null) return;
        _panel.SetActive(!_panel.activeSelf);
        if (_panel.activeSelf) AfterTweak();
    }

    /// <summary>VR button: two round lenses, or one plain flat view for taking the phone out.</summary>
    void ToggleStereo()
    {
        _stereoOn = !_stereoOn;
        Build();
    }

    // ---------------------------------------------------------------- live tuning

    void OnValidate()
    {
        if (!Application.isPlaying) return;

        if (_left != null && _right != null)
        {
            float s = swapEyes ? -1f : 1f;
            _left.transform.localPosition = new Vector3(-s * ipd * 0.5f, 0f, 0f);
            _right.transform.localPosition = new Vector3(s * ipd * 0.5f, 0f, 0f);

            if (overrideFieldOfView)
            {
                _left.fieldOfView = eyeFieldOfView;
                _right.fieldOfView = eyeFieldOfView;
            }
        }

        ApplyMaterialSettings();
    }

    [ContextMenu("Rebuild Stereo Rig")]
    public void RebuildNow()
    {
        if (Application.isPlaying) Build();
    }

    [ContextMenu("Clear Saved Viewer Settings")]
    public void ClearPrefs()
    {
        PlayerPrefs.DeleteKey(kPrefPrefix + "orient");
        PlayerPrefs.DeleteKey(kPrefPrefix + "invertPitch");
        PlayerPrefs.DeleteKey(kPrefPrefix + "fov");
        PlayerPrefs.DeleteKey(kPrefPrefix + "k1");
        PlayerPrefs.DeleteKey(kPrefPrefix + "ipd");
        PlayerPrefs.DeleteKey(kPrefPrefix + "pitchTrim");
        PlayerPrefs.Save();
    }
}
