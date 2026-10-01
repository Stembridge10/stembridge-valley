// Stand-in for StardewModdingAPI.exe in the launcher test: records how it was started,
// and the first time asks for an update (like a real server with newer mods would).
string dir = AppContext.BaseDirectory;
string state = Environment.GetEnvironmentVariable("SV_STATE_DIR") ?? "";
var lines = new List<string> { "args=" + string.Join(" | ", args) };
foreach (string k in new[] { "SV_ROLE", "SV_ADDRESS", "SV_PASSWORD", "SV_PLAYER_KEY", "SV_PACK_VERSION", "SV_STATE_DIR" })
    lines.Add(k + "=" + Environment.GetEnvironmentVariable(k));
int run = Directory.GetFiles(dir, "run-*.txt").Length + 1;
File.WriteAllLines(Path.Combine(dir, $"run-{run}.txt"), lines);
string bump = Path.Combine(dir, "bump-on-first-run.txt");
if (run == 1 && File.Exists(bump))
{
    // Publish the newer pack, then report "out of date" like the mod would.
    string[] parts = File.ReadAllLines(bump);
    File.Copy(parts[0], parts[1], true);
    File.WriteAllText(Path.Combine(state, "update-needed.txt"), "newer");
}
return 0;
