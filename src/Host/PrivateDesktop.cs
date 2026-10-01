using System.Runtime.InteropServices;
using System.Text;

namespace StembridgeValley.Host;

/// <summary>Starts a child process on its own invisible Windows desktop, so its windows can never appear over (or steal focus from) the owner's games.</summary>
internal static class PrivateDesktop
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? reserved; public string? desktop; public string? title;
        public int x, y, width, height, xChars, yChars, fill, flags;
        public short show, reserved2;
        public IntPtr reservedPtr, input, output, error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref StartupInfo start, out ProcessInfo info);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr handle, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public static int Run(string app, string arguments, string workingDir, string instance, Func<bool> shouldStop)
    {
        string name = "StembridgeValley-" + Environment.ProcessId;
        IntPtr desk = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
        if (desk == IntPtr.Zero)
            throw new Exception("CreateDesktop failed " + Marshal.GetLastWin32Error());

        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), desktop = "WinSta0\\" + name, flags = 1, show = 0 };
        var command = new StringBuilder('"' + app + "\" " + arguments);
        // CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS (never slow down the owner's own games)
        if (!CreateProcess(app, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000 | 0x00004000, IntPtr.Zero, workingDir, ref startup, out var process))
        {
            CloseDesktop(desk);
            throw new Exception("CreateProcess failed " + Marshal.GetLastWin32Error());
        }
        File.WriteAllText(Path.Combine(instance, "game-pid.txt"), process.pid.ToString());
        try
        {
            while (WaitForSingleObject(process.process, 2000) != 0)
            {
                if (shouldStop())
                {
                    TerminateProcess(process.process, 0);
                    WaitForSingleObject(process.process, 10_000);
                    break;
                }
            }
            GetExitCodeProcess(process.process, out uint code);
            return (int)code;
        }
        finally
        {
            CloseHandle(process.thread);
            CloseHandle(process.process);
            CloseDesktop(desk);
        }
    }
}
