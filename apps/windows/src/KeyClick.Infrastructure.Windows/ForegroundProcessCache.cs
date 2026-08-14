using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeyClick.Infrastructure.Windows;

internal sealed class ForegroundProcessCache : IDisposable
{
  private const uint EventSystemForeground = 0x0003;
  private const uint WineventOutofcontext = 0x0000;
  private readonly WinEventDelegate _callback;
  private readonly nint _hook;
  private string? _currentExecutable;

  public ForegroundProcessCache()
  {
    _callback = ForegroundChanged;
    _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _callback, 0, 0, WineventOutofcontext);
    QueueResolve(GetForegroundWindow());
  }

  public string? CurrentExecutable => Volatile.Read(ref _currentExecutable);

  public void Dispose()
  {
    if (_hook != 0) UnhookWinEvent(_hook);
  }

  private void ForegroundChanged(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint eventTime) => QueueResolve(window);

  private void QueueResolve(nint window) => ThreadPool.UnsafeQueueUserWorkItem(static state => state.Owner.Resolve(state.Window), (Owner: this, Window: window), false);

  private void Resolve(nint window)
  {
    string? path = null;
    if (window != 0)
    {
      GetWindowThreadProcessId(window, out var processId);
      try
      {
        using var process = Process.GetProcessById((int)processId);
        path = process.MainModule?.FileName;
      }
      catch { }
    }
    Volatile.Write(ref _currentExecutable, path);
  }

  private delegate void WinEventDelegate(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint eventTime);

  [DllImport("user32.dll")]
  private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

  [DllImport("user32.dll")]
  private static extern bool UnhookWinEvent(nint hook);

  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();

  [DllImport("user32.dll")]
  private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
