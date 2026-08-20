using System.Runtime.InteropServices;
using System.Threading.Channels;
using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public sealed class PointerActionService
{
  public bool Execute(PointerActionKind action)
  {
    var mouseFlags = action switch
    {
      PointerActionKind.LeftClick => (Down: 0x0002u, Up: 0x0004u),
      PointerActionKind.RightClick => (Down: 0x0008u, Up: 0x0010u),
      PointerActionKind.MiddleClick => (Down: 0x0020u, Up: 0x0040u),
      _ => (Down: 0u, Up: 0u)
    };
    if (mouseFlags.Down != 0)
    {
      mouse_event(mouseFlags.Down, 0, 0, 0, 0);
      mouse_event(mouseFlags.Up, 0, 0, 0, 0);
      return true;
    }
    if (action == PointerActionKind.DisableButton) return true;
    var key = action switch
    {
      PointerActionKind.BrowserBack => 0xA6,
      PointerActionKind.BrowserForward => 0xA7,
      PointerActionKind.MediaPlayPause => 0xB3,
      PointerActionKind.VolumeUp => 0xAF,
      PointerActionKind.VolumeDown => 0xAE,
      _ => 0
    };
    if (key != 0) { PressKey((byte)key); return true; }
    if (action == PointerActionKind.ShowDesktop)
    {
      keybd_event(0x5B, 0, 0, 0);
      keybd_event(0x44, 0, 0, 0);
      keybd_event(0x44, 0, 2, 0);
      keybd_event(0x5B, 0, 2, 0);
      return true;
    }
    return false;
  }

  private static void PressKey(byte key)
  {
    keybd_event(key, 0, 0, 0);
    keybd_event(key, 0, 2, 0);
  }

  [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);
  [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extraInfo);
}

public sealed class PointerSuppressionService : IDisposable
{
  private const int HookMouseLowLevel = 14;
  private const uint WmHotkey = 0x0312;
  private const uint WmQuit = 0x0012;
  private const int PanicHotkeyId = 0x4B43;
  private readonly Channel<PointerActionKind> _actions = Channel.CreateBounded<PointerActionKind>(new BoundedChannelOptions(64)
  {
    SingleReader = true,
    SingleWriter = true,
    FullMode = BoundedChannelFullMode.DropOldest,
    AllowSynchronousContinuations = false
  });
  private readonly object _gate = new();
  private Thread? _hookThread;
  private Thread? _dispatchThread;
  private CancellationTokenSource? _dispatchCancellation;
  private HookProcedure? _hookProcedure;
  private nint _hook;
  private uint _hookThreadId;
  private readonly ManualResetEventSlim _hookReady = new(false);
  private bool _hookStartupSucceeded;
  private IReadOnlyDictionary<PointerButtonKind, PointerButtonBinding> _bindings = new Dictionary<PointerButtonKind, PointerButtonBinding>();

  public event Action<PointerActionKind>? ActionRequested;
  public event Action? PanicTriggered;
  public bool IsActive => _hook != 0;

  public bool Configure(PointerStudioSettings settings)
  {
    var bindings = settings.ExperimentalSuppressionEnabled
      ? settings.ButtonBindings.Where(binding => binding.DeviceId == "*" && binding.SuppressOriginal && binding.Action != PointerActionKind.None)
        .GroupBy(binding => binding.Button).ToDictionary(group => group.Key, group => group.First())
      : new Dictionary<PointerButtonKind, PointerButtonBinding>();
    Volatile.Write(ref _bindings, bindings);
    if (bindings.Count > 0)
    {
      var started = EnsureStarted();
      if (!started) Stop();
      return started;
    }
    Stop();
    return true;
  }

  private bool EnsureStarted()
  {
    lock (_gate)
    {
      if (_hookThread is not null) return _hook != 0;
      _hookReady.Reset();
      _hookStartupSucceeded = false;
      _hookThread = new Thread(HookLoop) { IsBackground = true, Name = "KeyClick Pointer Suppression", Priority = ThreadPriority.AboveNormal };
      _hookThread.Start();
    }
    var ready = _hookReady.Wait(TimeSpan.FromSeconds(2)) && _hookStartupSucceeded;
    if (ready)
    {
      while (_actions.Reader.TryRead(out _)) { }
      var cancellation = new CancellationTokenSource();
      _dispatchCancellation = cancellation;
      _dispatchThread = new Thread(() => DispatchActions(cancellation.Token)) { IsBackground = true, Name = "KeyClick Pointer Action Dispatch", Priority = ThreadPriority.BelowNormal };
      _dispatchThread.Start();
    }
    return ready;
  }

  private void HookLoop()
  {
    _hookThreadId = GetCurrentThreadId();
    if (!RegisterHotKey(0, PanicHotkeyId, 0x0001 | 0x0002 | 0x0004, 0x7B))
    {
      Volatile.Write(ref _bindings, new Dictionary<PointerButtonKind, PointerButtonBinding>());
      _hookReady.Set();
      return;
    }
    _hookProcedure = HookCallback;
    _hook = SetWindowsHookEx(HookMouseLowLevel, _hookProcedure, 0, 0);
    _hookStartupSucceeded = _hook != 0;
    _hookReady.Set();
    if (_hook == 0)
    {
      UnregisterHotKey(0, PanicHotkeyId);
      Volatile.Write(ref _bindings, new Dictionary<PointerButtonKind, PointerButtonBinding>());
      return;
    }
    while (GetMessage(out var message, 0, 0, 0) > 0)
    {
      if (message.Value == WmHotkey && message.WParam == PanicHotkeyId)
      {
        Volatile.Write(ref _bindings, new Dictionary<PointerButtonKind, PointerButtonBinding>());
        PanicTriggered?.Invoke();
      }
      TranslateMessage(ref message);
      DispatchMessage(ref message);
    }
    UnregisterHotKey(0, PanicHotkeyId);
    if (_hook != 0) UnhookWindowsHookEx(_hook);
    _hook = 0;
  }

  private nint HookCallback(int code, nint message, nint data)
  {
    if (code < 0) return CallNextHookEx(_hook, code, message, data);
    var input = Marshal.PtrToStructure<LowLevelMouseInput>(data);
    if ((input.Flags & 1) != 0) return CallNextHookEx(_hook, code, message, data);
    if (!TryButton((uint)message, input.MouseData, out var button, out var completed)) return CallNextHookEx(_hook, code, message, data);
    var bindings = Volatile.Read(ref _bindings);
    if (!bindings.TryGetValue(button, out var binding)) return CallNextHookEx(_hook, code, message, data);
    if (completed) _actions.Writer.TryWrite(binding.Action);
    return 1;
  }

  private static bool TryButton(uint message, uint mouseData, out PointerButtonKind button, out bool completed)
  {
    completed = message is 0x0202 or 0x0205 or 0x0208 or 0x020C or 0x020A or 0x020E;
    button = message switch
    {
      0x0201 or 0x0202 => PointerButtonKind.Left,
      0x0204 or 0x0205 => PointerButtonKind.Right,
      0x0207 or 0x0208 => PointerButtonKind.Middle,
      0x020B or 0x020C => (mouseData >> 16) == 1 ? PointerButtonKind.X1 : PointerButtonKind.X2,
      0x020A => unchecked((short)(mouseData >> 16)) > 0 ? PointerButtonKind.WheelUp : PointerButtonKind.WheelDown,
      0x020E => unchecked((short)(mouseData >> 16)) > 0 ? PointerButtonKind.WheelRight : PointerButtonKind.WheelLeft,
      _ => PointerButtonKind.Middle
    };
    return message is 0x0201 or 0x0202 or 0x0204 or 0x0205 or 0x0207 or 0x0208 or 0x020B or 0x020C or 0x020A or 0x020E;
  }

  private void DispatchActions(CancellationToken cancellationToken)
  {
    try
    {
      while (_actions.Reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
        while (_actions.Reader.TryRead(out var action)) ActionRequested?.Invoke(action);
    }
    catch (OperationCanceledException) { }
  }

  private void Stop()
  {
    Thread? hook;
    Thread? dispatch;
    CancellationTokenSource? dispatchCancellation;
    lock (_gate)
    {
      hook = _hookThread;
      if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WmQuit, 0, 0);
      _hookThread = null;
      _hookThreadId = 0;
      dispatch = _dispatchThread;
      _dispatchThread = null;
      dispatchCancellation = _dispatchCancellation;
      _dispatchCancellation = null;
    }
    dispatchCancellation?.Cancel();
    hook?.Join(TimeSpan.FromSeconds(1));
    dispatch?.Join(TimeSpan.FromSeconds(1));
    dispatchCancellation?.Dispose();
  }

  public void Dispose()
  {
    Stop();
    _actions.Writer.TryComplete();
  }

  private delegate nint HookProcedure(int code, nint message, nint data);
  [StructLayout(LayoutKind.Sequential)] private struct LowLevelMouseInput { public Point Point; public uint MouseData; public uint Flags; public uint Time; public nuint ExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
  [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Value; public nint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }

  [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int hook, HookProcedure procedure, nint module, uint threadId);
  [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
  [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
  [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
  [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
  [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
  [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);
  [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
  [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
  [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
