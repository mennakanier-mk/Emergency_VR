using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Goes on the VR rig, on the phone running the game. Listens for the controller phone and hands
/// its four arrows to PhoneVRLocomotion as an extra input source, alongside the gyro.
///
/// Nothing needs pairing, no account, no internet. Both phones on the same Wi-Fi is the whole
/// requirement. The controller shouts into the network and this answers; if the network will not
/// carry a broadcast - a hotspot, or a router with client isolation - the address printed in the
/// corner of the screen can be typed into the controller instead, and that path always works.
///
/// The socket runs on its own thread. A socket read blocks until something arrives, and doing
/// that on the main thread would stall rendering for as long as the wait, which in VR is not a
/// dropped frame but a wave of nausea. So the thread writes the latest values into plain fields
/// and the main thread reads them; there is no queue, because a controller has no history worth
/// keeping - only its current state matters.
/// </summary>
[AddComponentMenu("Emergency VR/VR Link Receiver")]
public class VRLinkReceiver : MonoBehaviour
{
    [Header("Link")]
    [Tooltip("Turn off to ignore the controller phone entirely.")]
    public bool listen = true;

    [Tooltip("Stop moving if nothing arrives for this long. This is what makes a phone going " +
             "flat, or walking out of Wi-Fi range, a stop rather than a runaway.")]
    [Range(0.2f, 3f)] public float signalTimeout = 0.6f;

    [Header("On screen")]
    [Tooltip("Prints this phone's Wi-Fi address and the link state in the corner, so the number " +
             "to type into the controller is visible from inside the viewer.")]
    public bool showAddress = true;

    [Tooltip("Hide the address once a controller has connected, to keep the view clean.")]
    public bool hideAddressWhenConnected = true;

    [Header("Wiring (optional)")]
    [Tooltip("The stereo rig, so the controller's RECENTRE button works. Found automatically.")]
    public SimpleStereoVR stereo;

    // ------------------------------------------------------------------ public state

    /// <summary>x = strafe, y = forward. Both -1..1. Zero when nothing is connected.</summary>
    public Vector2 Move { get; private set; }

    /// <summary>True while packets are still arriving.</summary>
    public bool Connected { get; private set; }

    /// <summary>True on the frame the controller's jump arrives.</summary>
    public bool JumpPressed { get; private set; }

    public bool Sprint { get; private set; }

    /// <summary>This phone's address on the Wi-Fi, as the controller must be told it.</summary>
    public string Address { get; private set; } = "?";

    public int PacketsReceived { get; private set; }

    // ------------------------------------------------------------------ thread state

    Thread _thread;
    Socket _socket;
    volatile bool _run;

    // Written by the socket thread, read by the main thread. Floats and ints are written
    // atomically on every platform Unity ships, and a torn read of a controller axis would be
    // invisible anyway - it would last one frame and be overwritten 50 ms later.
    volatile float _netX, _netY;
    volatile int _netFlags;
    volatile int _netSeq = -1;
    volatile int _netCount;
    long _netTicks;

    VRLink.Buttons _lastButtons;
    Canvas _canvas;
    Text _label;

    // ------------------------------------------------------------------ lifecycle

    void OnEnable()
    {
        Address = VRLink.LocalAddress();

        if (stereo == null) stereo = GetComponentInParent<SimpleStereoVR>();
        if (stereo == null) stereo = UnityEngine.Object.FindFirstObjectByType<SimpleStereoVR>();

        if (listen) StartListening();
        if (showAddress) BuildLabel();
    }

    void OnDisable()
    {
        StopListening();
        if (_canvas != null) Destroy(_canvas.gameObject);
        _canvas = null;
        _label = null;
    }

    void StartListening()
    {
        if (_thread != null) return;

        try
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.EnableBroadcast = true;

            // A short read timeout is what lets the thread notice _run went false and exit,
            // instead of sitting in a blocking read until the process is killed.
            _socket.ReceiveTimeout = 400;
            _socket.Bind(new IPEndPoint(IPAddress.Any, VRLink.GamePort));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VRLinkReceiver] could not open port {VRLink.GamePort}: {e.Message}", this);

            if (_socket != null) { try { _socket.Close(); } catch { } _socket = null; }
            return;
        }

        _run = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "VRLink" };
        _thread.Start();

        Debug.Log($"[VRLinkReceiver] listening on {Address}:{VRLink.GamePort}", this);
    }

    void StopListening()
    {
        _run = false;

        if (_socket != null) { try { _socket.Close(); } catch { } _socket = null; }

        if (_thread != null)
        {
            if (!_thread.Join(700)) _thread.Interrupt();
            _thread = null;
        }
    }

    void Pump()
    {
        var buffer = new byte[256];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);

        while (_run)
        {
            int length;

            try
            {
                length = _socket.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException)
            {
                continue;   // the read timeout, almost always. Loop and check _run again.
            }
            catch (ObjectDisposedException)
            {
                return;     // socket closed from OnDisable
            }
            catch (ThreadInterruptedException)
            {
                return;
            }

            if (length <= 0) continue;

            int seq; float x, y; VRLink.Buttons buttons;
            if (!VRLink.TryDecodeInput(buffer, length, out seq, out x, out y, out buttons)) continue;

            // Drop a packet that overtook a newer one. The second test lets the counter wrap
            // or the controller restart without the link locking up forever.
            if (seq <= _netSeq && seq > _netSeq - 200) continue;

            _netSeq = seq;
            _netX = Mathf.Clamp(x, -1f, 1f);
            _netY = Mathf.Clamp(y, -1f, 1f);
            _netFlags = (int)buttons;
            _netCount++;

            Interlocked.Exchange(ref _netTicks, DateTime.UtcNow.Ticks);

            // Answer so the controller can say "connected" rather than leaving the person
            // guessing whether the arrows are doing anything.
            try
            {
                var sender = from as IPEndPoint;
                if (sender != null)
                {
                    byte[] pong = VRLink.EncodePong(SystemInfo.deviceModel);
                    _socket.SendTo(pong, new IPEndPoint(sender.Address, VRLink.ControllerPort));
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ per frame

    void Update()
    {
        long ticks = Interlocked.Read(ref _netTicks);
        double age = ticks == 0
            ? double.MaxValue
            : (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;

        bool live = _run && age < signalTimeout;

        Connected = live;
        PacketsReceived = _netCount;
        Move = live ? new Vector2(_netX, _netY) : Vector2.zero;

        var buttons = live ? (VRLink.Buttons)_netFlags : VRLink.Buttons.None;

        JumpPressed = (buttons & VRLink.Buttons.Jump) != 0 &&
                      (_lastButtons & VRLink.Buttons.Jump) == 0;

        Sprint = (buttons & VRLink.Buttons.Sprint) != 0;

        if ((buttons & VRLink.Buttons.Recenter) != 0 &&
            (_lastButtons & VRLink.Buttons.Recenter) == 0 &&
            stereo != null)
        {
            stereo.Recenter();
        }

        _lastButtons = buttons;

        UpdateLabel();
    }

    // ------------------------------------------------------------------ on-screen address

    void BuildLabel()
    {
        var go = new GameObject("VR Link Address");
        go.transform.SetParent(transform, false);

        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 32050;          // under the gear panel, over the eye images

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600f, 800f);
        scaler.matchWidthOrHeight = 0.5f;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);

        var rt = textGo.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, 8f);
        rt.sizeDelta = new Vector2(900f, 40f);

        _label = textGo.AddComponent<Text>();
        _label.font = LinkFont();
        _label.fontSize = 24;
        _label.alignment = TextAnchor.LowerCenter;
        _label.color = new Color(1f, 1f, 1f, 0.75f);
        _label.raycastTarget = false;
        _label.text = "";
    }

    void UpdateLabel()
    {
        if (_label == null) return;

        if (Connected && hideAddressWhenConnected)
        {
            _label.text = "";
            return;
        }

        _label.text = Connected
            ? "controller connected"
            : $"controller: enter  {Address}  on the other phone";
    }

    internal static Font LinkFont()
    {
        Font f = null;
        try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
        if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
        if (f == null) { try { f = Font.CreateDynamicFontFromOSFont("Arial", 24); } catch { } }
        return f;
    }
}
