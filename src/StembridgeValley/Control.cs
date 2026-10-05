using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using Lidgren.Network;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StembridgeValley;

/// <summary>
/// The launcher's farm panel talks to the server through this, on the same port as the game (no extra port).
///
/// The launcher sends one small "unconnected" packet (a plain request, not a game connection) holding JSON:
///   {"v":1, "id":N, "code":"d-DISCORDID-TOKEN", "op":"info"|"rename"|"visitors"|"invite"|"join", ...}
/// and gets one JSON packet back with the same id. Every request carries the player's own invite code, and the
/// server works out who they are from it exactly as when they join the game, so a code only ever acts as its owner.
///   info                    -> who you are, your farm, its members, its current invite code (members only)
///   rename  {"name"}        -> owner renames the farm
///   visitors{"open":bool}   -> owner opens or closes it to visitors
///   invite                  -> any member makes a 6-letter code for a friend (one per farm, 7 days)
///   join    {"invite"}      -> a player who hasn't made a farmer yet joins that friend's farm
/// Requests are read off the network thread into a queue and handled on the game thread; each address gets at
/// most 30 a minute.
/// </summary>
internal static class Control
{
    private const int MaxPacket = 1100;
    private static readonly TimeSpan InviteLife = TimeSpan.FromDays(7);
    private const string InviteLetters = "ACDEFGHJKLMNPQRTUVWXY34679"; // nothing that looks like another letter

    private static NetServer? server;
    private static readonly ConcurrentQueue<(IPEndPoint from, byte[] body)> Inbox = new();
    private static readonly Dictionary<IPAddress, (DateTime start, int count)> Rate = new();

    private static string InvitesPath => Path.Combine(SV.StateDir, "farm-invites.json");
    private static string JoinsPath => Path.Combine(SV.StateDir, "farm-joins.json");

    private sealed class Invite
    {
        public string farm { get; set; } = "";
        public string by { get; set; } = "";
        public long expires { get; set; }
    }

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (SV.Role != Role.Server || !Farms.Enabled)
            return;
        harmony.Patch(AccessTools.Method(typeof(NetPeer), nameof(NetPeer.Start)),
            prefix: new HarmonyMethod(typeof(Control), nameof(Start_Prefix)));
        harmony.Patch(AccessTools.Method(typeof(NetPeer), "ReleaseMessage"),
            prefix: new HarmonyMethod(typeof(Control), nameof(Release_Prefix)));
        helper.Events.GameLoop.UpdateTicked += OnTick;
    }

    private static void Start_Prefix(NetPeer __instance)
    {
        if (__instance is not NetServer s)
            return;
        server = s;
        s.Configuration.EnableMessageType(NetIncomingMessageType.UnconnectedData);
        Log.Info("Launcher farm panel: listening on the game port.");
    }

    /// <summary>Network thread: take launcher requests before the game's own message loop sees them.</summary>
    private static bool Release_Prefix(NetPeer __instance, NetIncomingMessage msg)
    {
        if (__instance != server || msg.MessageType != NetIncomingMessageType.UnconnectedData)
            return true;
        if (msg.SenderEndPoint != null && msg.LengthBytes is > 0 and <= MaxPacket && Inbox.Count < 200)
            Inbox.Enqueue((msg.SenderEndPoint, msg.Data.AsSpan(0, msg.LengthBytes).ToArray()));
        return false; // never reaches the game
    }

    private static void OnTick(object? sender, UpdateTickedEventArgs e)
    {
        for (int i = 0; i < 20 && Inbox.TryDequeue(out var req); i++)
        {
            if (!Allowed(req.from.Address))
                continue;
            JsonObject reply;
            long id = 0;
            try
            {
                var q = JsonNode.Parse(Encoding.UTF8.GetString(req.body))!.AsObject();
                id = q["id"]?.GetValue<long>() ?? 0;
                reply = Context.IsWorldReady ? Handle(q) : Fail("The server is still starting. Try again in a minute.");
            }
            catch (Exception ex)
            {
                Log.Debug($"Launcher request from {req.from} not understood: {ex.Message}");
                reply = Fail("The server didn't understand that. Update the launcher and try again.");
            }
            reply["id"] = id;
            Send(req.from, reply);
        }
    }

    private static bool Allowed(IPAddress ip)
    {
        DateTime now = DateTime.UtcNow;
        if (!Rate.TryGetValue(ip, out var r) || now - r.start > TimeSpan.FromMinutes(1))
            r = (now, 0);
        Rate[ip] = (r.start, r.count + 1);
        if (Rate.Count > 2000)
            Rate.Clear();
        return r.count < 30;
    }

    private static void Send(IPEndPoint to, JsonObject reply)
    {
        if (server == null)
            return;
        byte[] body = Encoding.UTF8.GetBytes(reply.ToJsonString());
        if (body.Length > MaxPacket)
            body = Encoding.UTF8.GetBytes(Fail("Too much to send.").ToJsonString());
        NetOutgoingMessage o = server.CreateMessage(body.Length);
        o.Write(body);
        server.SendUnconnectedMessage(o, to);
    }

    private static JsonObject Fail(string why) => new() { ["ok"] = false, ["error"] = why };
    private static JsonObject Ok(string? message = null) => message == null ? new() { ["ok"] = true } : new() { ["ok"] = true, ["message"] = message };

    // ---------- requests ----------

    private static JsonObject Handle(JsonObject q)
    {
        string code = q["code"]?.GetValue<string>() ?? "";
        if (!Discord.TryReadCode(code, out string key, out string discordName, out _))
            return Fail("The server didn't accept your code. Type /play in Discord for your current one.");
        string op = q["op"]?.GetValue<string>() ?? "info";
        string? farm = FarmRoster.FarmOfKey(key);
        bool started = HasCharacter(key);

        switch (op)
        {
            case "info":
                return Info(key, discordName, farm, started);

            case "rename":
            {
                if (farm == null || !started)
                    return Fail("Make your farmer first, then you can name your farm.");
                string? why = FarmSettings.SetName(farm, key, q["name"]?.GetValue<string>(), firstTimeOnly: false);
                return why != null ? Fail(why) : Ok($"Your farm is now {Farms.DisplayName(farm)}.");
            }

            case "visitors":
            {
                if (farm == null || !started)
                    return Fail("Make your farmer first.");
                bool open = q["open"]?.GetValue<bool>() ?? true;
                string? why = FarmSettings.SetClosed(farm, key, !open);
                return why != null ? Fail(why) : Ok(open ? "Visitors can look around your farm now." : "Your farm is closed to visitors.");
            }

            case "invite":
            {
                if (farm == null || !started)
                    return Fail("Make your farmer first, then you can invite friends.");
                if (FarmRoster.Members(farm).Count >= Farms.PlayersPerFarm)
                    return Fail($"{Farms.DisplayName(farm)} is full ({Farms.PlayersPerFarm}/{Farms.PlayersPerFarm}).");
                var all = ReadInvites();
                foreach (string old in all.Where(kv => kv.Value.farm == farm).Select(kv => kv.Key).ToList())
                    all.Remove(old); // one code per farm; a new one replaces it
                string fresh;
                do fresh = new string(Enumerable.Range(0, 6).Select(_ => InviteLetters[RandomNumberGenerator.GetInt32(InviteLetters.Length)]).ToArray());
                while (all.ContainsKey(fresh));
                all[fresh] = new Invite { farm = farm, by = key, expires = DateTimeOffset.UtcNow.Add(InviteLife).ToUnixTimeSeconds() };
                WriteJson(InvitesPath, all);
                Log.Info($"New launcher invite for {Farms.DisplayName(farm)}.");
                var r = Ok();
                r["invite"] = fresh;
                r["expires"] = all[fresh].expires;
                return r;
            }

            case "join":
            {
                string inv = (q["invite"]?.GetValue<string>() ?? "").Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");
                var all = ReadInvites();
                if (!all.TryGetValue(inv, out Invite? found) || found.expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    return Fail("That invite code doesn't work (it may have run out). Ask your friend for a new one.");
                if (started)
                    return Fail("You already have a farmer, so you can't move to another farm.");
                if (farm == found.farm)
                    return Ok($"You're already set to join {Farms.DisplayName(found.farm)}.");
                if (FarmRoster.Members(found.farm).Count >= Farms.PlayersPerFarm)
                    return Fail($"{Farms.DisplayName(found.farm)} is full.");
                var joins = ReadJoins();
                joins[key] = found.farm;
                WriteJson(JoinsPath, joins);
                Log.Info($"Player {key[..Math.Min(14, key.Length)]} will join {Farms.DisplayName(found.farm)} (launcher invite).");
                return Ok($"You'll start on {Farms.DisplayName(found.farm)}. Press Play and make your farmer.");
            }
        }
        return Fail("The launcher asked for something this server doesn't know. Update the launcher.");
    }

    private static JsonObject Info(string key, string discordName, string? farm, bool started)
    {
        var r = Ok();
        r["name"] = CharacterName(key) ?? discordName;
        r["started"] = started;
        if (ReadJoins().TryGetValue(key, out string? joining) && !started)
            r["joining"] = Farms.DisplayName(joining);
        if (farm == null)
            return r;
        var list = new JsonArray();
        var members = FarmRoster.Members(farm);
        foreach (string m in members)
            list.Add(new JsonObject
            {
                ["name"] = CharacterName(m) ?? Discord.NameOf(m) ?? "someone",
                ["started"] = HasCharacter(m),
                ["you"] = m == key,
            });
        var f = new JsonObject
        {
            ["name"] = Farms.DisplayName(farm),
            ["named"] = FarmSettings.ChosenName(farm) != null,
            ["owner"] = members.FirstOrDefault() == key,
            ["open"] = !FarmSettings.IsClosed(farm),
            ["size"] = Farms.PlayersPerFarm,
            ["members"] = list,
        };
        var inv = ReadInvites().FirstOrDefault(kv => kv.Value.farm == farm && kv.Value.expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (inv.Key != null && started)
        {
            f["invite"] = inv.Key;
            f["inviteExpires"] = inv.Value.expires;
        }
        r["farm"] = f;
        return r;
    }

    // ---------- shared with the join path ----------

    /// <summary>Farm a player picked with a launcher invite (used when they first join, before they make a farmer).</summary>
    public static string? JoinFarmFor(string key)
    {
        if (!Farms.Enabled || string.IsNullOrEmpty(SV.StateDir))
            return null;
        return ReadJoins().TryGetValue(key, out string? farm) && Farms.AllNames.Contains(farm) ? farm : null;
    }

    private static bool HasCharacter(string key) =>
        Game1.netWorldState.Value.farmhandData.Values.Concat(Game1.getAllFarmers())
            .Any(f => f != null && !f.IsMainPlayer && f.isCustomized.Value && f.userID.Value == key);

    private static string? CharacterName(string key) =>
        Game1.netWorldState.Value.farmhandData.Values.Concat(Game1.getAllFarmers())
            .FirstOrDefault(f => f != null && !f.IsMainPlayer && f.isCustomized.Value && f.userID.Value == key)?.Name;

    private static Dictionary<string, Invite> ReadInvites()
    {
        try
        {
            if (File.Exists(InvitesPath))
            {
                var all = JsonSerializer.Deserialize<Dictionary<string, Invite>>(File.ReadAllText(InvitesPath)) ?? new();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return all.Where(kv => kv.Value.expires > now).ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }
        catch (Exception ex) { Log.Warn($"Couldn't read farm-invites.json: {ex.Message}"); }
        return new();
    }

    private static Dictionary<string, string> ReadJoins()
    {
        try
        {
            if (File.Exists(JoinsPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(JoinsPath)) ?? new();
        }
        catch (Exception ex) { Log.Warn($"Couldn't read farm-joins.json: {ex.Message}"); }
        return new();
    }

    private static void WriteJson<T>(string path, T value)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
