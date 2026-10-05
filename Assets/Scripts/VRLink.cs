using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// The wire format for the two-phone link: one phone runs the game, the other runs the same APK
/// in controller mode and sends it the four arrows.
///
/// UDP, not TCP, and plain text at that. Both choices are deliberate.
///
/// UDP because a controller has no state worth protecting. If a packet is lost the next one
/// arrives 50 ms later carrying the same "forward is held" - there is nothing to retransmit and
/// nothing to acknowledge. TCP would instead stall the whole stream waiting for the lost packet,
/// so a moment of Wi-Fi interference would show up as the player freezing and then lurching
/// forward, which is exactly the thing that makes a wireless controller feel broken.
///
/// Plain text because when this does not work the problem will be the network, not the parser,
/// and a packet you can read is one you can diagnose.
///
/// The controller repeats its state 20 times a second rather than sending "pressed" and
/// "released" events. So a lost release cannot leave the player walking forever: the receiver
/// stops on its own the moment the packets stop arriving.
/// </summary>
public static class VRLink
{
    /// <summary>Port the game listens on.</summary>
    public const int GamePort = 48711;

    /// <summary>Port the controller listens on, for the game's reply.</summary>
    public const int ControllerPort = 48712;

    const string kInput = "EVR1";
    const string kPong  = "EVRP";

    [Flags]
    public enum Buttons
    {
        None     = 0,
        Recenter = 1,
        Jump     = 2,
        Sprint   = 4
    }

    // ------------------------------------------------------------------ packets

    public static byte[] EncodeInput(int seq, float x, float y, Buttons buttons)
    {
        string s = string.Format(CultureInfo.InvariantCulture,
                                 "{0} {1} {2:F3} {3:F3} {4}",
                                 kInput, seq, x, y, (int)buttons);
        return Encoding.ASCII.GetBytes(s);
    }

    public static bool TryDecodeInput(byte[] data, int length,
                                      out int seq, out float x, out float y, out Buttons buttons)
    {
        seq = 0; x = 0f; y = 0f; buttons = Buttons.None;

        if (data == null || length < 8) return false;

        string[] p = Encoding.ASCII.GetString(data, 0, length).Split(' ');
        if (p.Length < 5 || p[0] != kInput) return false;

        int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seq);

        if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
        if (!float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;

        int flags;
        int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out flags);
        buttons = (Buttons)flags;

        return true;
    }

    public static byte[] EncodePong(string gameName)
    {
        if (string.IsNullOrEmpty(gameName)) gameName = "game";
        return Encoding.ASCII.GetBytes(kPong + " " + gameName);
    }

    public static bool TryDecodePong(byte[] data, int length, out string gameName)
    {
        gameName = null;

        if (data == null || length < 4) return false;

        string s = Encoding.ASCII.GetString(data, 0, length);
        if (!s.StartsWith(kPong, StringComparison.Ordinal)) return false;

        gameName = s.Length > kPong.Length + 1 ? s.Substring(kPong.Length + 1) : "game";
        return true;
    }

    // ------------------------------------------------------------------ addresses

    /// <summary>
    /// This device's address on the Wi-Fi, for the game to print so it can be typed into the
    /// controller by hand.
    ///
    /// Asked two ways because neither is reliable alone on Android. The interface list is the
    /// correct answer when it is available, but which interfaces are visible to a sandboxed app
    /// varies by manufacturer. So the fallback opens a UDP socket "towards" a public address and
    /// reads back which local address the routing table chose for it - nothing is sent, no
    /// internet connection is needed, and the answer is by definition the interface that carries
    /// LAN traffic.
    /// </summary>
    public static string LocalAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    string s = ua.Address.ToString();
                    if (s.StartsWith("127.", StringComparison.Ordinal)) continue;
                    if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                    return s;
                }
            }
        }
        catch { }

        try
        {
            using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                probe.Connect(new IPEndPoint(IPAddress.Parse("203.0.113.1"), 9));
                var ep = probe.LocalEndPoint as IPEndPoint;
                if (ep != null) return ep.Address.ToString();
            }
        }
        catch { }

        return "?";
    }

    /// <summary>
    /// Every address worth broadcasting to.
    ///
    /// 255.255.255.255 ought to be enough, and on a plain home router it is. But a phone hotspot
    /// or a router with client isolation will drop it while still passing the subnet's own
    /// broadcast (192.168.1.255 and the like), and some Android builds refuse the limited
    /// broadcast outright. Sending to both costs one extra datagram per frame and removes a
    /// whole category of "it just does not connect".
    /// </summary>
    public static List<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    byte[] ip = ua.Address.GetAddressBytes();
                    if (ip[0] == 127 || (ip[0] == 169 && ip[1] == 254)) continue;

                    byte[] mask;
                    try { mask = ua.IPv4Mask != null ? ua.IPv4Mask.GetAddressBytes() : null; }
                    catch { mask = null; }

                    // No mask reported is the common case on Android. /24 is the right guess for
                    // every consumer router and every phone hotspot.
                    if (mask == null || mask.Length != 4) mask = new byte[] { 255, 255, 255, 0 };

                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | (byte)~mask[i]);

                    var addr = new IPAddress(bc);
                    if (!list.Contains(addr)) list.Add(addr);
                }
            }
        }
        catch { }

        if (list.Count == 1)
        {
            // Nothing readable about the network. Cover the three private ranges people's
            // routers actually hand out.
            list.Add(IPAddress.Parse("192.168.0.255"));
            list.Add(IPAddress.Parse("192.168.1.255"));
            list.Add(IPAddress.Parse("192.168.43.255"));   // Android hotspot
            list.Add(IPAddress.Parse("10.0.0.255"));
        }

        return list;
    }
}
