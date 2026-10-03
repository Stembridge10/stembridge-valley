using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StembridgeValley;

/// <summary>Server-owned rare skilling pets, with a small visual follower selected in the collection log.</summary>
internal static class Pets
{
    public const string ActiveKey = "SV.ActivePet";
    private const string ClaimMessage = "PetClaim";
    private const string ListMessage = "PetList";
    private static IModHelper Helper = null!;
    private static string ModId = "";
    private static readonly Dictionary<string, HashSet<string>> ServerPets = new();
    private static readonly Dictionary<long, Vector2> Positions = new();
    private static readonly Dictionary<int, DateTime> LastClaim = new();
    private static readonly Dictionary<string, HashSet<string>> ClientPets = new();
    private static readonly Dictionary<int, string> PetForSkill = new()
    {
        [Skills.Mining] = "rock_golem", [Skills.Fishing] = "heron", [Skills.Foraging] = "squirrel", [Skills.Farming] = "chick",
    };

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        Helper = helper;
        ModId = helper.ModRegistry.ModID;
        helper.Events.Multiplayer.ModMessageReceived += OnMessage;
        if (SV.Role == Role.Server)
        {
            Load();
            helper.Events.GameLoop.OneSecondUpdateTicked += ServerSync;
            helper.ConsoleCommands.Add("sv_pets", "Print skilling pet state.", (_, _) => Log.Info(JsonSerializer.Serialize(ServerPets)));
            return;
        }
        harmony.Patch(AccessTools.Method(typeof(Farmer), nameof(Farmer.gainExperience)),
            postfix: new HarmonyMethod(typeof(Pets), nameof(GainExperience_Postfix)));
        helper.Events.Display.RenderedWorld += Draw;
    }

    public static IEnumerable<string> Owned(Farmer who)
    {
        string key = who.userID.Value;
        return SV.Role == Role.Server ? ServerPets.GetValueOrDefault(key) ?? Enumerable.Empty<string>() : ClientPets.GetValueOrDefault(key) ?? Enumerable.Empty<string>();
    }

    public static string NameOf(string id) => id switch
    {
        "rock_golem" => "Rock Golem", "heron" => "Heron", "squirrel" => "Squirrel", "chick" => "Chick", _ => id,
    };

    private static void GainExperience_Postfix(Farmer __instance, int which, int howMuch)
    {
        if (SV.Role != Role.Client || !__instance.IsLocalPlayer || howMuch <= 0 || !PetForSkill.TryGetValue(which, out string? pet) || Skills.Level(__instance, which) < 1)
            return;
        int denominator = 5000 - 60 * Math.Min(50, Skills.Level(__instance, which));
        if (int.TryParse(Environment.GetEnvironmentVariable("SV_TEST_PET_ODDS"), out int test) && test > 0)
            denominator = test;
        denominator = Math.Max(100, denominator);
        if (Game1.random.Next(denominator) == 0)
            Helper.Multiplayer.SendMessage(new Claim(which, pet), ClaimMessage, new[] { ModId });
    }

    private sealed record Claim(int Skill, string Pet);

    private static void OnMessage(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != ModId)
            return;
        if (SV.Role == Role.Server && e.Type == ClaimMessage)
        {
            try
            {
                Claim claim = e.ReadAs<Claim>();
                Farmer? who = Skills.Players().FirstOrDefault(f => f.UniqueMultiplayerID == e.FromPlayerID);
                if (who == null || !PetForSkill.TryGetValue(claim.Skill, out string? expected) || expected != claim.Pet || Skills.Level(who, claim.Skill) < 1)
                    return;
                if (LastClaim.TryGetValue(claim.Skill.GetHashCode() ^ who.UniqueMultiplayerID.GetHashCode(), out DateTime last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(3))
                    return;
                LastClaim[claim.Skill.GetHashCode() ^ who.UniqueMultiplayerID.GetHashCode()] = DateTime.UtcNow;
                string key = who.userID.Value;
                HashSet<string> mine = ServerPets.GetValueOrDefault(key) ?? new();
                if (!mine.Add(claim.Pet))
                    return;
                ServerPets[key] = mine;
                Save();
                Sync(who);
                Skills.Announce($"{who.Name} received a pet: {NameOf(claim.Pet)} while {Skills.Name(claim.Skill)}!");
            }
            catch (Exception ex) { Log.Warn($"Pet claim failed: {ex.Message}"); }
        }
        else if (SV.Role == Role.Client && e.Type == ListMessage)
        {
            try
            {
                Dictionary<string, string[]> all = e.ReadAs<Dictionary<string, string[]>>();
                ClientPets.Clear();
                foreach ((string key, string[] pets) in all)
                    ClientPets[key] = pets.ToHashSet();
            }
            catch (Exception ex) { Log.Warn($"Pet list failed: {ex.Message}"); }
        }
    }

    private static void ServerSync(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (Context.IsWorldReady && e.IsMultipleOf(60))
            foreach (Farmer who in Skills.Players())
                Sync(who);
    }

    private static void Sync(Farmer who)
    {
        var all = ServerPets.ToDictionary(x => x.Key, x => x.Value.ToArray());
        Helper.Multiplayer.SendMessage(all, ListMessage, new[] { ModId }, new[] { who.UniqueMultiplayerID });
    }

    private static string PathName => Path.Combine(SV.StateDir, "pets.json");
    private static void Load()
    {
        try
        {
            if (File.Exists(PathName))
            {
                Dictionary<string, string[]>? saved = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(PathName));
                if (saved != null)
                    foreach ((string key, string[] pets) in saved)
                        ServerPets[key] = pets.ToHashSet();
            }
        }
        catch (Exception ex) { Log.Warn($"Could not load pets: {ex.Message}"); }
    }
    private static void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(SV.StateDir)) return;
            Directory.CreateDirectory(SV.StateDir);
            string json = JsonSerializer.Serialize(ServerPets.ToDictionary(x => x.Key, x => x.Value.ToArray()), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PathName + ".tmp", json);
            File.Move(PathName + ".tmp", PathName, true);
        }
        catch (Exception ex) { Log.Warn($"Could not save pets: {ex.Message}"); }
    }

    private static void Draw(object? sender, RenderedWorldEventArgs e)
    {
        try
        {
            foreach (Farmer who in Game1.currentLocation.farmers)
            {
                string active = who.modData.GetValueOrDefault(ActiveKey, "");
                if (active.Length == 0 || !Owned(who).Contains(active)) continue;
                Vector2 target = who.Position + new Vector2(who.FacingDirection == 1 ? -48 : 48, 12);
                if (!Positions.TryGetValue(who.UniqueMultiplayerID, out Vector2 pos)) pos = target;
                pos = Vector2.Lerp(pos, target, 0.12f);
                Positions[who.UniqueMultiplayerID] = pos;
                Rectangle source = active switch { "rock_golem" => new Rectangle(372, 1945, 16, 16), "heron" => new Rectangle(288, 1430, 16, 16), "squirrel" => new Rectangle(280, 1430, 16, 16), _ => new Rectangle(128, 128, 16, 16) };
                e.SpriteBatch.Draw(Game1.mouseCursors, Game1.GlobalToLocal(Game1.viewport, pos), source, Color.White, 0f, Vector2.Zero, 2f, SpriteEffects.None, who.getDrawLayer() + 0.0002f);
            }
        }
        catch (Exception ex) { Log.Warn($"Pet drawing failed: {ex.Message}"); }
    }
}
