using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StembridgeValley;

/// <summary>
/// Farm reset (owner only, from the launcher's My farm window), as Stembridge decided:
///  - the owner's farmer is deleted, and they make a brand-new one on the same farm (nothing kept);
///  - everyone else on the farm moves, cabin and all (farmer, items, money, skills), to a farm of their own;
///  - the farm itself and its quarry start over: fresh land, no buildings, no projects, the map picker again,
///    the name back to its tree name, open to visitors;
///  - at most once a week per owner, and only while nobody from the farm is online and nobody is on it.
///
/// Safety: before anything changes, the last saved world and the server's state files are copied to
/// state/reset-backups/, and farm-reset-pending.json points at that copy until the next nightly save. If the server
/// stops before that save, the world on disk is still the old one, so on start-up the state files are put back from
/// the copy too: the reset simply didn't happen (and doesn't count towards the weekly limit).
/// </summary>
internal static class FarmReset
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromDays(7);
    /// <summary>The state files a reset can change (and an unsaved one puts back).</summary>
    private static readonly string[] Touched =
    {
        "farm-members.json", "farm-maps.json", "farm-names.json", "farm-invites.json", "farm-joins.json",
        "farm-resets.json", "farms.json", "pets.json", "skill-levels.json",
    };

    private static string LastPath => Path.Combine(SV.StateDir, "farm-resets.json");
    private static string PendingPath => Path.Combine(SV.StateDir, "farm-reset-pending.json");

    private sealed class Pending
    {
        public string farm { get; set; } = "";
        public string backup { get; set; } = "";
        /// <summary>Marker written into the fresh farm's data: if the save on disk holds it, the reset was saved.</summary>
        public string tx { get; set; } = "";
        /// <summary>The world's main save file.</summary>
        public string save { get; set; } = "";
    }

    private const string TxKey = "SV.ResetTx";

    private static IModHelper Helper = null!;

    public static void Apply(IModHelper helper)
    {
        Helper = helper;
        if (SV.Role != Role.Server || !Farms.Enabled)
            return;
        // The nightly save now holds the reset: nothing left to redo.
        helper.Events.GameLoop.Saved += (_, _) =>
        {
            try { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
            catch (Exception ex) { Log.Warn($"Couldn't clear farm-reset-pending.json: {ex.Message}"); }
        };
    }

    /// <summary>
    /// Server start-up, before anything reads the state files: a reset the world never saved is undone, by putting the
    /// state files back as they were (the save on disk is still from before it).
    /// </summary>
    public static void UndoUnsaved()
    {
        try
        {
            if (string.IsNullOrEmpty(SV.StateDir) || !File.Exists(PendingPath))
                return;
            var p = JsonSerializer.Deserialize<Pending>(File.ReadAllText(PendingPath));
            // The save already holds this reset (the server stopped after saving, before tidying up): keep it.
            if (p != null && p.tx.Length > 0 && SaveHolds(p.save, p.tx))
            {
                File.Delete(PendingPath);
                Log.Info($"The reset of {p.farm} was saved before the server stopped; keeping it.");
                return;
            }
            string from = Path.Combine(p?.backup ?? "", "state");
            if (p == null || !Directory.Exists(from))
            {
                Log.Warn("farm-reset-pending.json points at a missing backup; leaving the state files alone.");
                return;
            }
            // Only the files a reset changes. (discord-players.json etc. belong to others and may have moved on.)
            foreach (string name in Touched)
            {
                string backup = Path.Combine(from, name), live = Path.Combine(SV.StateDir, name);
                if (File.Exists(backup))
                    File.Copy(backup, live, overwrite: true);
                else if (File.Exists(live))
                    File.Delete(live); // made by the reset itself
            }
            File.Delete(PendingPath);
            Log.Warn($"The server stopped before saving the reset of {p.farm}, so it was undone (state put back from {p.backup}).");
        }
        catch (Exception ex) { Log.Warn($"Couldn't undo an unsaved farm reset: {ex.Message}"); }
    }

    /// <summary>Whether the world's save file on disk contains this reset's marker.</summary>
    private static bool SaveHolds(string save, string tx)
    {
        if (string.IsNullOrEmpty(save) || !File.Exists(save))
            return false;
        using var reader = new StreamReader(save);
        var buffer = new char[1 << 20];
        string carry = "";
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            string chunk = carry + new string(buffer, 0, read);
            if (chunk.Contains(tx, StringComparison.Ordinal))
                return true;
            carry = chunk.Length > tx.Length ? chunk[^tx.Length..] : chunk;
        }
        return false;
    }

    /// <summary>When this owner may reset again (null = now).</summary>
    public static DateTimeOffset? NextAllowed(string ownerKey)
    {
        var last = ReadLast();
        if (!last.TryGetValue(Discord.AccountOf(ownerKey), out long at))
            return null;
        var next = DateTimeOffset.FromUnixTimeSeconds(at) + Cooldown;
        return next > DateTimeOffset.UtcNow ? next : null;
    }

    /// <summary>Why this farm can't be reset right now (null if it can), without changing anything.</summary>
    public static string? Problem(string ownerKey, string farm)
    {
        if (FarmRoster.Members(farm).FirstOrDefault() != ownerKey)
            return "Only the farm's owner can reset it.";
        if (NextAllowed(ownerKey) is { } next)
            return $"You can reset again {When(next)}. (Once a week.)";
        if (Game1.activeClickableMenu is SaveGameMenu || Game1.farmEvent != null || Game1.timeOfDay >= 2500)
            return "The server is ending the day. Try again in a minute.";
        var online = Game1.getOnlineFarmers().Where(f => !f.IsMainPlayer).ToList();
        if (online.FirstOrDefault(f => FarmRoster.Members(farm).Contains(f.userID.Value)) is { } here)
            return $"{here.Name} is playing right now. Everyone from your farm needs to be offline.";
        if (online.FirstOrDefault(f => Farms.FarmOf(f.currentLocation)?.Name == farm
                || f.currentLocation?.Name == Quarry.QuarryOf(farm)) is { } visitor)
            return $"{visitor.Name} is visiting your farm right now. Try again in a little while.";
        int moving = FarmRoster.Members(farm).Skip(1).Count();
        int room = Farms.AllNames.Count(f => f != farm && FarmRoster.Members(f).Count == 0) + (Farms.MaxFarms - Farms.Count);
        if (moving > room)
            return "There aren't enough free farms for your farmers to move to.";
        return null;
    }

    /// <summary>Server: reset the farm. Returns why not, or null when done.</summary>
    public static string? Run(string ownerKey, string farm)
    {
        if (Problem(ownerKey, farm) is { } why)
            return why;
        if (Game1.getLocationFromName(farm) is not Farm old || !Farms.IsFarm(old))
            return "That farm doesn't exist.";
        string shown = Farms.DisplayName(farm);

        if (Backup(farm, out string backup) is { } failed)
            return failed;
        string tx = "svreset-" + Guid.NewGuid().ToString("N");
        string saveFile = Constants.SaveFolderName is { Length: > 0 } folder
            ? Path.Combine(Constants.SavesPath, folder, folder) : "";
        WriteJson(PendingPath, new Pending { farm = farm, backup = backup, tx = tx, save = saveFile });

        // 1. Everyone else with a farmer moves out, cabin and all, onto a farm of their own. If anyone can't be
        //    moved, everyone already moved goes back and nothing has changed.
        var others = FarmRoster.Members(farm).Skip(1).ToList();
        var moved = new List<(string member, Building cabin, int x, int y, GameLocation dest)>();
        foreach (string member in others)
        {
            Building? cabin = CabinOf(old, member);
            if (cabin?.GetIndoors() is not Cabin c || !c.HasOwner || !c.owner.isCustomized.Value)
                continue;
            int x = cabin.tileX.Value, y = cabin.tileY.Value;
            string? to = Farms.AllNames.FirstOrDefault(f => f != farm && FarmRoster.Members(f).Count == 0) ?? Farms.OpenFarm();
            if (to == null || Game1.getLocationFromName(to) is not { } dest || !Server.MoveCabin(cabin, old, dest))
            {
                string who = c.owner.Name;
                for (int i = moved.Count - 1; i >= 0; i--)
                {
                    var m = moved[i];
                    m.dest.buildings.Remove(m.cabin);
                    m.cabin.tileX.Value = m.x;
                    m.cabin.tileY.Value = m.y;
                    old.buildings.Add(m.cabin);
                    m.cabin.updateInteriorWarps();
                    FarmRoster.MoveMember(m.member, m.dest.Name, farm);
                }
                FarmRoster.EnsureCabins();
                try { File.Delete(PendingPath); } catch { }
                Log.Warn($"Reset of {farm} stopped: no new home for {who}; {moved.Count} move(s) put back.");
                return $"Couldn't find a new home for {who}. Nothing was changed.";
            }
            FarmRoster.MoveMember(member, farm, to);
            moved.Add((member, cabin, x, y, dest));
        }
        // Members with no farmer yet are simply placed afresh the next time they join.
        foreach (string member in others.Where(m => FarmRoster.Members(farm).Contains(m)))
            FarmRoster.MoveMember(member, farm, null);

        // 2. The owner's farmer goes (they start again with a new one).
        if (CabinOf(old, ownerKey)?.GetIndoors() is Cabin mine)
            mine.DeleteFarmhand();
        foreach (Farmer f in Game1.netWorldState.Value.farmhandData.Values.Where(f => f.userID.Value == ownerKey).ToList())
            Game1.player.team.DeleteFarmhand(f);
        Pets.Forget(ownerKey);
        Skills.Forget(ownerKey);
        Collection.Forget(ownerKey);

        // 3. Fresh land and a fresh quarry, back to "pick your map".
        FarmMaps.Forget(farm);
        Helper.GameContent.InvalidateCache("Data/Locations");
        string seq = old.modData.TryGetValue("SV.SettingsSeq", out string? s) ? s : "";
        GameLocation fresh = Replace(old);
        fresh.modData[TxKey] = tx; // proof, in the save itself, that this reset was saved
        if (seq.Length > 0)
            fresh.modData["SV.SettingsSeq"] = seq; // so an old Discord rename isn't applied again
        fresh.AddDefaultBuildings();
        Server.BuildCabin(fresh, 0);
        fresh.modData["SV.CabinsInRow"] = "1";
        if (Game1.getLocationFromName(Quarry.QuarryOf(farm)) is { } quarry)
            Replace(quarry);
        fresh.updateWarps();
        FarmMaps.Label();
        FarmSettings.SaveKept();
        Control.ForgetInvites(farm);
        FarmRoster.EnsureCabins();

        var last = ReadLast();
        last[Discord.AccountOf(ownerKey)] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        WriteJson(LastPath, last);
        SendToEveryone(fresh, Game1.getLocationFromName(Quarry.QuarryOf(farm)));
        Log.Info($"{shown} was reset by its owner ({farm} is fresh land again).");
        return null;
    }

    /// <summary>Players online elsewhere swap their copy of the farm and its quarry for the fresh ones.</summary>
    private static void SendToEveryone(params GameLocation?[] locations)
    {
        var send = HarmonyLib.AccessTools.Method(typeof(StardewValley.Network.GameServer), "sendLocation");
        if (Game1.server is not StardewValley.Network.GameServer gs)
            return;
        foreach (long peer in Game1.otherFarmers.Keys.ToList())
            foreach (GameLocation? loc in locations)
                if (loc != null)
                    send.Invoke(gs, new object[] { peer, loc, false });
    }

    /// <summary>The cabin a player's farmer lives in on this farm (or the empty one held for them), if any.</summary>
    private static Building? CabinOf(GameLocation farm, string key) =>
        farm.buildings.FirstOrDefault(b => b.GetIndoors() is Cabin c && c.HasOwner && c.owner.userID.Value == key);

    /// <summary>Swap a location for a newly made one of the same name (nobody is in it).</summary>
    private static GameLocation Replace(GameLocation old)
    {
        int i = Game1.locations.IndexOf(old);
        foreach (Building b in old.buildings.ToList())
        {
            if (b.GetIndoors() is Cabin c)
                c.DeleteFarmhand();
            if (b.GetIndoors() is { } inside)
                Game1.removeLocationFromLocationLookup(inside);
        }
        Game1.removeLocationFromLocationLookup(old);
        old.OnRemoved();
        GameLocation fresh = Game1.CreateGameLocation(old.Name);
        Game1.locations[i] = fresh;
        Game1.netWorldState.Value.UpdateBuildingCache(fresh);
        return fresh;
    }

    /// <summary>Copy the last saved world and the server's state files, so a reset can be undone by hand.</summary>
    private static string? Backup(string farm, out string dir)
    {
        dir = "";
        try
        {
            dir = Path.Combine(SV.StateDir, "reset-backups", $"{DateTime.Now:yyyyMMdd-HHmmss}-{farm}");
            Directory.CreateDirectory(Path.Combine(dir, "state"));
            foreach (string f in Directory.GetFiles(SV.StateDir, "*.json"))
                File.Copy(f, Path.Combine(dir, "state", Path.GetFileName(f)));
            string save = Path.Combine(Constants.SavesPath, Constants.SaveFolderName ?? "");
            if (Constants.SaveFolderName is { Length: > 0 } && Directory.Exists(save))
                foreach (string f in Directory.GetFiles(save))
                    File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            Log.Info($"Backed up the world before resetting {farm}: {dir}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Reset backup failed: {ex.Message}");
            return "The server couldn't make a backup first, so nothing was changed. Tell Stembridge.";
        }
    }

    private static string When(DateTimeOffset at)
    {
        TimeSpan left = at - DateTimeOffset.UtcNow;
        return left.TotalHours < 24 ? $"in {Math.Max(1, (int)Math.Ceiling(left.TotalHours))} hour(s)" : $"in {(int)Math.Ceiling(left.TotalDays)} day(s)";
    }

    private static Dictionary<string, long> ReadLast()
    {
        try
        {
            if (File.Exists(LastPath))
                return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(LastPath)) ?? new();
        }
        catch (Exception ex) { Log.Warn($"Couldn't read farm-resets.json: {ex.Message}"); }
        return new();
    }

    private static void WriteJson<T>(string path, T value)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
