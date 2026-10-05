using System.Text.Json;
using System.Text.RegularExpressions;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StembridgeValley;

/// <summary>
/// Farm names and "open to visitors", chosen by each farm's owner (the player who founded it).
///  - In game: the owner names their farm once at the bus stop notice board, and can close or open it to visitors there.
///  - Discord: /farmname renames it later, /visitors opens or closes it (the bot writes farm-settings.json).
/// The server decides: it checks the request came from the owner, cleans up the name, and stores it in the
/// farm's modData (saved with the world, and synced to every player):
///   SV.FarmName  chosen name ("Sunny" shows as "Sunny Farm")      SV.Closed  "1" when closed to visitors
///   SV.Owner     owner's player key                               SV.OwnerName  owner's farmer name
/// Visitors are walked back out of a farm that closes while they're on it.
/// </summary>
internal static class FarmSettings
{
    public const string NameKey = "SV.FarmName", ClosedKey = "SV.Closed", OwnerKey = "SV.Owner", OwnerNameKey = "SV.OwnerName";
    private const string MsgName = "FarmName", MsgVisits = "FarmVisits", MsgLeave = "LeaveFarm";
    public const int MaxNameLength = 20;
    private static IModHelper Helper = null!;
    private static string ModId = "";

    private static string SettingsPath => Path.Combine(SV.StateDir, "farm-settings.json");
    /// <summary>Server's own copy of names/visits, written right away so a restart before the nightly save keeps them.</summary>
    private static string KeptPath => Path.Combine(SV.StateDir, "farm-names.json");
    private static DateTime settingsStamp = DateTime.MinValue;

    private class Kept
    {
        public string? name { get; set; }
        public bool closed { get; set; }
        public long seq { get; set; }
    }

    /// <summary>Server: put saved names/visits back on the farms (after loading the world).</summary>
    private static void LoadKept()
    {
        try
        {
            if (!File.Exists(KeptPath))
                return;
            var all = JsonSerializer.Deserialize<Dictionary<string, Kept>>(File.ReadAllText(KeptPath)) ?? new();
            foreach (var (farm, k) in all)
            {
                if (Find(farm) is not { } loc)
                    continue;
                SetIfChanged(loc, NameKey, Clean(k.name) ?? "");
                SetIfChanged(loc, ClosedKey, k.closed ? "1" : "");
                if (k.seq > 0)
                    loc.modData["SV.SettingsSeq"] = k.seq.ToString();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read farm-names.json: {ex.Message}");
        }
    }

    private static void SaveKept()
    {
        try
        {
            var all = Farms.AllNames.Where(f => Find(f) != null).ToDictionary(f => f, f => new Kept
            {
                name = ChosenName(f),
                closed = IsClosed(f),
                seq = Find(f)!.modData.TryGetValue("SV.SettingsSeq", out string? d) && long.TryParse(d, out long v) ? v : 0,
            });
            string tmp = KeptPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, KeptPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't write farm-names.json: {ex.Message}");
        }
    }

    public static void Apply(IModHelper helper)
    {
        if (!Farms.Enabled)
            return;
        Helper = helper;
        ModId = helper.ModRegistry.ModID;
        helper.Events.Multiplayer.ModMessageReceived += OnMessage;
        if (SV.Role == Role.Server)
            helper.Events.GameLoop.OneSecondUpdateTicked += OnServerSecond;
    }

    // ---------- reading (both sides) ----------

    private static GameLocation? Find(string farm) => Game1.locations?.FirstOrDefault(l => l?.Name == farm);

    /// <summary>The owner's chosen name, or null if they haven't picked one.</summary>
    public static string? ChosenName(string farm) =>
        Find(farm) is { } loc && loc.modData.TryGetValue(NameKey, out string? n) && n.Length > 0 ? n : null;

    public static bool IsClosed(string farm) => Find(farm) is { } loc && loc.modData.TryGetValue(ClosedKey, out string? c) && c == "1";
    public static string? OwnerOf(string farm) => Find(farm) is { } loc && loc.modData.TryGetValue(OwnerKey, out string? o) && o.Length > 0 ? o : null;
    public static string? OwnerName(string farm) => Find(farm) is { } loc && loc.modData.TryGetValue(OwnerNameKey, out string? o) && o.Length > 0 ? o : null;

    public static bool IsOwner(Farmer who, string farm) => !string.IsNullOrEmpty(who.userID.Value) && OwnerOf(farm) == who.userID.Value;

    // ---------- names ----------

    /// <summary>Tidy a requested name: letters, numbers, spaces, ' and -; a trailing "Farm" is dropped (it's added back on display).
    /// Anything else in it means it's refused, rather than quietly turned into something the owner didn't type.</summary>
    public static string? Clean(string? raw)
    {
        if (raw == null)
            return null;
        string s = Regex.Replace(raw, @"\s+", " ").Trim();
        if (Regex.IsMatch(s, @"[^\p{L}\p{N} '\-]"))
            return null;
        s = Regex.Replace(s, @"\s*farm$", "", RegexOptions.IgnoreCase).Trim();
        if (s.Length < 2 || s.Length > MaxNameLength || !s.Any(char.IsLetter))
            return null;
        return s;
    }

    /// <summary>Why a name can't be used on this farm, or null if it's fine.</summary>
    public static string? Problem(string farm, string? clean)
    {
        if (clean == null)
            return $"Names are 2-{MaxNameLength} letters, numbers, spaces, ' or -.";
        string shown = clean + " Farm";
        foreach (string other in Farms.AllNames)
            if (other != farm && string.Equals(Farms.DisplayName(other), shown, StringComparison.OrdinalIgnoreCase))
                return $"{shown} is already taken.";
        return null;
    }

    // ---------- server ----------

    private static int ownerTick;

    private static void OnServerSecond(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        if (ownerTick == 0)
            LoadKept();
        if (e.IsMultipleOf(5) || ownerTick++ == 0)
            RefreshOwners();
        ReadDiscordSettings();
        if (e.IsMultipleOf(3))
            SendVisitorsOut();
    }

    /// <summary>Keep each farm's owner (first member) and their farmer name on the farm, for players' menus.</summary>
    private static void RefreshOwners()
    {
        var names = Game1.netWorldState.Value.farmhandData.Values.Concat(Game1.getAllFarmers())
            .Where(f => f.isCustomized.Value && !string.IsNullOrEmpty(f.userID.Value))
            .GroupBy(f => f.userID.Value).ToDictionary(g => g.Key, g => g.First().Name);
        foreach (string farm in Farms.AllNames)
        {
            if (Find(farm) is not { } loc)
                continue;
            string owner = FarmRoster.Members(farm).FirstOrDefault() ?? "";
            string ownerName = names.TryGetValue(owner, out string? n) ? n : "";
            SetIfChanged(loc, OwnerKey, owner);
            SetIfChanged(loc, OwnerNameKey, ownerName);
        }
    }

    private static void SetIfChanged(GameLocation loc, string key, string value)
    {
        if (value.Length == 0)
        {
            if (loc.modData.ContainsKey(key))
                loc.modData.Remove(key);
        }
        else if (!loc.modData.TryGetValue(key, out string? old) || old != value)
            loc.modData[key] = value;
    }

    /// <summary>Server: apply a name from the owner. Returns why not, or null when done.</summary>
    internal static string? SetName(string farm, string playerKey, string? raw, bool firstTimeOnly)
    {
        if (Find(farm) is not { } loc || FarmRoster.Members(farm).FirstOrDefault() != playerKey)
            return "Only the farm's owner can name it.";
        if (firstTimeOnly && ChosenName(farm) != null)
            return "Your farm already has a name. Rename it with /farmname in Discord.";
        string? clean = Clean(raw);
        if (Problem(farm, clean) is { } why)
            return why;
        string before = Farms.DisplayName(farm);
        loc.modData[NameKey] = clean!;
        Log.Info($"{before} is now {Farms.DisplayName(farm)} (named by its owner).");
        SaveKept();
        FarmRoster.Refresh();
        return null;
    }

    internal static string? SetClosed(string farm, string playerKey, bool closed)
    {
        if (Find(farm) is not { } loc || FarmRoster.Members(farm).FirstOrDefault() != playerKey)
            return "Only the farm's owner can do that.";
        if (closed)
            loc.modData[ClosedKey] = "1";
        else
            loc.modData.Remove(ClosedKey);
        Log.Info($"{Farms.DisplayName(farm)} is now {(closed ? "closed to" : "open to")} visitors.");
        SaveKept();
        FarmRoster.Refresh();
        if (closed)
            SendVisitorsOut();
        return null;
    }

    private class DiscordSetting
    {
        public string? by { get; set; }
        public string? name { get; set; }
        public string? visits { get; set; }
        public long seq { get; set; }
    }

    /// <summary>farm-settings.json from the Discord bot: { "SV_Farm1": { "by": "discord-ID", "name": "...", "visits": "open"|"closed", "seq": N } }.</summary>
    private static void ReadDiscordSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            DateTime stamp = File.GetLastWriteTimeUtc(SettingsPath);
            if (stamp == settingsStamp)
                return;
            settingsStamp = stamp;
            var all = JsonSerializer.Deserialize<Dictionary<string, DiscordSetting>>(File.ReadAllText(SettingsPath)) ?? new();
            foreach (var (farm, s) in all)
            {
                if (Find(farm) is not { } loc || s.by == null)
                    continue;
                long done = loc.modData.TryGetValue("SV.SettingsSeq", out string? d) && long.TryParse(d, out long v) ? v : 0;
                if (s.seq <= done)
                    continue;
                loc.modData["SV.SettingsSeq"] = s.seq.ToString();
                string? why = null;
                if (s.name != null && Clean(s.name) != ChosenName(farm))
                    why = SetName(farm, s.by, s.name, firstTimeOnly: false);
                if (s.visits is "open" or "closed" && (s.visits == "closed") != IsClosed(farm))
                    why ??= SetClosed(farm, s.by, s.visits == "closed");
                SaveKept(); // remember this request was handled, even if it was refused
                if (why != null)
                    Log.Info($"Discord farm setting for {farm} not applied: {why}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read farm-settings.json: {ex.Message}");
        }
    }

    /// <summary>Anyone on a closed farm (or in a building on it) who doesn't live there is sent back to the bus stop.</summary>
    private static void SendVisitorsOut()
    {
        foreach (Farmer f in Game1.getOnlineFarmers())
        {
            if (f.IsMainPlayer || Farms.FarmOf(f.currentLocation) is not { } farm || !Farms.IsFarm(farm) || !IsClosed(farm.Name))
                continue;
            if (Farms.HomeFarmOf(f) == farm.Name)
                continue;
            Helper.Multiplayer.SendMessage(farm.Name, MsgLeave, new[] { ModId }, new[] { f.UniqueMultiplayerID });
        }
    }

    // ---------- messages ----------

    private record NameRequest(string Farm, string Name);
    private record VisitsRequest(string Farm, bool Closed);
    private record Reply(string Text, bool Ok);

    private static void OnMessage(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != ModId)
            return;
        if (SV.Role == Role.Server)
        {
            Farmer? who = Game1.getOnlineFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == e.FromPlayerID);
            if (who == null || string.IsNullOrEmpty(who.userID.Value))
                return;
            string? why = null;
            string done = "";
            if (e.Type == MsgName && e.ReadAs<NameRequest>() is { } n)
            {
                why = SetName(n.Farm, who.userID.Value, n.Name, firstTimeOnly: true);
                done = $"Your farm is now {Farms.DisplayName(n.Farm)}!";
            }
            else if (e.Type == MsgVisits && e.ReadAs<VisitsRequest>() is { } v)
            {
                why = SetClosed(v.Farm, who.userID.Value, v.Closed);
                done = v.Closed ? "Your farm is closed to visitors." : "Your farm is open to visitors.";
            }
            else
                return;
            Helper.Multiplayer.SendMessage(new Reply(why ?? done, why == null), "Reply", new[] { ModId }, new[] { e.FromPlayerID });
            return;
        }
        if (e.Type == "Reply" && e.ReadAs<Reply>() is { } r)
            Game1.addHUDMessage(new HUDMessage(r.Text, r.Ok ? HUDMessage.achievement_type : HUDMessage.error_type));
        else if (e.Type == MsgLeave && Farms.FarmOf(Game1.currentLocation) is { } here && here.Name == e.ReadAs<string>())
        {
            Game1.addHUDMessage(new HUDMessage("This farm just closed to visitors.", HUDMessage.error_type));
            Farms.LeaveToBusStop();
        }
    }

    // ---------- player menus (at the notice board) ----------

    public static void AskForName(string farm)
    {
        Game1.activeClickableMenu = new NamingMenu(name =>
        {
            Game1.exitActiveMenu();
            string? clean = Clean(name);
            if (Problem(farm, clean) is { } why)
            {
                Game1.addHUDMessage(new HUDMessage(why, HUDMessage.error_type));
                return;
            }
            SendToServer(MsgName, new NameRequest(farm, clean!));
        }, "Name your farm", OwnerName(farm) ?? "");
    }

    public static void SetVisits(string farm, bool closed) => SendToServer(MsgVisits, new VisitsRequest(farm, closed));

    /// <summary>Test hook: send a name request exactly as the naming box would (skips the client-side check, so the server's check is tested).</summary>
    public static void RequestNameForTest(string farm, string name) => SendToServer(MsgName, new NameRequest(farm, name));

    private static void SendToServer<T>(string type, T message) where T : notnull =>
        Helper.Multiplayer.SendMessage(message, type, new[] { ModId }, new[] { Game1.MasterPlayer.UniqueMultiplayerID });
}
