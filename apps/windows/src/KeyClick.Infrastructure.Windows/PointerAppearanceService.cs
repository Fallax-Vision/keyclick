using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using KeyClick.Core;
using Microsoft.Win32;

namespace KeyClick.Infrastructure.Windows;

public sealed class PointerAppearanceService(AppPaths paths)
{
  private const string CursorRegistryPath = @"Control Panel\Cursors";
  private const uint SpiGetMouse = 0x0003;
  private const uint SpiSetMouse = 0x0004;
  private const uint SpiSetCursors = 0x0057;
  private const uint SpiSetMouseTrails = 0x005D;
  private const uint SpiGetMouseTrails = 0x005E;
  private const uint SpiGetMouseSpeed = 0x0070;
  private const uint SpiSetMouseSpeed = 0x0071;
  private const uint SpiGetCursorShadow = 0x101A;
  private const uint SpiSetCursorShadow = 0x101B;
  private const uint PersistAndNotify = 0x0001 | 0x0002;
  private static readonly string[] Roles =
  [
    "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No",
    "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand"
  ];
  private readonly string _cursorRoot = Path.Combine(paths.Media, "cursors");
  private readonly string _recoveryPath = Path.Combine(paths.Data, "pointer-recovery.json");
  private readonly string _experimentalMarker = Path.Combine(paths.Data, "pointer-experimental-active");

  public NativePointerSettings ReadNativeSettings()
  {
    var speed = 10;
    SystemParametersInfoInt(SpiGetMouseSpeed, 0, ref speed, 0);
    var acceleration = new int[3];
    SystemParametersInfoArray(SpiGetMouse, 0, acceleration, 0);
    var trails = 0;
    SystemParametersInfoInt(SpiGetMouseTrails, 0, ref trails, 0);
    var shadow = 1;
    SystemParametersInfoInt(SpiGetCursorShadow, 0, ref shadow, 0);
    return new(Math.Clamp(speed, 1, 20), acceleration[2] != 0, Math.Clamp(trails, 0, 16), shadow != 0);
  }

  public PointerApplyResult ApplyNativeSettings(PointerStudioSettings settings)
  {
    try
    {
      CaptureRecovery(settings);
      var speed = Math.Clamp(settings.WindowsPointerSpeed, 1, 20);
      if (!SystemParametersInfoValue(SpiSetMouseSpeed, 0, (nint)speed, PersistAndNotify)) throw LastError("pointer speed");
      var acceleration = settings.EnhancePointerPrecision ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 };
      if (!SystemParametersInfoArray(SpiSetMouse, 0, acceleration, PersistAndNotify)) throw LastError("pointer acceleration");
      if (!SystemParametersInfoValue(SpiSetMouseTrails, (uint)Math.Clamp(settings.PointerTrails, 0, 16), 0, PersistAndNotify)) throw LastError("pointer trails");
      if (!SystemParametersInfoValue(SpiSetCursorShadow, 0, settings.NativeShadow ? 1 : 0, PersistAndNotify)) throw LastError("cursor shadow");
      return new(true);
    }
    catch (Exception exception) { return new(false, Error: exception.Message); }
  }

  public PointerApplyResult ApplyTheme(PointerThemeDefinition theme, PointerStudioSettings settings)
  {
    try
    {
      CaptureRecovery(settings);
      var directory = CompileTheme(theme, settings.Size, ResolveVariant(settings.Variant));
      using var key = Registry.CurrentUser.CreateSubKey(CursorRegistryPath, true) ?? throw new InvalidOperationException("The Windows cursor settings are unavailable.");
      key.SetValue(string.Empty, $"KeyClick {theme.Id}", RegistryValueKind.String);
      foreach (var role in Roles) key.SetValue(role, Path.Combine(directory, $"{role}.cur"), RegistryValueKind.String);
      key.SetValue("Scheme Source", 1, RegistryValueKind.DWord);
      if (!SystemParametersInfoValue(SpiSetCursors, 0, 0, PersistAndNotify)) throw LastError("cursor scheme");
      var native = ApplyNativeSettings(settings);
      if (!native.Success) throw new InvalidOperationException(native.Error);
      return new(true, Path.Combine(directory, "Arrow.cur"));
    }
    catch (Exception exception)
    {
      RestorePrevious(settings);
      return new(false, Error: exception.Message);
    }
  }

  public PointerApplyResult PrepareTheme(PointerThemeDefinition theme, PointerStudioSettings settings)
  {
    try
    {
      var directory = CompileTheme(theme, settings.Size, ResolveVariant(settings.Variant));
      return new(true, Path.Combine(directory, "Arrow.cur"));
    }
    catch (Exception exception) { return new(false, Error: exception.Message); }
  }

  public PointerApplyResult RestorePrevious(PointerStudioSettings settings)
  {
    try
    {
      if (!settings.RecoverySnapshotCaptured || settings.PreviousCursorScheme.Count == 0) return RestoreWindowsDefaults();
      using var key = Registry.CurrentUser.CreateSubKey(CursorRegistryPath, true) ?? throw new InvalidOperationException("The Windows cursor settings are unavailable.");
      foreach (var role in Roles.Prepend(string.Empty))
      {
        if (settings.PreviousCursorScheme.TryGetValue(role, out var value) && value is not null) key.SetValue(role, value, RegistryValueKind.String);
        else key.DeleteValue(role, false);
      }
      SystemParametersInfoValue(SpiSetCursors, 0, 0, PersistAndNotify);
      var restored = new PointerStudioSettings
      {
        WindowsPointerSpeed = settings.PreviousPointerSpeed,
        EnhancePointerPrecision = settings.PreviousEnhancePointerPrecision,
        PointerTrails = settings.PreviousPointerTrails,
        NativeShadow = settings.PreviousNativeShadow,
        RecoverySnapshotCaptured = true
      };
      ApplyNativeSettingsWithoutCapture(restored);
      ClearExperimentalMarker();
      return new(true);
    }
    catch (Exception exception) { return new(false, Error: exception.Message); }
  }

  public PointerApplyResult RestoreWindowsDefaults()
  {
    try
    {
      using var key = Registry.CurrentUser.CreateSubKey(CursorRegistryPath, true) ?? throw new InvalidOperationException("The Windows cursor settings are unavailable.");
      foreach (var role in Roles.Prepend(string.Empty)) key.DeleteValue(role, false);
      key.DeleteValue("Scheme Source", false);
      if (!SystemParametersInfoValue(SpiSetCursors, 0, 0, PersistAndNotify)) throw LastError("Windows cursors");
      ClearExperimentalMarker();
      return new(true);
    }
    catch (Exception exception) { return new(false, Error: exception.Message); }
  }

  public bool RecoverExperimentalIfNeeded(PointerStudioSettings settings)
  {
    if (!File.Exists(_experimentalMarker)) return false;
    RestorePrevious(settings);
    ClearExperimentalMarker();
    return true;
  }

  public void MarkExperimentalActive()
  {
    Directory.CreateDirectory(paths.Data);
    File.WriteAllText(_experimentalMarker, DateTimeOffset.UtcNow.ToString("O"));
  }

  public void ClearExperimentalMarker()
  {
    try { if (File.Exists(_experimentalMarker)) File.Delete(_experimentalMarker); } catch (IOException) { }
  }

  public bool OwnsSystemTheme(string themeId)
  {
    try
    {
      using var key = Registry.CurrentUser.OpenSubKey(CursorRegistryPath, false);
      return string.Equals(key?.GetValue(string.Empty)?.ToString(), $"KeyClick {themeId}", StringComparison.Ordinal);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
  }

  private void CaptureRecovery(PointerStudioSettings settings)
  {
    if (settings.RecoverySnapshotCaptured) return;
    using var key = Registry.CurrentUser.OpenSubKey(CursorRegistryPath, false);
    settings.PreviousCursorScheme = Roles.Prepend(string.Empty).ToDictionary(role => role, role => key?.GetValue(role)?.ToString(), StringComparer.OrdinalIgnoreCase);
    var native = ReadNativeSettings();
    settings.PreviousPointerSpeed = native.Speed;
    settings.PreviousEnhancePointerPrecision = native.EnhancePointerPrecision;
    settings.PreviousPointerTrails = native.Trails;
    settings.PreviousNativeShadow = native.Shadow;
    settings.RecoverySnapshotCaptured = true;
    Directory.CreateDirectory(paths.Data);
    File.WriteAllText(_recoveryPath, JsonSerializer.Serialize(new RecoverySnapshot(settings.PreviousCursorScheme, native)));
  }

  private void ApplyNativeSettingsWithoutCapture(PointerStudioSettings settings)
  {
    SystemParametersInfoValue(SpiSetMouseSpeed, 0, (nint)Math.Clamp(settings.WindowsPointerSpeed, 1, 20), PersistAndNotify);
    SystemParametersInfoArray(SpiSetMouse, 0, settings.EnhancePointerPrecision ? [6, 10, 1] : [0, 0, 0], PersistAndNotify);
    SystemParametersInfoValue(SpiSetMouseTrails, (uint)Math.Clamp(settings.PointerTrails, 0, 16), 0, PersistAndNotify);
    SystemParametersInfoValue(SpiSetCursorShadow, 0, settings.NativeShadow ? 1 : 0, PersistAndNotify);
  }

  private string CompileTheme(PointerThemeDefinition theme, PointerCursorSize selectedSize, PointerThemeVariant variant)
  {
    var size = selectedSize switch { PointerCursorSize.Small => 24, PointerCursorSize.Standard => 32, PointerCursorSize.Large => 48, _ => 64 };
    var directory = Path.Combine(_cursorRoot, $"v2-{theme.Id}-{variant.ToString().ToLowerInvariant()}-{size}");
    Directory.CreateDirectory(directory);
    foreach (var role in Roles)
    {
      var path = Path.Combine(directory, $"{role}.cur");
      if (!File.Exists(path) || new FileInfo(path).Length < 64) WriteCursor(path, theme, role, size, variant);
    }
    return directory;
  }

  private static void WriteCursor(string path, PointerThemeDefinition theme, string role, int size, PointerThemeVariant variant)
  {
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.Transparent);
    graphics.SmoothingMode = theme.Style == "pixel" ? SmoothingMode.None : SmoothingMode.AntiAlias;
    var primary = ColorTranslator.FromHtml(variant == PointerThemeVariant.Dark ? theme.Outline : theme.Primary);
    var outline = ColorTranslator.FromHtml(variant == PointerThemeVariant.Dark ? theme.Primary : theme.Outline);
    var accent = ColorTranslator.FromHtml(theme.Secondary);
    using var fill = new SolidBrush(primary);
    using var accentBrush = new SolidBrush(accent);
    using var outlinePen = new Pen(outline, Math.Max(1.5f, size / 16f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
    using var accentPen = new Pen(accent, Math.Max(1.5f, size / 14f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
    var scale = size / 32f;
    var hotspot = DrawRole(graphics, role, theme.Style, fill, accentBrush, outlinePen, accentPen, scale);
    using var png = new MemoryStream();
    bitmap.Save(png, ImageFormat.Png);
    var bytes = png.ToArray();
    using var output = File.Create(path);
    using var writer = new BinaryWriter(output);
    writer.Write((ushort)0); writer.Write((ushort)2); writer.Write((ushort)1);
    writer.Write((byte)(size >= 256 ? 0 : size)); writer.Write((byte)(size >= 256 ? 0 : size));
    writer.Write((byte)0); writer.Write((byte)0);
    writer.Write((ushort)Math.Clamp((int)Math.Round(hotspot.X * scale), 0, size - 1));
    writer.Write((ushort)Math.Clamp((int)Math.Round(hotspot.Y * scale), 0, size - 1));
    writer.Write((uint)bytes.Length); writer.Write((uint)22); writer.Write(bytes);
  }

  private static PointF DrawRole(Graphics graphics, string role, string style, Brush fill, Brush accent, Pen outline, Pen accentPen, float s)
  {
    float P(float value) => value * s;
    if (role is "Arrow" or "Help" or "AppStarting")
    {
      var points = new[] { new PointF(P(4), P(2)), new PointF(P(5), P(25)), new PointF(P(11), P(19)), new PointF(P(16), P(29)), new PointF(P(21), P(26)), new PointF(P(16), P(17)), new PointF(P(25), P(16)) };
      if (style is "orbital" or "liquid") graphics.FillEllipse(accent, P(2), P(1), P(10), P(10));
      graphics.FillPolygon(fill, points); graphics.DrawPolygon(outline, points);
      if (style is "folded" or "geometric") graphics.DrawLine(accentPen, P(6), P(5), P(15), P(17));
      if (role == "Help") { graphics.FillEllipse(accent, P(20), P(2), P(10), P(10)); graphics.DrawString("?", new Font("Segoe UI", P(6), FontStyle.Bold), Brushes.White, P(22), P(1)); }
      if (role == "AppStarting") graphics.DrawArc(accentPen, P(19), P(2), P(10), P(10), -70, 270);
      return new(4, 2);
    }
    if (role == "NWPen")
    {
      var points = new[] { new PointF(P(5), P(27)), new PointF(P(9), P(14)), new PointF(P(24), P(2)), new PointF(P(29), P(7)), new PointF(P(18), P(22)) };
      graphics.FillPolygon(fill, points); graphics.DrawPolygon(outline, points); graphics.DrawLine(accentPen, P(9), P(25), P(26), P(5)); graphics.FillEllipse(accent, P(4), P(25), P(5), P(5));
      return new(6, 27);
    }
    if (role == "UpArrow")
    {
      var points = new[] { new PointF(P(16), P(2)), new PointF(P(6), P(13)), new PointF(P(12), P(13)), new PointF(P(12), P(29)), new PointF(P(20), P(29)), new PointF(P(20), P(13)), new PointF(P(26), P(13)) };
      graphics.FillPolygon(fill, points); graphics.DrawPolygon(outline, points); graphics.DrawLine(accentPen, P(16), P(6), P(16), P(25));
      return new(16, 2);
    }
    if (role == "Wait")
    {
      graphics.DrawEllipse(outline, P(5), P(5), P(22), P(22)); graphics.DrawArc(accentPen, P(5), P(5), P(22), P(22), -90, 230); graphics.FillEllipse(accent, P(14), P(2), P(4), P(4));
      return new(16, 16);
    }
    if (role == "IBeam")
    {
      graphics.DrawLine(outline, P(16), P(4), P(16), P(28)); graphics.DrawLine(outline, P(10), P(5), P(22), P(5)); graphics.DrawLine(outline, P(10), P(27), P(22), P(27));
      return new(16, 16);
    }
    if (role == "Crosshair")
    {
      graphics.DrawEllipse(accentPen, P(8), P(8), P(16), P(16)); graphics.DrawLine(outline, P(16), 0, P(16), P(32)); graphics.DrawLine(outline, 0, P(16), P(32), P(16));
      return new(16, 16);
    }
    if (role == "No")
    {
      graphics.FillEllipse(fill, P(4), P(4), P(24), P(24)); graphics.DrawEllipse(outline, P(4), P(4), P(24), P(24)); graphics.DrawLine(accentPen, P(8), P(8), P(24), P(24));
      return new(16, 16);
    }
    if (role == "Hand")
    {
      var points = new[] { new PointF(P(9), P(15)), new PointF(P(9), P(4)), new PointF(P(13), P(4)), new PointF(P(13), P(12)), new PointF(P(16), P(9)), new PointF(P(19), P(11)), new PointF(P(22), P(12)), new PointF(P(24), P(16)), new PointF(P(20), P(27)), new PointF(P(11), P(25)), new PointF(P(5), P(17)) };
      graphics.FillPolygon(fill, points); graphics.DrawPolygon(outline, points); return new(10, 5);
    }
    var horizontal = role == "SizeWE";
    var diagonalDown = role == "SizeNWSE";
    var diagonalUp = role == "SizeNESW";
    if (role == "SizeAll")
    {
      graphics.DrawLine(outline, P(16), P(3), P(16), P(29)); graphics.DrawLine(outline, P(3), P(16), P(29), P(16));
      graphics.FillPolygon(accent, [new PointF(P(16), P(1)), new PointF(P(12), P(7)), new PointF(P(20), P(7))]);
      return new(16, 16);
    }
    var start = horizontal ? new PointF(P(3), P(16)) : diagonalDown ? new(P(5), P(5)) : diagonalUp ? new(P(5), P(27)) : new(P(16), P(3));
    var end = horizontal ? new PointF(P(29), P(16)) : diagonalDown ? new(P(27), P(27)) : diagonalUp ? new(P(27), P(5)) : new(P(16), P(29));
    graphics.DrawLine(outline, start, end); graphics.FillEllipse(accent, start.X - P(3), start.Y - P(3), P(6), P(6)); graphics.FillEllipse(accent, end.X - P(3), end.Y - P(3), P(6), P(6));
    return new(16, 16);
  }

  private static PointerThemeVariant ResolveVariant(PointerThemeVariant variant) => variant == PointerThemeVariant.Automatic
    ? (AppsUseLightTheme() ? PointerThemeVariant.Light : PointerThemeVariant.Dark)
    : variant;

  private static bool AppsUseLightTheme() => (Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") as int? ?? 1) != 0;
  private static Exception LastError(string operation) => new InvalidOperationException($"Windows could not apply {operation} ({Marshal.GetLastWin32Error()}).");

  public sealed record RecoverySnapshot(Dictionary<string, string?> CursorScheme, NativePointerSettings NativeSettings);

  [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
  private static extern bool SystemParametersInfoValue(uint action, uint parameter, nint value, uint flags);
  [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
  private static extern bool SystemParametersInfoInt(uint action, uint parameter, ref int value, uint flags);
  [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
  private static extern bool SystemParametersInfoArray(uint action, uint parameter, [In, Out] int[] value, uint flags);
}
