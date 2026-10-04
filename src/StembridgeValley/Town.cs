using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace StembridgeValley;

/// <summary>
/// The town starts finished (owner decision, Oct 4): the Community Center is restored from day one on every world,
/// so the bus, minecarts, bridges, panning, the theater and every other town repair are open to everyone.
/// Progression comes from skills and farm projects instead.
///
/// Stardew decides almost every town repair from the host's mail flags, so the server sets those once on load
/// (players see them through the host). Each player also gets the Community Center opening ceremony marked as seen,
/// so nobody gets pulled into a cutscene for work they didn't do.
/// Off with "TownComplete": false in the server's config.
/// </summary>
internal static class Town
{
    /// <summary>The flags the game sets as each Community Center room is finished, plus the completion itself.</summary>
    private static readonly string[] Flags =
    {
        "ccPantry", "ccCraftsRoom", "ccFishTank", "ccBoilerRoom", "ccVault", "ccBulletin",
        "ccIsComplete", "ccMovieTheater",
    };

    /// <summary>The Community Center opening ceremony.</summary>
    private const string CeremonyEvent = "191393";

    public static void Apply(IModHelper helper)
    {
        if (SV.Role == Role.Server)
            helper.Events.GameLoop.SaveLoaded += OnServerLoaded;
        else
            helper.Events.GameLoop.SaveLoaded += OnJoined;
    }

    public static bool IsComplete => Game1.MasterPlayer?.mailReceived.Contains("ccIsComplete") == true;

    private static void OnServerLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (!SV.Config.Server.TownComplete || !Game1.IsServer)
            return;
        Farmer host = Game1.player;
        var added = Flags.Where(f => host.mailReceived.Add(f)).ToList();
        host.eventsSeen.Add(CeremonyEvent);

        if (Game1.getLocationFromName("CommunityCenter") is CommunityCenter cc)
        {
            for (int i = 0; i < cc.areasComplete.Count; i++)
                cc.areasComplete[i] = true;
            foreach (var bundle in cc.bundles.FieldDict.Values)
                for (int i = 0; i < bundle.Count; i++)
                    bundle[i] = true;
        }
        if (Game1.getLocationFromName("Beach") is Beach beach)
            beach.bridgeFixed.Value = true;

        // Redraw the repaired town on the server's own copy of the maps (players redraw theirs when they arrive).
        foreach (string name in new[] { "Town", "Mountain", "BusStop", "Beach", "CommunityCenter", "Forest" })
            Game1.getLocationFromName(name)?.MakeMapModifications();

        Log.Info(added.Count > 0
            ? $"Town: Community Center finished for everyone ({string.Join(", ", added)})."
            : "Town: already finished.");
    }

    private static void OnJoined(object? sender, SaveLoadedEventArgs e)
    {
        if (IsComplete)
            Game1.player.eventsSeen.Add(CeremonyEvent);
    }
}
