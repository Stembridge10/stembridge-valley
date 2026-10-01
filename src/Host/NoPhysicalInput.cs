using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace StembridgeValley.Host;

/// <summary>The hidden copy ignores the real keyboard, mouse and controller, and never pauses for being "unfocused".</summary>
internal static class NoPhysicalInput
{
    public static void Apply(Harmony harmony)
    {
        foreach (var m in typeof(Keyboard).GetMethods().Where(m => m.Name == "GetState"))
            harmony.Patch(m, new HarmonyMethod(typeof(NoPhysicalInput), nameof(Keys)));
        foreach (var m in typeof(Mouse).GetMethods().Where(m => m.Name == "GetState"))
            harmony.Patch(m, new HarmonyMethod(typeof(NoPhysicalInput), nameof(MouseState)));
        foreach (var m in typeof(GamePad).GetMethods().Where(m => m.Name == "GetState"))
            harmony.Patch(m, new HarmonyMethod(typeof(NoPhysicalInput), nameof(Pad)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Game), "IsActive"), new HarmonyMethod(typeof(NoPhysicalInput), nameof(Active)));
    }

    private static bool Keys(ref KeyboardState __result) { __result = default; return false; }
    private static bool MouseState(ref Microsoft.Xna.Framework.Input.MouseState __result) { __result = default; return false; }
    private static bool Pad(ref GamePadState __result) { __result = default; return false; }
    private static bool Active(ref bool __result) { __result = true; return false; }
}
