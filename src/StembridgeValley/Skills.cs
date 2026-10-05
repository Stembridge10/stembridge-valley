using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StembridgeValley;

/// <summary>
/// Skills past 10, OSRS style. Levels 1-10 are vanilla (same XP, same professions, same effects).
/// After that XP keeps counting toward level 50 on a rising curve (each level needs 10% more than the last,
/// so 50 takes about 2.2M XP; level 43 is roughly the halfway point). Levels are read from the XP Stardew already
/// saves for each player, so nothing new is stored on the player.
///  - Players: the Skills page shows the real level and XP to next; level-ups and unlocks pop up.
///  - Server: watches everyone's levels, announces milestones (every 5 levels from 15) to all players,
///    and writes hiscores.json for the Discord bot.
/// Note: XP lives in each player's own game, as in vanilla. Rewards that matter are given out by the server.
/// </summary>
internal static class Skills
{
    public const int MaxLevel = 50;
    private const int FirstStep = 5000;     // XP from 10 to 11
    private const double Growth = 1.10;     // each level after that needs 10% more

    // Stardew skill numbers.
    public const int Farming = 0, Fishing = 1, Foraging = 2, Mining = 3, Combat = 4;
    public static readonly int[] All = { Farming, Mining, Foraging, Fishing, Combat };
    public static string Name(int skill) => skill switch
    {
        Farming => "Farming", Fishing => "Fishing", Foraging => "Foraging", Mining => "Mining", Combat => "Combat", _ => "?",
    };

    /// <summary>Total XP needed to reach each level (index = level).</summary>
    private static readonly int[] Table = BuildTable();

    private static int[] BuildTable()
    {
        int[] t = new int[MaxLevel + 1];
        for (int l = 1; l <= 10; l++)
            t[l] = Farmer.getBaseExperienceForLevel(l);
        double step = FirstStep;
        for (int l = 11; l <= MaxLevel; l++)
        {
            t[l] = t[l - 1] + (int)Math.Round(step / 10) * 10;
            step *= Growth;
        }
        return t;
    }

    public static int XpForLevel(int level) => Table[Math.Clamp(level, 0, MaxLevel)];

    public static int LevelForXp(int xp)
    {
        for (int l = MaxLevel; l >= 1; l--)
            if (xp >= Table[l])
                return l;
        return 0;
    }

    public static int Xp(Farmer who, int skill) => skill >= 0 && skill < who.experiencePoints.Length ? who.experiencePoints[skill] : 0;
    public static int Level(Farmer who, int skill) => LevelForXp(Xp(who, skill));

    // ---------- unlocks ----------

    /// <summary>What a level gives, for the level-up popup and the Skills page. Only Mining has unlocks so far.</summary>
    public static readonly Dictionary<int, SortedDictionary<int, string>> Unlocks = new()
    {
        [Mining] = new SortedDictionary<int, string>
        {
            [15] = "Your farm's quarry opens (south of your farm, over the bridge)",
            [20] = "Quarry: more rocks each day, and more gold",
            [25] = "Quarry: gem rocks",
            [30] = "Quarry: even more rocks each day",
            [35] = "Quarry: iridium",
            [40] = "Quarry: mystic stones",
            [45] = "Quarry: twice the rocks",
            [50] = "Mining mastered",
        },
    };

    public static string? UnlockAt(int skill, int level) =>
        Unlocks.TryGetValue(skill, out var u) && u.TryGetValue(level, out string? s) ? s : null;

    public static (int Level, string Text)? NextUnlock(int skill, int level) =>
        Unlocks.TryGetValue(skill, out var u) && u.FirstOrDefault(kv => kv.Key > level) is { Value: not null } next ? (next.Key, next.Value) : null;

    // ---------- setup ----------

    private static IModHelper Helper = null!;
    private static string ModId = "";
    private const string MsgAnnounce = "Announce";

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        Helper = helper;
        ModId = helper.ModRegistry.ModID;
        helper.Events.Multiplayer.ModMessageReceived += OnMessage;
        if (SV.Role == Role.Server)
        {
            helper.Events.GameLoop.OneSecondUpdateTicked += ServerScan;
            return;
        }
        harmony.Patch(AccessTools.Method(typeof(Farmer), nameof(Farmer.gainExperience)),
            prefix: new HarmonyMethod(typeof(Skills), nameof(GainXp_Prefix)),
            postfix: new HarmonyMethod(typeof(Skills), nameof(GainXp_Postfix)));
        harmony.Patch(AccessTools.Method(typeof(SkillsPage), nameof(SkillsPage.draw), new[] { typeof(Microsoft.Xna.Framework.Graphics.SpriteBatch) }),
            prefix: new HarmonyMethod(typeof(Skills), nameof(SkillsDraw_Prefix)),
            finalizer: new HarmonyMethod(typeof(Skills), nameof(SkillsDraw_Finalizer)));
        harmony.Patch(AccessTools.Method(typeof(NumberSprite), nameof(NumberSprite.draw)),
            prefix: new HarmonyMethod(typeof(Skills), nameof(NumberDraw_Prefix)));
        harmony.Patch(AccessTools.Constructor(typeof(SkillsPage), new[] { typeof(int), typeof(int), typeof(int), typeof(int) }),
            postfix: new HarmonyMethod(typeof(Skills), nameof(SkillsPage_Postfix)));
    }

    // ---------- player: level-ups ----------

    private static void GainXp_Prefix(Farmer __instance, int which, out int __state) =>
        __state = __instance.IsLocalPlayer ? Level(__instance, which) : -1;

    private static void GainXp_Postfix(Farmer __instance, int which, int __state)
    {
        if (__state < 0 || !__instance.IsLocalPlayer)
            return;
        int now = Level(__instance, which);
        if (now <= __state || now <= 10)
            return; // levels up to 10 get the vanilla level-up screen
        for (int l = Math.Max(__state + 1, 11); l <= now; l++)
        {
            Game1.addHUDMessage(new HUDMessage($"{Name(which)} level {l}!", HUDMessage.achievement_type));
            if (UnlockAt(which, l) is { } unlock)
                Game1.addHUDMessage(new HUDMessage($"Unlocked: {unlock}", HUDMessage.newQuest_type));
        }
        Game1.playSound("achievement");
    }

    // ---------- player: Skills page ----------

    private static SkillsPage? drawingPage;
    private static void SkillsDraw_Prefix(SkillsPage __instance) => drawingPage = __instance;
    private static Exception? SkillsDraw_Finalizer(Exception? __exception)
    {
        drawingPage = null;
        return __exception;
    }

    /// <summary>The Skills page draws a capped "10" after each skill's pips: show the real level instead.</summary>
    private static void NumberDraw_Prefix(ref int number, Vector2 position)
    {
        if (drawingPage == null || number != 10)
            return;
        int top = drawingPage.yPositionOnScreen + IClickableMenu.spaceToClearTopBorder + IClickableMenu.borderWidth - 8;
        int row = (int)Math.Round((position.Y - top - 14) / 68f);
        int skill = row switch { 0 => Farming, 1 => Mining, 2 => Foraging, 3 => Fishing, 4 => Combat, _ => -1 };
        if (skill >= 0 && Math.Abs(position.Y - (top + 14 + row * 68)) <= 4)
            number = Level(Game1.player, skill);
    }

    /// <summary>Hovering a skill shows its real level, XP to the next level and the next unlock.</summary>
    private static void SkillsPage_Postfix(SkillsPage __instance)
    {
        foreach (var area in __instance.skillAreas)
        {
            if (!int.TryParse(area.name, out int skill) || Level(Game1.player, skill) < 1)
                continue;
            area.hoverText = (area.hoverText?.Length > 0 ? area.hoverText + Environment.NewLine + Environment.NewLine : "") + Describe(Game1.player, skill);
        }
    }

    public static string Describe(Farmer who, int skill)
    {
        int level = Level(who, skill), xp = Xp(who, skill);
        string text = $"{Name(skill)} level {level} of {MaxLevel} ({xp:N0} XP)";
        if (level < MaxLevel)
            text += Environment.NewLine + $"{XpForLevel(level + 1) - xp:N0} XP to level {level + 1}";
        if (NextUnlock(skill, level) is { } next)
            text += Environment.NewLine + $"Level {next.Level}: {next.Text}";
        return text;
    }

    // ---------- server: milestones and hiscores ----------

    private static string SeenPath => Path.Combine(SV.StateDir, "skill-levels.json");
    private static string HiscoresPath => Path.Combine(SV.StateDir, "hiscores.json");
    private static Dictionary<string, Dictionary<string, int>>? seen;
    private static string lastHiscores = "";

    /// <summary>Raised on the server when a player's level goes up (key, skill, new level). Used by the farm quarry.</summary>
    public static event Action<Farmer, int, int>? LevelUp;

    private static void ServerScan(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || !e.IsMultipleOf(300))
            return; // every 5 seconds
        try
        {
            Scan();
        }
        catch (Exception ex)
        {
            Log.Warn($"Skill scan failed: {ex.Message}");
        }
    }

    public static IEnumerable<Farmer> Players() =>
        Game1.getAllFarmers().Where(f => !f.IsMainPlayer && f.isCustomized.Value && !string.IsNullOrEmpty(f.userID.Value));

    /// <summary>Farm reset: forget the old farmer's levels, so the new one's milestones are announced.</summary>
    public static void Forget(string key)
    {
        if (seen == null || !seen.Remove(key))
            return;
        try
        {
            File.WriteAllText(SeenPath + ".tmp", JsonSerializer.Serialize(seen));
            File.Move(SeenPath + ".tmp", SeenPath, true);
        }
        catch (Exception ex) { Log.Warn($"Couldn't write skill-levels.json: {ex.Message}"); }
    }

    private static void Scan()
    {
        bool first = seen == null;
        if (seen == null)
        {
            try
            {
                seen = File.Exists(SeenPath) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(SeenPath)) : null;
            }
            catch
            {
                seen = null;
            }
            seen ??= new();
        }
        bool changed = false;
        var board = new List<object>();
        foreach (Farmer f in Players())
        {
            string key = f.userID.Value;
            bool known = seen.TryGetValue(key, out var mine);
            mine ??= new Dictionary<string, int>();
            var skills = new Dictionary<string, object>();
            int total = 0;
            long totalXp = 0;
            foreach (int s in All)
            {
                int level = Level(f, s);
                total += level;
                totalXp += Xp(f, s);
                skills[Name(s)] = new { level, xp = Xp(f, s) };
                int before = mine.TryGetValue(Name(s), out int b) ? b : level;
                if (level != before || !mine.ContainsKey(Name(s)))
                {
                    mine[Name(s)] = level;
                    changed = true;
                }
                if (!first && known && level > before)
                {
                    LevelUp?.Invoke(f, s, level);
                    for (int l = before + 1; l <= level; l++)
                        if (l >= 15 && (l % 5 == 0 || l == MaxLevel))
                            Announce(l == MaxLevel
                                ? $"{f.Name} has mastered {Name(s)}: level {MaxLevel}!"
                                : $"{f.Name} reached {Name(s)} level {l}!");
                }
            }
            seen[key] = mine;
            string? home = Farms.HomeFarmOf(f);
            board.Add(new { key, name = f.Name, farm = home != null ? Farms.DisplayName(home) : "", total, totalXp, skills });
        }
        if (changed || first)
        {
            File.WriteAllText(SeenPath + ".tmp", JsonSerializer.Serialize(seen));
            File.Move(SeenPath + ".tmp", SeenPath, overwrite: true);
        }
        string json = JsonSerializer.Serialize(board, new JsonSerializerOptions { WriteIndented = true });
        if (json != lastHiscores)
        {
            File.WriteAllText(HiscoresPath + ".tmp", json);
            File.Move(HiscoresPath + ".tmp", HiscoresPath, overwrite: true);
            lastHiscores = json;
        }
    }

    /// <summary>Server: a line in every player's chat.</summary>
    public static void Announce(string text)
    {
        Log.Info($"Announce: {text}");
        Helper.Multiplayer.SendMessage(text, MsgAnnounce, new[] { ModId });
    }

    private static void OnMessage(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID == ModId && e.Type == MsgAnnounce && SV.Role != Role.Server && Game1.chatBox != null)
            Game1.chatBox.addInfoMessage(e.ReadAs<string>());
    }
}
