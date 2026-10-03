using StardewModdingAPI;
using StardewValley;

namespace StembridgeValley;

/// <summary>Test-only: SV_DUMP_MAP=BusStop,Town logs map tiles/actions so map edits can be planned. Off unless the variable is set.</summary>
internal static class MapDump
{
    public static void Run()
    {
        string? want = Environment.GetEnvironmentVariable("SV_DUMP_MAP");
        if (string.IsNullOrEmpty(want))
            return;
        foreach (string asset in (Environment.GetEnvironmentVariable("SV_DUMP_ASSETS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            try
            {
                var t = Game1.content.Load<Microsoft.Xna.Framework.Graphics.Texture2D>(asset);
                using var af = File.Create(Path.Combine(SV.StateDir, "asset-" + asset.Replace('\\', '_').Replace('/', '_') + ".png"));
                t.SaveAsPng(af, t.Width, t.Height);
            }
            catch (Exception ex)
            {
                Log.Info($"[mapdump] couldn't export {asset}: {ex.Message}");
            }
        try
        {
            Log.Info($"[mapdump] BusStop.1 = {Game1.content.LoadString("Strings\\Locations:BusStop.1")}");
            var tex = Game1.content.Load<Microsoft.Xna.Framework.Graphics.Texture2D>("Maps\\spring_outdoorsTileSheet");
            int cols = tex.Width / 16;
            int[] ids = { 384, 385, 409, 410, 411, 434, 435, 436, 359 };
            var data = new Microsoft.Xna.Framework.Color[16 * 16];
            var outTex = new Microsoft.Xna.Framework.Graphics.Texture2D(Game1.graphics.GraphicsDevice, 16 * ids.Length, 16);
            for (int i = 0; i < ids.Length; i++)
            {
                tex.GetData(0, new Microsoft.Xna.Framework.Rectangle(ids[i] % cols * 16, ids[i] / cols * 16, 16, 16), data, 0, data.Length);
                outTex.SetData(0, new Microsoft.Xna.Framework.Rectangle(i * 16, 0, 16, 16), data, 0, data.Length);
            }
            using var fs = File.Create(Path.Combine(SV.StateDir, "tiles.png"));
            outTex.SaveAsPng(fs, outTex.Width, outTex.Height);
            Log.Info($"[mapdump] tiles.png written: {string.Join(" ", ids)} (sheet {cols} cols)");
            // Whole tile sheets of the first map, so a preview of an edit can be drawn offline.
            if (Game1.getLocationFromName(want.Split(',')[0].Trim())?.map is { } m0)
                foreach (var sheet in m0.TileSheets)
                {
                    var t = Game1.content.Load<Microsoft.Xna.Framework.Graphics.Texture2D>(sheet.ImageSource);
                    using var sf = File.Create(Path.Combine(SV.StateDir, $"sheet-{sheet.Id}.png"));
                    t.SaveAsPng(sf, t.Width, t.Height);
                }
        }
        catch (Exception ex)
        {
            Log.Info($"[mapdump] tile export failed: {ex.Message}");
        }
        try
        {
            var locs = Game1.content.Load<Dictionary<string, StardewValley.GameData.Locations.LocationData>>("Data\\Locations");
            Log.Info($"[mapdump] farm location data: {string.Join(" ", locs.Keys.Where(k => k.StartsWith("Farm")))}");
            foreach (var f in Game1.content.Load<List<StardewValley.GameData.ModFarmType>>("Data\\AdditionalFarms"))
                Log.Info($"[mapdump] additional farm: {f.Id} map={f.MapName} tooltip={f.TooltipStringPath} icon={f.IconTexture} mail={f.SpawnMonstersByDefault}");
        }
        catch (Exception ex)
        {
            Log.Info($"[mapdump] data: {ex.Message}");
        }
        foreach (string name in want.Split(','))
        {
            GameLocation? loc = Game1.getLocationFromName(name.Trim());
            xTile.Map? rawMap = null;
            if (name.Trim().StartsWith("Maps"))
                try { rawMap = Game1.content.Load<xTile.Map>(name.Trim()); } catch (Exception ex) { Log.Info($"[mapdump] {name}: {ex.Message}"); }
            if (loc?.map == null && rawMap == null)
                continue;
            var map = rawMap ?? loc!.map;
            if (rawMap != null)
                foreach (var sheet in map.TileSheets)
                    try
                    {
                        var t = Game1.content.Load<Microsoft.Xna.Framework.Graphics.Texture2D>(sheet.ImageSource);
                        using var sf = File.Create(Path.Combine(SV.StateDir, $"sheet-{sheet.Id}.png"));
                        t.SaveAsPng(sf, t.Width, t.Height);
                    }
                    catch { }
            Log.Info($"[mapdump] {name} props: {string.Join(" | ", map.Properties.Select(p => p.Key + "=" + p.Value))}");
            Log.Info($"[mapdump] {name} {map.Layers[0].LayerWidth}x{map.Layers[0].LayerHeight} sheets: {string.Join(" ", map.TileSheets.Select(t => t.Id + "=" + t.ImageSource))}");
            bool all = (Environment.GetEnvironmentVariable("SV_DUMP_ALL") ?? "").Split(',').Contains(name.Trim());
            using var full = all ? new StreamWriter(Path.Combine(SV.StateDir, $"mapdump-{name.Trim().Replace('\\', '_').Replace('/', '_')}.txt")) : null;
            foreach (var layer in map.Layers)
                for (int y = 0; y < layer.LayerHeight; y++)
                    for (int x = 0; x < layer.LayerWidth; x++)
                    {
                        var t = layer.Tiles[x, y];
                        if (t == null)
                            continue;
                        string props = string.Join(";", t.Properties.Select(p => p.Key + "=" + p.Value)
                            .Concat(t.TileIndexProperties.Select(p => "i:" + p.Key + "=" + p.Value)));
                        if (full != null)
                        {
                            full.WriteLine($"{layer.Id} {x},{y} {t.TileSheet.Id}#{t.TileIndex} {props}");
                            continue;
                        }
                        bool interesting = props.Contains("Action") || props.Contains("Message") || (name == "BusStop" && x <= 34 && y >= 10 && y <= 29);
                        if (interesting)
                            Log.Info($"[mapdump] {name} {layer.Id} {x},{y} {t.TileSheet.Id}#{t.TileIndex} {props}");
                    }
        }
    }
}
