using System.Runtime.InteropServices;
using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public sealed class GlobalShortcutService : IGlobalShortcutService
{
  private const uint WmHotkey = 0x0312;
  private const uint WmClose = 0x0010;
  private const uint WmDestroy = 0x0002;
  private const uint ModAlt = 0x0001;
  private const uint ModControl = 0x0002;
  private const uint ModShift = 0x0004;
  private const uint ModWin = 0x0008;
  private const uint ModNoRepeat = 0x4000;
  private static readonly nint HwndMessage = new(-3);

  private readonly ManualResetEventSlim _ready = new(false);
  private readonly Dictionary<int, ShortcutBinding> _bindings = [];
  private Thread? _thread;
  private WndProc? _windowProc;
  private nint _window;

  public event EventHandler<string>? CommandInvoked;

  public bool ReplaceBindings(IEnumerable<ShortcutBinding> bindings, out string? error)
  {
    EnsureStarted();
    var allBindings = bindings.ToArray();
    var validationError = ShortcutBindingValidator.Validate(allBindings);
    if (validationError is not null) { error = validationError; return false; }
    var candidates = allBindings.Where(item => item.Enabled && item.Scope == ShortcutScope.Global && item.Kind == ShortcutKind.Chord && item.Steps.Count == 1).ToArray();
    var duplicates = candidates.GroupBy(item => item.Steps[0]).FirstOrDefault(group => group.Count() > 1);
    if (duplicates is not null)
    {
      error = $"The shortcut {duplicates.Key.Display} is assigned more than once.";
      return false;
    }

    lock (_bindings)
    {
      var previous = _bindings.Values.ToArray();
      UnregisterAll();
      var id = 1000;
      foreach (var candidate in candidates)
      {
        var step = candidate.Steps[0];
        if (!RegisterHotKey(_window, id, Modifiers(step), (uint)step.VirtualKey))
        {
          UnregisterAll();
          foreach (var old in previous)
          {
            var oldStep = old.Steps[0];
            if (RegisterHotKey(_window, id, Modifiers(oldStep), (uint)oldStep.VirtualKey)) _bindings[id++] = old;
          }
          error = $"Windows could not register {candidate.Gesture}; it may already be used by another app.";
          return false;
        }
        _bindings[id++] = candidate;
      }
    }

    error = null;
    return true;
  }

  public void Dispose()
  {
    lock (_bindings) UnregisterAll();
    if (_window != 0) PostMessage(_window, WmClose, 0, 0);
    _thread?.Join(TimeSpan.FromSeconds(1));
    _ready.Dispose();
  }

  private static uint Modifiers(ShortcutStep step)
  {
    var result = ModNoRepeat;
    if (step.Alt) result |= ModAlt;
    if (step.Control) result |= ModControl;
    if (step.Shift) result |= ModShift;
    if (step.Windows) result |= ModWin;
    return result;
  }

  private void EnsureStarted()
  {
    if (_thread is not null) return;
    _thread = new Thread(MessageLoop) { IsBackground = true, Name = "KeyClick Shortcuts" };
    _thread.SetApartmentState(ApartmentState.STA);
    _thread.Start();
    if (!_ready.Wait(TimeSpan.FromSeconds(3)) || _window == 0)
      throw new InvalidOperationException("KeyClick could not initialize global shortcuts.");
  }

  private void MessageLoop()
  {
    _windowProc = WindowProcedure;
    var className = $"KeyClick.Shortcuts.{Environment.ProcessId}";
    var module = GetModuleHandle(null);
    var windowClass = new WndClassEx
    {
      Size = (uint)Marshal.SizeOf<WndClassEx>(),
      Instance = module,
      WindowProcedure = _windowProc,
      ClassName = className
    };
    if (RegisterClassEx(ref windowClass) != 0)
      _window = CreateWindowEx(0, className, "KeyClick Shortcuts", 0, 0, 0, 0, 0, HwndMessage, 0, module, 0);
    _ready.Set();
    while (_window != 0 && GetMessage(out var message, 0, 0, 0) > 0)
    {
      TranslateMessage(ref message);
      DispatchMessage(ref message);
    }
  }

  private nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
  {
    switch (message)
    {
      case WmHotkey:
        ShortcutBinding? binding;
        lock (_bindings) _bindings.TryGetValue((int)wParam, out binding);
        if (binding is not null) CommandInvoked?.Invoke(this, binding.CommandId);
        return 0;
      case WmClose:
        DestroyWindow(window);
        return 0;
      case WmDestroy:
        _window = 0;
        PostQuitMessage(0);
        return 0;
      default:
        return DefWindowProc(window, message, wParam, lParam);
    }
  }

  private void UnregisterAll()
  {
    foreach (var id in _bindings.Keys) UnregisterHotKey(_window, id);
    _bindings.Clear();
  }

  [UnmanagedFunctionPointer(CallingConvention.Winapi)]
  private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct WndClassEx
  {
    public uint Size;
    public uint Style;
    public WndProc? WindowProcedure;
    public int ClassExtra;
    public int WindowExtra;
    public nint Instance;
    public nint Icon;
    public nint Cursor;
    public nint Background;
    public string? MenuName;
    public string ClassName;
    public nint SmallIcon;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct Message
  {
    public nint Window;
    public uint Value;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public int X;
    public int Y;
    public uint Private;
  }

  [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
  [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx windowClass);
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
  [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
  [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
  [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
  [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
  [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
  [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
  [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
}
