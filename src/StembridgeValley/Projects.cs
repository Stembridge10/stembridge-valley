using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace StembridgeValley;

/// <summary>
/// Farm projects: the farm's long progression, done the Community Center way. Each project is a few bundles; each
/// bundle is a few slots ("10 Wood", "5 of any vegetable"). The 4 players of a farm fill them together by donating
/// items from the Farm Projects book (P). Six named projects, then endless Farm Orders that grow with each one done.
/// Nothing vanilla is locked behind them: every reward is an extra on top of the normal game.
///  - The server is the authority: it checks every donation (a member of that farm, a slot that still needs it) and
///    keeps the progress in the farm's modData, which every player's game sees.
///  - A donating player's game takes the items out first and gets back whatever the server didn't take.
///  - Rewards go to every member: each player's own game hands them out once, the next time they're in the world.
/// </summary>
internal static class Projects
{
    public const string ProgressKey = "SV.Projects";
    private const string ClaimedKey = "SV.ProjectsClaimed";
    private const string MsgDonate = "ProjectDonate", MsgReply = "ProjectReply";
    private const SButton Hotkey = SButton.P;

    /// <summary>A slot: a specific item ("(O)388") or any item of a category ("cat:-75"), and how many.</summary>
    public sealed record Slot(string Want, int Count, string Label);
    public sealed record Bundle(string Name, Slot[] Slots);
    public sealed record Reward(string Item, int Count);
    public sealed record Project(string Name, string Blurb, Bundle[] Bundles, Reward[] Rewards, int Gold);

    private static Slot Item(string id, int count) => new("(O)" + id, count, "");
    public static string LabelOf(Slot s) => s.Label.Length > 0 ? s.Label : ItemName(s.Want);
    private static Slot Cat(int category, int count, string label) => new("cat:" + category, count, label);

    private static readonly Project[] Named =
    {
        new("Break Ground", "Clear the land and stock up.", new[]
        {
            new Bundle("Timber", new[] { Item("388", 100), Item("709", 10) }),          // wood, hardwood
            new Bundle("Rock", new[] { Item("390", 100), Item("378", 20) }),            // stone, copper ore
            new Bundle("Field", new[] { Item("771", 50), Item("92", 25) }),             // fiber, sap
        }, new[] { new Reward("(O)599", 5), new Reward("(O)368", 20) }, 500),            // sprinklers, basic fertilizer
        new("First Fields", "Grow a bit of everything.", new[]
        {
            new Bundle("Vegetables", new[] { Cat(-75, 15, "any vegetable") }),
            new Bundle("Fruit", new[] { Cat(-79, 10, "any fruit") }),
            new Bundle("Forage", new[] { Cat(-81, 15, "any forage") }),
        }, new[] { new Reward("(O)621", 4), new Reward("(O)465", 10) }, 1000),           // quality sprinklers, speed-gro
        new("Livestock", "Start the barnyard.", new[]
        {
            new Bundle("Coop", new[] { Cat(-5, 10, "any egg") }),
            new Bundle("Barn", new[] { Cat(-6, 5, "any milk") }),
            new Bundle("Feed", new[] { Item("178", 50) }),                                // hay
        }, new[] { new Reward("(BC)24", 1), new Reward("(BC)16", 1), new Reward("(O)178", 100) }, 1500), // mayo machine, cheese press, hay
        new("Pantry", "Fill the cellar.", new[]
        {
            new Bundle("Artisan", new[] { Cat(-26, 10, "any artisan good") }),
            new Bundle("Kitchen", new[] { Cat(-7, 5, "any cooked dish") }),
            new Bundle("Catch", new[] { Cat(-4, 10, "any fish") }),
        }, new[] { new Reward("(BC)12", 2), new Reward("(BC)15", 2) }, 2500),           // kegs, preserves jars
        new("Workshop", "Smelt and build.", new[]
        {
            new Bundle("Bars", new[] { Item("334", 20), Item("335", 20), Item("336", 10) }), // copper, iron, gold bars
            new Bundle("Glass", new[] { Item("338", 10), Item("382", 50) }),             // refined quartz, coal
        }, new[] { new Reward("(O)645", 2), new Reward("(BC)21", 1) }, 5000),           // iridium sprinklers, crystalarium
        new("Wizard's Favor", "Rare things for a rare friend.", new[]
        {
            new Bundle("Essence", new[] { Item("768", 30), Item("769", 30) }),           // solar, void essence
            new Bundle("Treasure", new[] { Item("337", 5), Item("74", 1) }),             // iridium bar, prismatic shard
        }, new[] { new Reward("(O)645", 4), new Reward("(O)908", 50) }, 10000),          // iridium sprinklers, magic bait
    };

    /// <summary>Endless Farm Orders after the named projects: four bundles that grow every time.</summary>
    private static readonly (int Category, string Label)[] OrderPool =
    {
        (-75, "any vegetable"), (-79, "any fruit"), (-81, "any forage"), (-4, "any fish"), (-5, "any egg"),
        (-6, "any milk"), (-26, "any artisan good"), (-7, "any cooked dish"), (-2, "any gem"), (-15, "any metal bar or ore"),
    };

    public static int NamedCount => Named.Length;

    public static Project Get(int index)
    {
        if (index < Named.Length)
            return Named[index];
        int n = index - Named.Length + 1; // Farm Order #n
        var rng = new Random(9137 * n);
        var picks = OrderPool.OrderBy(_ => rng.Next()).Take(4).ToArray();
        int count = 10 + 5 * n;
        var bundles = picks.Select(p => new Bundle(p.Label[4..].Trim() is var l && l.Length > 0 ? char.ToUpper(l[0]) + l[1..] : "Order",
            new[] { Cat(p.Category, count, p.Label) })).ToArray();
        return new Project($"Farm Order #{n}", "A standing order from town. There's always another.", bundles,
            new[] { new Reward("(O)MysteryBox", 1 + n / 3) }, 2000 + 1000 * n);
    }

    // ---------- progress (in the farm's modData; written only by the server) ----------

    /// <summary>Progress of one farm: the project it's on and how much each slot has (key "bundle.slot").</summary>
    public sealed class Progress
    {
        public int Current { get; set; }
        public Dictionary<string, int> Filled { get; set; } = new();
        public List<string> Log { get; set; } = new();
    }

    public static Progress Read(GameLocation? farm)
    {
        if (farm != null && farm.modData.TryGetValue(ProgressKey, out string? json) && json.Length > 0)
        {
            try { return JsonSerializer.Deserialize<Progress>(json) ?? new(); }
            catch { }
        }
        return new();
    }

    private static void Write(GameLocation farm, Progress p) => farm.modData[ProgressKey] = JsonSerializer.Serialize(p);

    public static int Have(Progress p, int bundle, int slot) => p.Filled.GetValueOrDefault($"{bundle}.{slot}");

    public static bool BundleDone(Progress p, Project project, int b) =>
        project.Bundles[b].Slots.Select((s, i) => Have(p, b, i) >= s.Count).All(x => x);

    public static bool Matches(Slot slot, Item item) =>
        item is StardewValley.Object o && !o.bigCraftable.Value && !o.questItem.Value && item.TypeDefinitionId == "(O)" &&
        (slot.Want.StartsWith("cat:") ? int.TryParse(slot.Want[4..], out int c) && item.Category == c : item.QualifiedItemId == slot.Want);

    // ---------- setup ----------

    private static IModHelper Helper = null!;
    private static string ModId = "";

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        if (!Farms.Enabled)
            return;
        Helper = helper;
        ModId = helper.ModRegistry.ModID;
        helper.Events.Multiplayer.ModMessageReceived += OnMessage;
        if (SV.Role == Role.Server)
        {
            helper.Events.GameLoop.OneSecondUpdateTicked += (_, e) => { if (Context.IsWorldReady && e.IsMultipleOf(600)) WriteBoard(); };
            helper.ConsoleCommands.Add("sv_projects", "Print every farm's project progress.", (_, _) => Log.Info(BoardJson()));
        }
        else if (SV.Role == Role.Client)
        {
            helper.Events.Input.ButtonPressed += (_, e) =>
            {
                if (Context.IsWorldReady && e.Button == Hotkey && Game1.activeClickableMenu == null && Farms.HomeFarmOf(Game1.player) is string home)
                    Game1.activeClickableMenu = new ProjectMenu(home);
            };
            helper.Events.GameLoop.OneSecondUpdateTicked += (_, e) => { if (Context.IsWorldReady && e.IsMultipleOf(120)) ClaimRewards(); };
        }
    }

    // ---------- donating ----------

    private record Donate(string Farm, int Project, int Bundle, int Slot, string ItemId, int Count);
    private record Reply(string ItemId, int Taken, int Offered, string Text);

    /// <summary>Player: take up to what the slot still needs of matching items out of the inventory and offer them.</summary>
    public static string? Offer(string farm, int bundle, int slot)
    {
        Progress p = Read(Game1.getLocationFromName(farm));
        Project project = Get(p.Current);
        Slot s = project.Bundles[bundle].Slots[slot];
        int need = s.Count - Have(p, bundle, slot);
        if (need <= 0)
            return "That one's full.";
        Item? item = Game1.player.Items.Where(i => i != null && Matches(s, i)).OrderByDescending(i => i.Stack).FirstOrDefault();
        if (item == null)
            return $"You have no {LabelOf(s)}.";
        int count = Math.Min(need, item.Stack);
        string id = item.QualifiedItemId;
        int quality = item.Quality;
        item.Stack -= count;
        if (item.Stack <= 0)
            Game1.player.removeItemFromInventory(item);
        pendingQuality[id] = quality;
        Helper.Multiplayer.SendMessage(new Donate(farm, p.Current, bundle, slot, id, count), MsgDonate, new[] { ModId }, new[] { Game1.MasterPlayer.UniqueMultiplayerID });
        Game1.playSound("Ship");
        return null;
    }

    private static readonly Dictionary<string, int> pendingQuality = new();
    public static string LastReply = "";

    private static void OnMessage(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != ModId)
            return;
        if (SV.Role == Role.Server && e.Type == MsgDonate && e.ReadAs<Donate>() is { } d)
        {
            Farmer? who = Game1.getOnlineFarmers().FirstOrDefault(f => f.UniqueMultiplayerID == e.FromPlayerID);
            int taken = 0;
            string text;
            try { text = Take(d, who, out taken); }
            catch (Exception ex) { text = "Something went wrong; your items are back."; Log.Warn($"Projects: {ex}"); }
            Helper.Multiplayer.SendMessage(new Reply(d.ItemId, taken, d.Count, text), MsgReply, new[] { ModId }, new[] { e.FromPlayerID });
        }
        else if (SV.Role == Role.Client && e.Type == MsgReply && e.ReadAs<Reply>() is { } r && e.FromPlayerID == Game1.MasterPlayer?.UniqueMultiplayerID)
        {
            int back = Math.Max(0, r.Offered - r.Taken);
            if (back > 0)
            {
                Item refund = ItemRegistry.Create(r.ItemId, back, pendingQuality.GetValueOrDefault(r.ItemId));
                Game1.player.addItemByMenuIfNecessary(refund);
            }
            LastReply = r.Text;
            Log.Info($"[projects] {r.Text} (took {r.Taken}/{r.Offered} {r.ItemId})");
            Game1.addHUDMessage(new HUDMessage(r.Text, r.Taken > 0 ? HUDMessage.newQuest_type : HUDMessage.error_type));
        }
    }

    /// <summary>Server: check a donation and record it. Returns the text for the player; <paramref name="taken"/> is how many count.</summary>
    private static string Take(Donate d, Farmer? who, out int taken)
    {
        taken = 0;
        if (who == null || Farms.HomeFarmOf(who) != d.Farm || Game1.getLocationFromName(d.Farm) is not { } farm)
            return "Only this farm's players can work on its projects.";
        Progress p = Read(farm);
        if (d.Project != p.Current)
            return "That project is already done.";
        Project project = Get(p.Current);
        if (d.Bundle < 0 || d.Bundle >= project.Bundles.Length || d.Slot < 0 || d.Slot >= project.Bundles[d.Bundle].Slots.Length || d.Count <= 0)
            return "That isn't part of this project.";
        Slot s = project.Bundles[d.Bundle].Slots[d.Slot];
        Item probe = ItemRegistry.Create(d.ItemId, 1);
        if (!Matches(s, probe))
            return $"That isn't {LabelOf(s)}.";
        int need = s.Count - Have(p, d.Bundle, d.Slot);
        taken = Math.Clamp(d.Count, 0, need);
        if (taken == 0)
            return "That one's already full.";
        p.Filled[$"{d.Bundle}.{d.Slot}"] = Have(p, d.Bundle, d.Slot) + taken;
        Log.Info($"Projects: {who.Name} gave {taken} {probe.DisplayName} to {Farms.DisplayName(d.Farm)}'s {project.Name} ({project.Bundles[d.Bundle].Name}).");
        string text = $"Gave {taken} {probe.DisplayName}.";
        if (Enumerable.Range(0, project.Bundles.Length).All(b => BundleDone(p, project, b)))
        {
            p.Log.Add($"{project.Name}|{Game1.season} {Game1.dayOfMonth}, Year {Game1.year}");
            p.Current++;
            p.Filled.Clear();
            text = $"{project.Name} is done! Rewards are on their way to everyone on the farm.";
            Skills.Announce($"{Farms.DisplayName(d.Farm)} finished the farm project {project.Name}!");
        }
        else if (BundleDone(p, project, d.Bundle))
            text = $"The {project.Bundles[d.Bundle].Name} bundle is complete!";
        Write(farm, p);
        return text;
    }

    // ---------- rewards ----------

    /// <summary>Player: hand out rewards for every finished project of the home farm this player hasn't had yet.</summary>
    private static void ClaimRewards()
    {
        if (!Context.IsPlayerFree || Farms.HomeFarmOf(Game1.player) is not string home)
            return;
        Progress p = Read(Game1.getLocationFromName(home));
        int claimed = int.TryParse(Game1.player.modData.GetValueOrDefault(ClaimedKey + "." + home), out int c) ? c : 0;
        if (claimed >= p.Current)
            return;
        Project project = Get(claimed);
        Game1.player.modData[ClaimedKey + "." + home] = (claimed + 1).ToString();
        var items = project.Rewards.Select(r => ItemRegistry.Create(r.Item, r.Count)).ToList();
        string text = $"Farm project reward ({project.Name}): {project.Gold}g, {string.Join(", ", items.Select(i => $"{i.Stack} {i.DisplayName}"))}";
        Game1.player.Money += project.Gold;
        Game1.player.addItemsByMenuIfNecessary(items);
        Log.Info($"[projects] {text}");
        Game1.addHUDMessage(new HUDMessage(text, HUDMessage.achievement_type));
        Game1.playSound("reward");
    }

    // ---------- board for Discord/hiscores ----------

    private static string lastBoard = "";

    private static string BoardJson() => JsonSerializer.Serialize(Farms.AllNames.Select(f =>
    {
        Progress p = Read(Game1.getLocationFromName(f));
        return new { farm = f, name = Farms.DisplayName(f), done = p.Current, current = Get(p.Current).Name, log = p.Log };
    }), new JsonSerializerOptions { WriteIndented = true });

    private static void WriteBoard()
    {
        if (string.IsNullOrEmpty(SV.StateDir))
            return;
        string json = BoardJson();
        if (json == lastBoard)
            return;
        string path = Path.Combine(SV.StateDir, "farm-projects.json");
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
        lastBoard = json;
    }

    private static string ItemName(string id)
    {
        try { return ItemRegistry.GetDataOrErrorItem(id).DisplayName; }
        catch { return id; }
    }

    // ---------- the book ----------

    internal sealed class ProjectMenu : IClickableMenu
    {
        private readonly string farm;
        private readonly List<(Rectangle Box, int Bundle, int Slot)> slots = new();
        private string status = "";

        public ProjectMenu(string farm) : base(Game1.uiViewport.Width / 2 - 500, Game1.uiViewport.Height / 2 - 320, 1000, 640, true)
        {
            this.farm = farm;
            LastReply = "";
        }

        public override void receiveKeyPress(Microsoft.Xna.Framework.Input.Keys key)
        {
            if (key is Microsoft.Xna.Framework.Input.Keys.Escape or Microsoft.Xna.Framework.Input.Keys.P)
                exitThisMenu();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);
            foreach (var (box, b, s) in slots)
                if (box.Contains(x, y))
                {
                    status = Offer(farm, b, s) ?? "";
                    if (status.Length > 0)
                        Game1.playSound("cancel");
                    return;
                }
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
            Progress p = Read(Game1.getLocationFromName(farm));
            Project project = Get(p.Current);
            // Size the book to what this project lists, centred, so nothing overlaps on small screens.
            height = 150 + project.Bundles.Sum(bu => 46 + bu.Slots.Length * 36) + 120;
            yPositionOnScreen = Math.Max(0, Game1.uiViewport.Height / 2 - height / 2);
            xPositionOnScreen = Game1.uiViewport.Width / 2 - width / 2;
            upperRightCloseButton.bounds.X = xPositionOnScreen + width - 36;
            upperRightCloseButton.bounds.Y = yPositionOnScreen - 8;
            drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
            int x = xPositionOnScreen + 40, y = yPositionOnScreen + 36;
            SpriteText.drawString(b, $"{Farms.DisplayName(farm)}: {project.Name}", x, y);
            y += 56;
            b.DrawString(Game1.smallFont, $"Project {p.Current + 1}. {project.Blurb}", new Vector2(x, y), Game1.textColor);
            y += 32;
            b.DrawString(Game1.smallFont, "Click a row to give what you're carrying.", new Vector2(x, y), Color.Gray);
            y += 40;
            slots.Clear();
            for (int bi = 0; bi < project.Bundles.Length; bi++)
            {
                Bundle bundle = project.Bundles[bi];
                bool done = BundleDone(p, project, bi);
                b.DrawString(Game1.dialogueFont, bundle.Name + (done ? "  (complete)" : ""), new Vector2(x, y), done ? Color.DarkGreen : Game1.textColor);
                y += 42;
                for (int si = 0; si < bundle.Slots.Length; si++)
                {
                    Slot s = bundle.Slots[si];
                    int have = Have(p, bi, si);
                    int carried = Game1.player.Items.Where(i => i != null && Matches(s, i)).Sum(i => i.Stack);
                    var box = new Rectangle(x + 10, y, width - 100, 34);
                    bool hover = box.Contains(Game1.getMouseX(), Game1.getMouseY()) && have < s.Count;
                    if (hover)
                        b.Draw(Game1.staminaRect, box, Color.Wheat * 0.6f);
                    if (!s.Want.StartsWith("cat:"))
                    {
                        var data = ItemRegistry.GetDataOrErrorItem(s.Want);
                        b.Draw(data.GetTexture(), new Rectangle(box.X + 4, box.Y + 2, 32, 32), data.GetSourceRect(), Color.White);
                    }
                    string line = $"{LabelOf(s)}: {Math.Min(have, s.Count)} / {s.Count}" + (have < s.Count && carried > 0 ? $"   (you carry {carried})" : "");
                    b.DrawString(Game1.smallFont, line, new Vector2(box.X + 44, box.Y + 4), have >= s.Count ? Color.DarkGreen : carried > 0 ? Game1.textColor : Color.Gray);
                    slots.Add((box, bi, si));
                    y += 36;
                }
                y += 4;
            }
            string reward = $"Reward for everyone on the farm: {project.Gold}g, " + string.Join(", ", project.Rewards.Select(r => $"{r.Count} {ItemName(r.Item)}"));
            y += 10;
            string wrapped = Game1.parseText(reward, Game1.smallFont, width - 80);
            b.DrawString(Game1.smallFont, wrapped, new Vector2(x, y), Color.DarkBlue);
            y += (int)Game1.smallFont.MeasureString(wrapped).Y + 6;
            string footer = status.Length > 0 ? status : LastReply.Length > 0 ? LastReply : $"Projects finished: {p.Current}" + (p.Log.Count > 0 ? $" (last: {p.Log[^1].Split('|')[0]})" : "");
            b.DrawString(Game1.smallFont, footer, new Vector2(x, y), Game1.textColor);
            base.draw(b);
            drawMouse(b);
        }
    }
}
