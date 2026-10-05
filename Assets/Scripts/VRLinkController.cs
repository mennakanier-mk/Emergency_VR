using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The other phone. Four arrows, a jump, a recentre, and nothing else.
///
/// This is the same APK as the game - installing two builds and keeping them in step is the kind
/// of chore that goes wrong at the worst moment, so the build opens on a chooser instead: one
/// phone taps PLAY IN VR and goes into the viewer, the other taps CONTROLLER and becomes the
/// remote. The choice is remembered, so after the first run each phone goes straight where it
/// belongs.
///
/// The whole UI is built in code. A controller made of prefabs would mean a scene to keep in sync
/// with this script, and there is nothing here worth art-directing.
///
/// Finding the game happens two ways at once. It broadcasts, and the game answers; that covers a
/// normal router. When a broadcast will not pass - a phone hotspot, a router with client
/// isolation, a guest network - the game's address is printed along the bottom of its own screen
/// and can be typed in here, which needs no broadcast at all. The second path exists because the
/// first one fails on exactly the networks people improvise with.
/// </summary>
[AddComponentMenu("Emergency VR/VR Link Controller")]
public class VRLinkController : MonoBehaviour
{
    [Header("Scenes")]
    [Tooltip("Scene loaded by PLAY IN VR. Must be in Build Settings.")]
    public string gameSceneName = "SampleScene";

    [Header("Sending")]
    [Tooltip("Times a second the held arrows are repeated. The receiver stops when these stop, " +
             "so this is also how quickly releasing an arrow registers.")]
    [Range(5f, 60f)] public float sendRate = 20f;

    [Tooltip("Speed the arrows ask for, 0-1. Below 1 for a slower walk.")]
    [Range(0.2f, 1f)] public float arrowStrength = 1f;

    [Header("Start-up")]
    [Tooltip("Show the PLAY / CONTROLLER chooser. Off makes this build controller-only.")]
    public bool showModeChooser = true;

    [Tooltip("Unused: the chooser now shows on every launch.")]
    [HideInInspector] public bool rememberChoice = false;

    const string kPrefMode = "EVR_link_mode";      // 0 unset, 1 game, 2 controller
    const string kPrefHost = "EVR_link_host";

    // ------------------------------------------------------------------ state

    Socket _socket;
    int _seq;
    float _sendTimer, _broadcastTimer;

    IPEndPoint _lockedTarget;          // learnt from the game's reply, or typed in
    string _manualHost = "";
    string _gameName = "";
    float _lastPongTime = -99f;

    List<IPEndPoint> _broadcastTargets = new List<IPEndPoint>();
    byte[] _rx = new byte[256];

    Vector2 _axis;                      // built from which arrows are held
    bool _up, _down, _left, _right;
    bool _jumpHeld, _recenterHeld, _sprint;

    Canvas _canvas;
    GameObject _chooser, _pad;
    Text _status;
    InputField _hostField;

    // ------------------------------------------------------------------ lifecycle

    void Start()
    {
        // Single mode now: no PLAY IN VR / CONTROLLER chooser. If this scene is ever opened,
        // it goes straight into the game.
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        LoadGame();
    }

    // The old two-mode start-up, kept only for reference - nothing calls it any more.
    void StartTwoModeChooser()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        Application.targetFrameRate = 30;      // a d-pad does not need 60

        // The game locks itself to landscape for the viewer. A controller is held in one hand,
        // so let this scene follow the phone.
        Screen.orientation = ScreenOrientation.AutoRotation;
        Screen.autorotateToPortrait = true;
        Screen.autorotateToLandscapeLeft = true;
        Screen.autorotateToLandscapeRight = true;

        _manualHost = PlayerPrefs.GetString(kPrefHost, "");

        BuildUI();

        // The chooser comes up on every launch: each time the user picks VR or Controller.
        // (A choice saved by older builds is cleared so it cannot skip the chooser.)
        PlayerPrefs.DeleteKey(kPrefMode);

        if (!showModeChooser) EnterControllerMode();
        else ShowChooser();
    }

    void OnDestroy()
    {
        CloseSocket();
    }

    void ShowChooser()
    {
        if (_chooser != null) _chooser.SetActive(true);
        if (_pad != null) _pad.SetActive(false);
    }

    void EnterControllerMode()
    {
        if (rememberChoice) { PlayerPrefs.SetInt(kPrefMode, 2); PlayerPrefs.Save(); }

        if (_chooser != null) _chooser.SetActive(false);
        if (_pad != null) _pad.SetActive(true);

        OpenSocket();
        RefreshBroadcastTargets();
        ApplyManualHost(_manualHost);
    }

    void LoadGame()
    {
        if (rememberChoice) { PlayerPrefs.SetInt(kPrefMode, 1); PlayerPrefs.Save(); }

        if (string.IsNullOrEmpty(gameSceneName))
        {
            Debug.LogError("[VRLinkController] no game scene name set.", this);
            ShowChooser();
            return;
        }

        StartCoroutine(LoadGameLandscape());
    }

    /// <summary>
    /// The chooser runs with auto-rotate (portrait allowed). Lock to landscape and wait for the
    /// surface to actually turn BEFORE the city loads, so the stereo rig is built and measures
    /// the phone already sideways - not upright in the hand.
    /// </summary>
    System.Collections.IEnumerator LoadGameLandscape()
    {
        Screen.autorotateToPortrait = false;
        Screen.autorotateToPortraitUpsideDown = false;
        Screen.orientation = ScreenOrientation.LandscapeLeft;

        float until = Time.realtimeSinceStartup + 1.5f;
        while (Screen.height > Screen.width && Time.realtimeSinceStartup < until)
            yield return null;

        SceneManager.LoadScene(gameSceneName);
    }

    /// <summary>Wipes the remembered choice, so the chooser comes back next launch.</summary>
    public void ForgetChoice()
    {
        PlayerPrefs.DeleteKey(kPrefMode);
        PlayerPrefs.Save();
        ShowChooser();
    }

    // ------------------------------------------------------------------ socket

    void OpenSocket()
    {
        if (_socket != null) return;

        try
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.EnableBroadcast = true;
            _socket.Blocking = false;
            _socket.Bind(new IPEndPoint(IPAddress.Any, VRLink.ControllerPort));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VRLinkController] socket failed: {e.Message}", this);
            if (_socket != null) { try { _socket.Close(); } catch { } _socket = null; }
        }
    }

    void CloseSocket()
    {
        if (_socket == null) return;
        try { _socket.Close(); } catch { }
        _socket = null;
    }

    void RefreshBroadcastTargets()
    {
        _broadcastTargets.Clear();

        foreach (var addr in VRLink.BroadcastAddresses())
            _broadcastTargets.Add(new IPEndPoint(addr, VRLink.GamePort));
    }

    void ApplyManualHost(string host)
    {
        _manualHost = host == null ? "" : host.Trim();

        PlayerPrefs.SetString(kPrefHost, _manualHost);
        PlayerPrefs.Save();

        if (_manualHost.Length == 0) { _lockedTarget = null; return; }

        IPAddress parsed;
        if (IPAddress.TryParse(_manualHost, out parsed))
        {
            _lockedTarget = new IPEndPoint(parsed, VRLink.GamePort);
        }
        else
        {
            _lockedTarget = null;
            Debug.LogWarning($"[VRLinkController] '{_manualHost}' is not an address.", this);
        }
    }

    // ------------------------------------------------------------------ per frame

    void Update()
    {
        if (_pad == null || !_pad.activeSelf) return;

        _axis = new Vector2((_right ? 1f : 0f) - (_left ? 1f : 0f),
                            (_up ? 1f : 0f) - (_down ? 1f : 0f));

        if (_axis.sqrMagnitude > 1f) _axis.Normalize();
        _axis *= arrowStrength;

        ReadReplies();

        _sendTimer -= Time.unscaledDeltaTime;
        if (_sendTimer <= 0f)
        {
            _sendTimer = 1f / Mathf.Max(1f, sendRate);
            Send();
        }

        UpdateStatus();
    }

    void Send()
    {
        if (_socket == null) return;

        var buttons = VRLink.Buttons.None;
        if (_jumpHeld) buttons |= VRLink.Buttons.Jump;
        if (_recenterHeld) buttons |= VRLink.Buttons.Recenter;
        if (_sprint) buttons |= VRLink.Buttons.Sprint;

        byte[] packet = VRLink.EncodeInput(++_seq, _axis.x, _axis.y, buttons);

        // Straight to the game once we know where it is. Broadcasting anyway, slowly, so the
        // link comes back by itself if the game phone restarts and picks up a new address.
        bool haveTarget = _lockedTarget != null;

        if (haveTarget)
        {
            try { _socket.SendTo(packet, _lockedTarget); } catch { }
        }

        _broadcastTimer -= Time.unscaledDeltaTime;

        bool searching = !haveTarget || Time.time - _lastPongTime > 2f;

        if (searching || _broadcastTimer <= 0f)
        {
            _broadcastTimer = searching ? 0f : 1.5f;

            for (int i = 0; i < _broadcastTargets.Count; i++)
            {
                try { _socket.SendTo(packet, _broadcastTargets[i]); } catch { }
            }
        }
    }

    void ReadReplies()
    {
        if (_socket == null) return;

        // Non-blocking: ask how much is waiting rather than waiting for it. A controller that
        // stalled on a socket read would stop repeating the held arrows, and the game would come
        // to a halt every time the Wi-Fi hiccuped.
        for (int guard = 0; guard < 8; guard++)
        {
            int available;
            try { available = _socket.Available; } catch { return; }
            if (available <= 0) return;

            int length;
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);

            try { length = _socket.ReceiveFrom(_rx, ref from); }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }

            string name;
            if (!VRLink.TryDecodePong(_rx, length, out name)) continue;

            var sender = from as IPEndPoint;
            if (sender == null) continue;

            _gameName = name;
            _lastPongTime = Time.time;

            // Learnt the hard way: only adopt the discovered address while nothing was typed in.
            // Otherwise a stale reply could drag the link off the phone the person chose.
            if (_manualHost.Length == 0)
                _lockedTarget = new IPEndPoint(sender.Address, VRLink.GamePort);
        }
    }

    void UpdateStatus()
    {
        if (_status == null) return;

        bool live = Time.time - _lastPongTime < 2f;

        if (live)
        {
            _status.color = new Color(0.55f, 1f, 0.6f);
            _status.text = $"connected  -  {_gameName}";
            return;
        }

        _status.color = new Color(1f, 0.8f, 0.45f);

        _status.text = _lockedTarget != null
            ? $"sending to {_lockedTarget.Address}  -  no answer yet"
            : "searching for the game on this Wi-Fi...";
    }

    // ------------------------------------------------------------------ UI

    void BuildUI()
    {
        EnsureEventSystem();

        var go = new GameObject("Controller Canvas");
        go.transform.SetParent(transform, false);

        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 100;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.matchWidthOrHeight = 0.5f;

        go.AddComponent<GraphicRaycaster>();

        var bg = new GameObject("Background");
        bg.transform.SetParent(go.transform, false);
        Stretch(bg.AddComponent<RectTransform>());
        bg.AddComponent<Image>().color = new Color(0.05f, 0.06f, 0.09f, 1f);

        BuildChooser(go.transform);
        BuildPad(go.transform);
    }

    void BuildChooser(Transform parent)
    {
        _chooser = new GameObject("Chooser");
        _chooser.transform.SetParent(parent, false);
        Stretch(_chooser.AddComponent<RectTransform>());

        Label(_chooser.transform, "Title", "EMERGENCY VR",
              new Vector2(0.5f, 0.72f), Vector2.zero, new Vector2(900f, 90f), 58);

        Label(_chooser.transform, "Hint", "this phone is the...",
              new Vector2(0.5f, 0.64f), Vector2.zero, new Vector2(900f, 60f), 32);

        HoldOrClick(_chooser.transform, "Play", "PLAY IN VR",
                    new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(620f, 130f), 44,
                    new Color(0.15f, 0.42f, 0.3f, 1f), LoadGame);

        HoldOrClick(_chooser.transform, "Ctrl", "CONTROLLER",
                    new Vector2(0.5f, 0.36f), Vector2.zero, new Vector2(620f, 130f), 44,
                    new Color(0.2f, 0.3f, 0.5f, 1f), EnterControllerMode);

        Label(_chooser.transform, "Note",
              "both phones must be on the same Wi-Fi",
              new Vector2(0.5f, 0.22f), Vector2.zero, new Vector2(900f, 50f), 26);
    }

    void BuildPad(Transform parent)
    {
        _pad = new GameObject("Pad");
        _pad.transform.SetParent(parent, false);
        Stretch(_pad.AddComponent<RectTransform>());
        _pad.SetActive(false);

        _status = Label(_pad.transform, "Status", "",
                        new Vector2(0.5f, 1f), new Vector2(0f, -70f), new Vector2(1000f, 60f), 32);

        // --- manual address row ---------------------------------------------
        _hostField = TextField(_pad.transform, "Host", _manualHost,
                               new Vector2(0.5f, 1f), new Vector2(-120f, -160f),
                               new Vector2(560f, 92f), 36);

        HoldOrClick(_pad.transform, "Connect", "SET",
                    new Vector2(0.5f, 1f), new Vector2(240f, -160f), new Vector2(160f, 92f), 32,
                    new Color(0.2f, 0.3f, 0.5f, 1f),
                    () => ApplyManualHost(_hostField != null ? _hostField.text : ""));

        Label(_pad.transform, "HostHint", "leave blank to search automatically",
              new Vector2(0.5f, 1f), new Vector2(0f, -218f), new Vector2(1000f, 44f), 24);

        // --- the cross -------------------------------------------------------
        // Anchored to the bottom so it sits under the thumbs, and sized in the same units as
        // the canvas reference so it lands in the same place on a tall or a short screen.
        Vector2 centre = new Vector2(0f, 430f);
        const float size = 210f;
        const float gap = 226f;

        Arrow(_pad.transform, "Up", "▲", centre + new Vector2(0f, gap), size,
              v => _up = v);

        Arrow(_pad.transform, "Down", "▼", centre + new Vector2(0f, -gap), size,
              v => _down = v);

        Arrow(_pad.transform, "Left", "◀", centre + new Vector2(-gap, 0f), size,
              v => _left = v);

        Arrow(_pad.transform, "Right", "▶", centre + new Vector2(gap, 0f), size,
              v => _right = v);

        Label(_pad.transform, "Centre", "MOVE", new Vector2(0.5f, 0f), centre,
              new Vector2(180f, 60f), 28);

        // --- extras ----------------------------------------------------------
        Hold(_pad.transform, "Run", "RUN", new Vector2(0f, 0f), new Vector2(180f, 200f),
             new Vector2(230f, 110f), 32, new Color(0.24f, 0.24f, 0.3f, 1f),
             v => _sprint = v);

        Hold(_pad.transform, "Jump", "JUMP", new Vector2(1f, 0f), new Vector2(-180f, 200f),
             new Vector2(230f, 110f), 32, new Color(0.24f, 0.24f, 0.3f, 1f),
             v => _jumpHeld = v);

        Hold(_pad.transform, "Recentre", "RECENTRE", new Vector2(1f, 1f),
             new Vector2(-180f, -320f), new Vector2(280f, 100f), 28,
             new Color(0.3f, 0.22f, 0.22f, 1f), v => _recenterHeld = v);

        HoldOrClick(_pad.transform, "Back", "MODE", new Vector2(0f, 1f),
                    new Vector2(120f, -70f), new Vector2(180f, 70f), 26,
                    new Color(0.18f, 0.18f, 0.22f, 1f), ForgetChoice);
    }

    // ------------------------------------------------------------------ UI helpers

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
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

    Text Label(Transform parent, string goName, string content, Vector2 anchor,
               Vector2 offset, Vector2 size, int fontSize)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(parent, false);
        Place(go, anchor, offset, size);

        var t = go.AddComponent<Text>();
        t.font = VRLinkReceiver.LinkFont();
        t.fontSize = fontSize;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.text = content;
        t.raycastTarget = false;
        return t;
    }

    GameObject Panel(Transform parent, string goName, Vector2 anchor, Vector2 offset,
                     Vector2 size, Color colour, string content, int fontSize)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(parent, false);
        Place(go, anchor, offset, size);

        var img = go.AddComponent<Image>();
        img.color = colour;

        if (content != null)
        {
            var label = new GameObject("Label");
            label.transform.SetParent(go.transform, false);
            Stretch(label.AddComponent<RectTransform>());

            var t = label.AddComponent<Text>();
            t.font = VRLinkReceiver.LinkFont();
            t.fontSize = fontSize;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.white;
            t.text = content;
            t.raycastTarget = false;
        }

        return go;
    }

    void HoldOrClick(Transform parent, string goName, string content, Vector2 anchor,
                     Vector2 offset, Vector2 size, int fontSize, Color colour,
                     UnityEngine.Events.UnityAction onClick)
    {
        var go = Panel(parent, goName, anchor, offset, size, colour, content, fontSize);

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = go.GetComponent<Image>();
        btn.onClick.AddListener(onClick);
    }

    void Hold(Transform parent, string goName, string content, Vector2 anchor, Vector2 offset,
              Vector2 size, int fontSize, Color colour, Action<bool> onHeld)
    {
        var go = Panel(parent, goName, anchor, offset, size, colour, content, fontSize);

        var hold = go.AddComponent<VRLinkHoldButton>();
        hold.tint = colour;
        hold.onHeld = onHeld;
    }

    void Arrow(Transform parent, string goName, string glyph, Vector2 offsetFromBottom,
               float size, Action<bool> onHeld)
    {
        Hold(parent, goName, glyph, new Vector2(0.5f, 0f), offsetFromBottom,
             new Vector2(size, size), Mathf.RoundToInt(size * 0.42f),
             new Color(0.16f, 0.34f, 0.5f, 1f), onHeld);
    }

    InputField TextField(Transform parent, string goName, string content, Vector2 anchor,
                         Vector2 offset, Vector2 size, int fontSize)
    {
        var go = Panel(parent, goName, anchor, offset, size,
                       new Color(1f, 1f, 1f, 0.12f), null, fontSize);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);

        var trt = textGo.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(20f, 8f);
        trt.offsetMax = new Vector2(-20f, -8f);

        var text = textGo.AddComponent<Text>();
        text.font = VRLinkReceiver.LinkFont();
        text.fontSize = fontSize;
        text.alignment = TextAnchor.MiddleLeft;
        text.color = Color.white;
        text.supportRichText = false;

        var placeholderGo = new GameObject("Placeholder");
        placeholderGo.transform.SetParent(go.transform, false);

        var prt = placeholderGo.AddComponent<RectTransform>();
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = new Vector2(20f, 8f);
        prt.offsetMax = new Vector2(-20f, -8f);

        var placeholder = placeholderGo.AddComponent<Text>();
        placeholder.font = VRLinkReceiver.LinkFont();
        placeholder.fontSize = fontSize;
        placeholder.alignment = TextAnchor.MiddleLeft;
        placeholder.color = new Color(1f, 1f, 1f, 0.4f);
        placeholder.text = "game phone address";

        var field = go.AddComponent<InputField>();
        field.targetGraphic = go.GetComponent<Image>();
        field.textComponent = text;
        field.placeholder = placeholder;
        field.lineType = InputField.LineType.SingleLine;
        field.characterLimit = 15;
        field.contentType = InputField.ContentType.Custom;
        field.keyboardType = TouchScreenKeyboardType.NumbersAndPunctuation;
        field.text = content == null ? "" : content;

        return field;
    }

    static void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;

        var es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
        es.AddComponent<StandaloneInputModule>();
#endif
    }
}

/// <summary>
/// A button that reports held rather than clicked.
///
/// Button's onClick fires on release, which for an arrow key means the player would move for one
/// frame after you let go and not at all while you press. Pointer down and up are the events that
/// match what a d-pad is: a switch that is either closed or open.
///
/// Implemented per pointer, so two thumbs work - holding forward and left together is the whole
/// point of a cross.
/// </summary>
public class VRLinkHoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public Action<bool> onHeld;
    public Color tint = Color.white;

    Image _image;
    int _pointers;

    void Awake()
    {
        _image = GetComponent<Image>();
        if (_image != null) tint = _image.color;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        _pointers++;
        Apply();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        _pointers = Mathf.Max(0, _pointers - 1);
        Apply();
    }

    void OnDisable()
    {
        // A button that is hidden while held would otherwise leave the player walking.
        _pointers = 0;
        Apply();
    }

    void Apply()
    {
        bool held = _pointers > 0;

        if (_image != null)
            _image.color = held ? Color.Lerp(tint, Color.white, 0.45f) : tint;

        if (onHeld != null) onHeld(held);
    }
}
