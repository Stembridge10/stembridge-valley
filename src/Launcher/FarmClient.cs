using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace StembridgeValley.Launcher;

/// <summary>
/// Talks to the server's farm panel (Control.cs on the server) over the game's own port, with no game running.
/// One small request packet out, one reply back. Every request carries the player's invite code; the server
/// works out who they are from it, so nothing here can act as anyone else.
/// Packets use the game network library's "unconnected message" framing: type 0, two sequence bytes,
/// a 16-bit length in bits, then the JSON.
/// </summary>
internal sealed class FarmClient
{
    private readonly string host;
    private readonly int port;
    private readonly string code;
    private long nextId = Random.Shared.NextInt64(1, long.MaxValue / 2);

    public FarmClient(string address, string code)
    {
        int colon = address.LastIndexOf(':');
        host = colon > 0 ? address[..colon] : address;
        port = colon > 0 && int.TryParse(address[(colon + 1)..], out int p) ? p : 24642;
        this.code = code;
    }

    /// <summary>Only personal Discord codes (d-...) can use the farm panel.</summary>
    public static bool CanUse(string code) => code.StartsWith("d-", StringComparison.Ordinal);

    /// <summary>Send a request; returns the reply, or a reply with ok=false and a friendly error.</summary>
    public async Task<JsonObject> Ask(string op, JsonObject? extra = null)
    {
        long id = Interlocked.Increment(ref nextId);
        var q = new JsonObject { ["v"] = 1, ["id"] = id, ["code"] = code, ["op"] = op };
        if (extra != null)
            foreach (var (k, v) in extra)
                q[k] = v?.DeepClone();
        byte[] body = Encoding.UTF8.GetBytes(q.ToJsonString());
        byte[] packet = new byte[5 + body.Length];
        int bits = body.Length * 8;
        packet[0] = 0;                    // unconnected message
        packet[3] = (byte)bits;
        packet[4] = (byte)(bits >> 8);
        Buffer.BlockCopy(body, 0, packet, 5, body.Length);

        try
        {
            IPAddress[] addrs = await Dns.GetHostAddressesAsync(host);
            IPAddress ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0];
            using var udp = new UdpClient(ip.AddressFamily);
            var to = new IPEndPoint(ip, port);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await udp.SendAsync(packet, to);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
                try
                {
                    while (true)
                    {
                        UdpReceiveResult r = await udp.ReceiveAsync(cts.Token);
                        if (Parse(r.Buffer) is { } reply && reply["id"]?.GetValue<long>() == id)
                            return reply;
                    }
                }
                catch (OperationCanceledException) { } // no answer yet: send again
            }
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return Error("Couldn't reach the server: " + ex.Message);
        }
        return Error("The server didn't answer. It may be restarting; try again in a minute.");
    }

    private static JsonObject? Parse(byte[] data)
    {
        if (data.Length < 5 || data[0] != 0)
            return null;
        int len = (data[3] | (data[4] << 8)) / 8; // length is sent in bits
        if (len <= 0 || 5 + len > data.Length)
            return null;
        try { return JsonNode.Parse(Encoding.UTF8.GetString(data, 5, len)) as JsonObject; }
        catch { return null; }
    }

    private static JsonObject Error(string why) => new() { ["ok"] = false, ["error"] = why };
}
