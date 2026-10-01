namespace StembridgeValley.Host;

/// <summary>
/// Stardew and some mods ship pure-IL DLLs that are only *labelled* x64. .NET on an ARM server refuses to load
/// them because of that label. The code inside is processor-neutral, so relabel the server's own copy as ARM64.
/// Only IL-only files are touched; anything with real native code is left alone. The original is kept as *.x64-original.
/// </summary>
internal static class ArmPatch
{
    public static void Folder(string dir, bool recursive)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (string file in Directory.EnumerateFiles(dir, "*.dll", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            try { File(file); }
            catch (Exception ex) { Console.WriteLine($"ARM relabel skipped {file}: {ex.Message}"); }
        }
    }

    private static void File(string path)
    {
        byte[] b = System.IO.File.ReadAllBytes(path);
        if (b.Length < 0x200 || b[0] != (byte)'M' || b[1] != (byte)'Z')
            return;
        int pe = BitConverter.ToInt32(b, 0x3c);
        if (pe <= 0 || pe + 0x100 > b.Length || BitConverter.ToUInt32(b, pe) != 0x00004550)
            return;
        if (BitConverter.ToUInt16(b, pe + 4) != 0x8664)
            return;
        ushort magic = BitConverter.ToUInt16(b, pe + 24);
        int dataDirs = pe + 24 + (magic == 0x20b ? 112 : 96);
        uint clrRva = BitConverter.ToUInt32(b, dataDirs + 14 * 8);
        if (clrRva == 0)
            return; // not .NET
        int sections = BitConverter.ToUInt16(b, pe + 6);
        int sectionTable = pe + 24 + BitConverter.ToUInt16(b, pe + 20);
        for (int i = 0; i < sections; i++)
        {
            int s = sectionTable + i * 40;
            uint size = BitConverter.ToUInt32(b, s + 8), va = BitConverter.ToUInt32(b, s + 12), raw = BitConverter.ToUInt32(b, s + 20);
            uint rawSize = BitConverter.ToUInt32(b, s + 16);
            if (clrRva >= va && clrRva < va + Math.Max(size, rawSize))
            {
                uint flags = BitConverter.ToUInt32(b, (int)(raw + clrRva - va) + 16);
                if ((flags & 0x1) == 0 || (flags & 0x4) != 0)
                    return; // has native code (not IL-only, or ReadyToRun) - can't run on ARM anyway
                if (!System.IO.File.Exists(path + ".x64-original"))
                    System.IO.File.WriteAllBytes(path + ".x64-original", b);
                b[pe + 4] = 0x64; b[pe + 5] = 0xAA; // IMAGE_FILE_MACHINE_ARM64
                System.IO.File.WriteAllBytes(path, b);
                Console.WriteLine("ARM relabel: " + Path.GetFileName(path));
                return;
            }
        }
    }
}
