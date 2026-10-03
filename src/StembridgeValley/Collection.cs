using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Objects;
using StardewValley.Menus;

namespace StembridgeValley;

/// <summary>A per-player record of Stardew's fish, mineral and artifact counters.</summary>
internal static class Collection
{
    private const SButton Hotkey = SButton.L;
    private const string Message = "CollectionNew";
    private static IModHelper Helper = null!;
    private static string ModId = "";
    private static readonly Dictionary<string, HashSet<string>> Previous = new();
    private static Dictionary<string, HashSet<string>>? completed;
    private static string lastJson = "";

    private sealed record Entry(string Id, string Name);
    private sealed class Category
    {
        public string Name = "";
        public List<Entry> Items = new();
    }

    public static void Apply(IModHelper helper, Harmony harmony)
    {
        Helper = helper;
        ModId = helper.ModRegistry.ModID;
        helper.Events.Input.ButtonPressed += ButtonPressed;
        helper.Events.GameLoop.OneSecondUpdateTicked += Tick;
        helper.Events.Multiplayer.ModMessageReceived += OnMessage;
        if (SV.Role == Role.Server)
            helper.ConsoleCommands.Add("sv_log", "Print collection log state.", (_, _) => Log.Info(BuildBoard().RootElement.GetRawText()));
    }

    private static void ButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (SV.Role == Role.Client && Context.IsWorldReady && e.Button == Hotkey && Game1.activeClickableMenu == null)
            Game1.activeClickableMenu = new LogMenu();
    }

    private static void Tick(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        try
        {
            if (SV.Role == Role.Server && e.IsMultipleOf(600))
                WriteBoard(); // ten seconds
            else if (SV.Role == Role.Client && e.IsMultipleOf(60))
                FindNew(Game1.player);
        }
        catch (Exception ex)
        {
            Log.Warn($"Collection update failed: {ex.Message}");
        }
    }

    private static List<Category> Categories()
    {
        var result = new List<Category> { new() { Name = "Fish" }, new() { Name = "Minerals & Gems" }, new() { Name = "Artifacts" }, new() { Name = "Rare drops" } };
        foreach ((string id, ObjectData data) in Game1.objectData)
        {
            if (data.Category == -4)
                result[0].Items.Add(new Entry(id, data.Name));
            else if (data.Category is -2 or -12)
                result[1].Items.Add(new Entry(id, data.Name));
            else if (data.Type == "Arch")
                result[2].Items.Add(new Entry(id, data.Name));
        }
        // These are deliberately explicit: they are meaningful rare finds, not things merely shipped.
        foreach (string id in new[] { "74", "347", "454", "499", "114", "166" })
            if (Game1.objectData.TryGetValue(id, out ObjectData? data))
                result[3].Items.Add(new Entry(id, data.Name));
        foreach (Category category in result)
            category.Items = category.Items.DistinctBy(x => x.Id).OrderBy(x => x.Name).ToList();
        return result;
    }

    private static bool Found(Farmer who, Entry entry, int category)
    {
        return category switch
        {
            0 => who.fishCaught.ContainsKey(entry.Id),
            1 => who.mineralsFound.ContainsKey(entry.Id),
            2 => who.archaeologyFound.ContainsKey(entry.Id),
            _ => who.mineralsFound.ContainsKey(entry.Id) || who.archaeologyFound.ContainsKey(entry.Id) || who.basicShipped.ContainsKey(entry.Id),
        };
    }

    private static Dictionary<string, int> Totals(Farmer who, out int total, out int maximum)
    {
        var categories = Categories();
        var totals = new Dictionary<string, int>();
        total = maximum = 0;
        for (int i = 0; i < categories.Count; i++)
        {
            int got = categories[i].Items.Count(x => Found(who, x, i));
            totals[categories[i].Name] = got;
            total += got;
            maximum += categories[i].Items.Count;
        }
        return totals;
    }

    private static void FindNew(Farmer who)
    {
        string key = who.userID.Value;
        var now = new HashSet<string>();
        var categories = Categories();
        for (int i = 0; i < categories.Count; i++)
            foreach (Entry item in categories[i].Items.Where(x => Found(who, x, i)))
                now.Add(i + ":" + item.Id);
        if (!Previous.TryGetValue(key, out HashSet<string>? before))
        {
            Previous[key] = now;
            return;
        }
        foreach (string added in now.Except(before))
        {
            string[] parts = added.Split(':');
            Entry? item = categories[int.Parse(parts[0])].Items.FirstOrDefault(x => x.Id == parts[1]);
            if (item != null)
            {
                string text = "New collection log entry: " + item.Name;
                Game1.addHUDMessage(new HUDMessage(text, HUDMessage.achievement_type));
                Game1.chatBox?.addInfoMessage(text);
                Helper.Multiplayer.SendMessage(text, Message, new[] { ModId });
            }
        }
        Previous[key] = now;
    }

    private static JsonDocument BuildBoard()
    {
        var rows = Skills.Players().Select(who =>
        {
            Dictionary<string, int> categories = Totals(who, out int total, out int max);
            return new { key = who.userID.Value, name = who.Name, total, max, categories };
        }).ToList();
        return JsonDocument.Parse(JsonSerializer.Serialize(rows));
    }

    private static void WriteBoard()
    {
        if (string.IsNullOrEmpty(SV.StateDir))
            return;
        JsonDocument board = BuildBoard();
        string json = JsonSerializer.Serialize(board.RootElement, new JsonSerializerOptions { WriteIndented = true });
        if (json != lastJson)
        {
            Directory.CreateDirectory(SV.StateDir);
            string path = Path.Combine(SV.StateDir, "collection.json");
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
            lastJson = json;
        }
        completed ??= new();
        foreach (Farmer who in Skills.Players())
        {
            Dictionary<string, int> totals = Totals(who, out _, out _);
            HashSet<string> mine = completed.GetValueOrDefault(who.userID.Value) ?? new();
            foreach (Category category in Categories())
                if (category.Items.Count > 0 && totals.GetValueOrDefault(category.Name) == category.Items.Count && mine.Add(category.Name))
                    Skills.Announce($"{who.Name} completed their {category.Name} collection log!");
            completed[who.userID.Value] = mine;
        }
    }

    private static void OnMessage(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID == ModId && e.Type == Message && SV.Role == Role.Server)
            Skills.Announce(e.ReadAs<string>());
    }

    private sealed class LogMenu : IClickableMenu
    {
        private int tab;
        public LogMenu() : base(Game1.viewport.Width / 2 - 320, Game1.viewport.Height / 2 - 240, 640, 480, true) { }
        public override void receiveKeyPress(Microsoft.Xna.Framework.Input.Keys key)
        {
            if (key == Microsoft.Xna.Framework.Input.Keys.Escape || key == Microsoft.Xna.Framework.Input.Keys.L)
                exitThisMenu();
            else if (key == Microsoft.Xna.Framework.Input.Keys.Tab)
                tab = (tab + 1) % 5;
        }
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (y >= yPositionOnScreen + 48 && y < yPositionOnScreen + 82)
                tab = Math.Clamp((x - xPositionOnScreen - 16) / 115, 0, 4);
            if (tab == 4 && y > yPositionOnScreen + 110)
            {
                int index = (y - yPositionOnScreen - 110) / 32;
                string[] pets = Pets.Owned(Game1.player).ToArray();
                Game1.player.modData[Pets.ActiveKey] = index == 0 ? "" : pets.ElementAtOrDefault(index - 1) ?? "";
            }
        }
        public override void draw(SpriteBatch b)
        {
            drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
            string[] tabs = { "Fish", "Minerals", "Artifacts", "Rare", "Pets" };
            for (int i = 0; i < tabs.Length; i++)
                b.DrawString(Game1.smallFont, tabs[i], new Vector2(xPositionOnScreen + 20 + i * 115, yPositionOnScreen + 55), i == tab ? Color.Gold : Color.White);
            if (tab == 4)
            {
                b.DrawString(Game1.smallFont, "Following: " + (Game1.player.modData.GetValueOrDefault(Pets.ActiveKey, "") is { Length: > 0 } p ? Pets.NameOf(p) : "None"), new Vector2(xPositionOnScreen + 30, yPositionOnScreen + 105), Color.White);
                b.DrawString(Game1.smallFont, "None", new Vector2(xPositionOnScreen + 45, yPositionOnScreen + 140), Color.LightGray);
                int row = 1;
                foreach (string pet in Pets.Owned(Game1.player))
                    b.DrawString(Game1.smallFont, Pets.NameOf(pet), new Vector2(xPositionOnScreen + 45, yPositionOnScreen + 140 + row++ * 32), Color.White);
            }
            else
            {
                Category category = Categories()[tab];
                int got = category.Items.Count(x => Found(Game1.player, x, tab));
                b.DrawString(Game1.smallFont, $"{category.Name}: {got} / {category.Items.Count}", new Vector2(xPositionOnScreen + 30, yPositionOnScreen + 100), Color.White);
                for (int i = 0; i < Math.Min(14, category.Items.Count); i++)
                {
                    Entry item = category.Items[i];
                    b.DrawString(Game1.smallFont, Found(Game1.player, item, tab) ? item.Name : "???", new Vector2(xPositionOnScreen + 45, yPositionOnScreen + 135 + i * 22), Found(Game1.player, item, tab) ? Color.White : Color.Gray);
                }
            }
            drawMouse(b);
        }
    }
}
