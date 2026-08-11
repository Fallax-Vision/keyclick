using System.Runtime.InteropServices;

namespace KeyClick.App;

internal static class FullscreenDetector
{
  public static bool IsForegroundFullscreen()
  {
    var foreground = GetForegroundWindow();
    if (foreground == 0 || IsIconic(foreground)) return false;
    if (!GetWindowRect(foreground, out var window)) return false;
    var monitor = MonitorFromWindow(foreground, 2);
    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
    return GetMonitorInfo(monitor, ref info) &&
      window.Left <= info.Monitor.Left && window.Top <= info.Monitor.Top &&
      window.Right >= info.Monitor.Right && window.Bottom >= info.Monitor.Bottom;
  }

  [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
  [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
  [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
  [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
  [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
