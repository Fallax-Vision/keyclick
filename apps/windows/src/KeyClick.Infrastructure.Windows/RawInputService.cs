using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public sealed class RawInputService : IRawInputService
{
  private const uint WmInput = 0x00FF;
  private const uint WmInputDeviceChange = 0x00FE;
  private const uint WmClose = 0x0010;
  private const uint WmDestroy = 0x0002;
  private const uint RidInput = 0x10000003;
  private const uint RidiDeviceName = 0x20000007;
  private const uint RidiDeviceInfo = 0x2000000B;
  private const uint RidevInputSink = 0x00000100;
  private const uint RidevDevNotify = 0x00002000;
  private const ushort RiKeyE0 = 0x0002;
  private const ushort RiKeyE1 = 0x0004;
  private const int RimTypeMouse = 0;
  private const int RimTypeKeyboard = 1;
  private static readonly nint HwndMessage = new(-3);

  private readonly ConcurrentDictionary<nint, DeviceDescriptor> _devices = new();
  private readonly ConcurrentDictionary<nint, byte> _pendingDeviceLookups = new();
  private readonly Dictionary<(nint Device, bool Horizontal), WheelAccumulator> _wheelAccumulators = [];
  private readonly HashSet<(nint Device, int ScanCode)> _pressedKeys = new(512);
  private readonly ForegroundProcessCache _foreground = new();
  private readonly ManualResetEventSlim _ready = new(false);
  private readonly Channel<InputActionEvent> _actions = Channel.CreateBounded<InputActionEvent>(new BoundedChannelOptions(4096)
  {
    SingleReader = true,
    SingleWriter = true,
    FullMode = BoundedChannelFullMode.Wait,
    AllowSynchronousContinuations = false
  });
  private Thread? _thread;
  private Thread? _dispatchThread;
  private nint _window;
  private WndProc? _windowProc;

  public event EventHandler<InputActionEvent>? InputAction;
  public event EventHandler<InputDeviceDescriptor>? DeviceChanged;
  public event Action<PointerMovementSignal>? PointerMoved;

  public IReadOnlyList<InputDeviceDescriptor> EnumeratePointerDevices()
  {
    uint count = 0;
    var entrySize = (uint)Marshal.SizeOf<RawInputDeviceListEntry>();
    if (GetRawInputDeviceList(null, ref count, entrySize) == uint.MaxValue || count == 0 || count > 256) return [];
    var entries = new RawInputDeviceListEntry[count];
    if (GetRawInputDeviceList(entries, ref count, entrySize) == uint.MaxValue) return [];
    var devices = new List<InputDeviceDescriptor>((int)count);
    foreach (var entry in entries.Take((int)count))
    {
      if (entry.Type != RimTypeMouse || entry.Device == 0) continue;
      var descriptor = ResolveDescriptor(entry.Device, DeviceFamily.UnknownPointer);
      _devices[entry.Device] = descriptor;
      devices.Add(ToPublic(descriptor, true));
    }
    return devices;
  }

  public void Start()
  {
    if (_thread is not null) return;
    _dispatchThread = new Thread(DispatchActions)
    {
      IsBackground = true,
      Name = "KeyClick Input Dispatch",
      Priority = ThreadPriority.AboveNormal
    };
    _dispatchThread.Start();
    _thread = new Thread(MessageLoop)
    {
      IsBackground = true,
      Name = "KeyClick Raw Input",
      Priority = ThreadPriority.AboveNormal
    };
    _thread.SetApartmentState(ApartmentState.STA);
    _thread.Start();
    if (!_ready.Wait(TimeSpan.FromSeconds(3)) || _window == 0)
    {
      throw new InvalidOperationException("KeyClick could not initialize Windows Raw Input.");
    }
  }

  public void Dispose()
  {
    if (_window != 0) PostMessage(_window, WmClose, 0, 0);
    _thread?.Join(TimeSpan.FromSeconds(1));
    _actions.Writer.TryComplete();
    _dispatchThread?.Join(TimeSpan.FromSeconds(1));
    _foreground.Dispose();
    _ready.Dispose();
  }

  private void MessageLoop()
  {
    _windowProc = WindowProcedure;
    var className = $"KeyClick.RawInput.{Environment.ProcessId}";
    var module = GetModuleHandle(null);
    var windowClass = new WndClassEx
    {
      Size = (uint)Marshal.SizeOf<WndClassEx>(),
      Instance = module,
      WindowProcedure = _windowProc,
      ClassName = className
    };
    if (RegisterClassEx(ref windowClass) == 0)
    {
      _ready.Set();
      return;
    }

    _window = CreateWindowEx(0, className, "KeyClick Input", 0, 0, 0, 0, 0, HwndMessage, 0, module, 0);
    if (_window != 0)
    {
      var devices = new[]
      {
        new RawInputDevice(0x01, 0x06, RidevInputSink | RidevDevNotify, _window),
        new RawInputDevice(0x01, 0x02, RidevInputSink | RidevDevNotify, _window)
      };
      if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
      {
        DestroyWindow(_window);
        _window = 0;
      }
    }
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
      case WmInput:
        try { ProcessRawInput(lParam); }
        catch { }
        return 0;
      case WmInputDeviceChange:
        _devices.TryRemove(lParam, out var removed);
        _pendingDeviceLookups.TryRemove(lParam, out _);
        _pressedKeys.RemoveWhere(item => item.Device == lParam);
        lock (_wheelAccumulators)
          foreach (var key in _wheelAccumulators.Keys.Where(key => key.Device == lParam).ToArray()) _wheelAccumulators.Remove(key);
        if (removed is not null) DeviceChanged?.Invoke(this, ToPublic(removed, false));
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

  private unsafe void ProcessRawInput(nint rawInputHandle)
  {
    uint size = 0;
    var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
    if (GetRawInputData(rawInputHandle, RidInput, 0, ref size, headerSize) == uint.MaxValue || size > 256) return;
    byte* buffer = stackalloc byte[256];
    if (GetRawInputData(rawInputHandle, RidInput, (nint)buffer, ref size, headerSize) != size) return;
    var input = Marshal.PtrToStructure<RawInput>((nint)buffer);

    if (input.Header.Type == RimTypeKeyboard)
    {
      ProcessKeyboard(input.Header.Device, input.Data.Keyboard);
    }
    else if (input.Header.Type == RimTypeMouse)
    {
      ProcessMouse(input.Header.Device, input.Data.Mouse);
    }
  }

  private void ProcessKeyboard(nint device, RawKeyboard keyboard)
  {
    if (keyboard.VirtualKey == 0x00FF) return;
    var extended = (keyboard.Flags & (RiKeyE0 | RiKeyE1)) != 0;
    var scanCode = keyboard.MakeCode | ((keyboard.Flags & RiKeyE0) != 0 ? 0xE000 : 0) | ((keyboard.Flags & RiKeyE1) != 0 ? 0xE100 : 0);
    var phase = InputEventRules.IsKeyboardRelease(keyboard.Flags, keyboard.VirtualKey) ? InputPhase.Up : InputPhase.Down;
    var stateKey = (device, scanCode);
    if (phase == InputPhase.Down)
    {
      if (!_pressedKeys.Add(stateKey)) return;
    }
    else if (!_pressedKeys.Remove(stateKey))
    {
      return;
    }
    var isLockKey = keyboard.VirtualKey is 0x14 or 0x90 or 0x91;
    var variant = InputEventRules.ResolveVariant(
      (GetKeyState(0xA5) & 0x8000) != 0,
      (GetKeyState(0x10) & 0x8000) != 0,
      isLockKey,
      isLockKey && (GetKeyState(keyboard.VirtualKey) & 1) != 0);
    var identity = new InputIdentity(InputKind.KeyboardKey, scanCode, extended, DeviceFamily.Keyboard, DescribeDevice(device, DeviceFamily.Keyboard).Id);
    Emit(new InputActionEvent(
      identity,
      keyboard.VirtualKey,
      variant,
      KeyClassifier.ClassifyKeyboard(keyboard.VirtualKey),
      phase,
      Stopwatch.GetTimestamp(),
      _foreground.CurrentExecutable,
      phase == InputPhase.Up ? new ShortcutStep(
        (GetKeyState(0x11) & 0x8000) != 0,
        (GetKeyState(0x12) & 0x8000) != 0,
        (GetKeyState(0x10) & 0x8000) != 0,
        (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0,
        keyboard.VirtualKey) : null));
  }

  private void ProcessMouse(nint device, RawMouse mouse)
  {
    var descriptor = DescribeDevice(device, DeviceFamily.UnknownPointer);
    if (mouse.LastX != 0 || mouse.LastY != 0)
      PointerMoved?.Invoke(new(mouse.LastX, mouse.LastY, Stopwatch.GetTimestamp()));
    var flags = mouse.ButtonFlags;
    if ((flags & 0x0002) != 0) EmitPointer(descriptor, 1, InputKind.PointerButton);
    if ((flags & 0x0008) != 0) EmitPointer(descriptor, 2, InputKind.PointerButton);
    if ((flags & 0x0020) != 0) EmitPointer(descriptor, 3, InputKind.PointerButton);
    if ((flags & 0x0080) != 0) EmitPointer(descriptor, 4, InputKind.PointerButton);
    if ((flags & 0x0200) != 0) EmitPointer(descriptor, 5, InputKind.PointerButton);
    if ((flags & 0x0400) != 0) AccumulateWheel(descriptor, false, (short)mouse.ButtonData);
    if ((flags & 0x0800) != 0) AccumulateWheel(descriptor, true, (short)mouse.ButtonData);
  }

  private void AccumulateWheel(DeviceDescriptor descriptor, bool horizontal, int delta)
  {
    var key = (descriptor.Handle, horizontal);
    lock (_wheelAccumulators)
    {
      if (!_wheelAccumulators.TryGetValue(key, out var accumulator))
      {
        accumulator = new WheelAccumulator();
        _wheelAccumulators[key] = accumulator;
      }
      var detents = accumulator.AddDetents(delta);
      var direction = Math.Sign(detents);
      for (var index = 0; index < Math.Abs(detents); index++)
      {
        var code = horizontal ? (direction > 0 ? 9 : 8) : (direction > 0 ? 6 : 7);
        EmitPointer(descriptor, code, InputKind.Wheel);
      }
    }
  }

  private void EmitPointer(DeviceDescriptor descriptor, int code, InputKind kind)
  {
    var identity = new InputIdentity(kind, code, false, descriptor.Family, descriptor.Id);
    Emit(new InputActionEvent(
      identity,
      0,
      KeyVariant.Base,
      KeyClassifier.ClassifyPointer(code),
      kind == InputKind.Wheel ? InputPhase.WheelDetent : InputPhase.Up,
      Stopwatch.GetTimestamp(),
      _foreground.CurrentExecutable));
  }

  private void Emit(InputActionEvent action) => _actions.Writer.TryWrite(action);

  private void DispatchActions()
  {
    while (_actions.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
      while (_actions.Reader.TryRead(out var action)) InputAction?.Invoke(this, action);
  }

  private DeviceDescriptor DescribeDevice(nint handle, DeviceFamily expectedFamily)
  {
    if (_devices.TryGetValue(handle, out var descriptor)) return descriptor;
    if (_pendingDeviceLookups.TryAdd(handle, 0))
      ThreadPool.UnsafeQueueUserWorkItem(static state => state.Owner.ResolveDevice(state.Handle, state.ExpectedFamily), (Owner: this, Handle: handle, ExpectedFamily: expectedFamily), false);
    return new DeviceDescriptor(handle, "pending", expectedFamily);
  }

  private void ResolveDevice(nint current, DeviceFamily expectedFamily)
  {
    var descriptor = ResolveDescriptor(current, expectedFamily);
    _devices[current] = descriptor;
    _pendingDeviceLookups.TryRemove(current, out _);
    DeviceChanged?.Invoke(this, ToPublic(descriptor, true));
  }

  private static DeviceDescriptor ResolveDescriptor(nint current, DeviceFamily expectedFamily)
  {
    var capacity = 512u;
    var name = new StringBuilder((int)capacity);
    var result = GetRawInputDeviceInfo(current, RidiDeviceName, name, ref capacity);
    var rawName = result == uint.MaxValue ? $"device-{current:X}" : name.ToString();
    var lower = rawName.ToLowerInvariant();
    var family = expectedFamily == DeviceFamily.Keyboard
      ? DeviceFamily.Keyboard
      : lower.Contains("touchpad") || lower.Contains("precision") || lower.Contains("synaptics") || lower.Contains("i2c")
        ? DeviceFamily.Trackpad
        : lower.Contains("mouse") ? DeviceFamily.ExternalMouse : DeviceFamily.UnknownPointer;
    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawName));
    var info = new RawInputDeviceInfo { Size = (uint)Marshal.SizeOf<RawInputDeviceInfo>() };
    var infoSize = info.Size;
    var hasInfo = GetRawInputDeviceInfoStruct(current, RidiDeviceInfo, ref info, ref infoSize) != uint.MaxValue;
    var buttons = hasInfo ? Math.Clamp((int)info.Mouse.NumberOfButtons, 0, 32) : 0;
    var horizontalWheel = hasInfo && info.Mouse.HasHorizontalWheel != 0;
    return new(current, Convert.ToHexString(digest.AsSpan(0, 8)), family, FriendlyDeviceName(rawName, family), buttons, horizontalWheel);
  }

  private static InputDeviceDescriptor ToPublic(DeviceDescriptor descriptor, bool connected) =>
    new(descriptor.Id, descriptor.Family, connected, descriptor.DisplayName, descriptor.ButtonCount, descriptor.HasHorizontalWheel);

  private static string FriendlyDeviceName(string rawName, DeviceFamily family)
  {
    var upper = rawName.ToUpperInvariant();
    var vid = ExtractHardwareToken(upper, "VID_");
    var pid = ExtractHardwareToken(upper, "PID_");
    var kind = family == DeviceFamily.Trackpad ? "Precision trackpad" : "Mouse";
    return vid is null && pid is null ? kind : $"{kind} · {vid ?? "----"}:{pid ?? "----"}";
  }

  private static string? ExtractHardwareToken(string source, string prefix)
  {
    var index = source.IndexOf(prefix, StringComparison.Ordinal);
    if (index < 0 || index + prefix.Length + 4 > source.Length) return null;
    var value = source.Substring(index + prefix.Length, 4);
    return value.All(char.IsAsciiHexDigit) ? value : null;
  }

  private sealed record DeviceDescriptor(
    nint Handle,
    string Id,
    DeviceFamily Family,
    string DisplayName = "Pointer device",
    int ButtonCount = 0,
    bool HasHorizontalWheel = false);

  [StructLayout(LayoutKind.Sequential)]
  private struct RawInputDevice
  {
    public RawInputDevice(ushort usagePage, ushort usage, uint flags, nint target)
    {
      UsagePage = usagePage;
      Usage = usage;
      Flags = flags;
      Target = target;
    }
    public ushort UsagePage;
    public ushort Usage;
    public uint Flags;
    public nint Target;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct RawInputDeviceListEntry
  {
    public nint Device;
    public uint Type;
  }

  [StructLayout(LayoutKind.Explicit)]
  private struct RawInputDeviceInfo
  {
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public uint Type;
    [FieldOffset(8)] public RawInputMouseInfo Mouse;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct RawInputMouseInfo
  {
    public uint Id;
    public uint NumberOfButtons;
    public uint SampleRate;
    public uint HasHorizontalWheel;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct RawInputHeader
  {
    public uint Type;
    public uint Size;
    public nint Device;
    public nint WParam;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct RawInput
  {
    public RawInputHeader Header;
    public RawInputData Data;
  }

  [StructLayout(LayoutKind.Explicit)]
  private struct RawInputData
  {
    [FieldOffset(0)] public RawMouse Mouse;
    [FieldOffset(0)] public RawKeyboard Keyboard;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct RawKeyboard
  {
    public ushort MakeCode;
    public ushort Flags;
    public ushort Reserved;
    public ushort VirtualKey;
    public uint Message;
    public uint ExtraInformation;
  }

  [StructLayout(LayoutKind.Explicit)]
  private struct RawMouse
  {
    [FieldOffset(0)] public ushort Flags;
    [FieldOffset(4)] public uint Buttons;
    [FieldOffset(4)] public ushort ButtonFlags;
    [FieldOffset(6)] public ushort ButtonData;
    [FieldOffset(8)] public uint RawButtons;
    [FieldOffset(12)] public int LastX;
    [FieldOffset(16)] public int LastY;
    [FieldOffset(20)] public uint ExtraInformation;
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

  [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint number, uint size);
  [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([Out] RawInputDeviceListEntry[]? devices, ref uint count, uint size);
  [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetRawInputDeviceInfo(nint device, uint command, StringBuilder data, ref uint size);
  [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)] private static extern uint GetRawInputDeviceInfoStruct(nint device, uint command, ref RawInputDeviceInfo data, ref uint size);
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx windowClass);
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
  [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
  [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
  [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
  [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
  [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
  [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
  [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
  [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);
  [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
}
