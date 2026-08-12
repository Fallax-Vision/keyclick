using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using KeyClick.Core;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ThemeMode = KeyClick.Core.ThemeMode;

namespace KeyClick.App;

public sealed class ThemeService : IDisposable
{
  private ThemeMode _mode;

  public ThemeService()
  {
    SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
  }

  public void Apply(ThemeMode mode, Window? window = null)
  {
    _mode = mode;
    var dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && IsSystemDark());
    SetBrush("WindowBackgroundBrush", dark ? "#050505" : "#F3F5F3");
    SetBrush("CardBackgroundBrush", dark ? "#121212" : "#FFFFFF");
    SetBrush("SurfaceBrush", dark ? "#202020" : "#ECEFEC");
    SetBrush("SurfaceHoverBrush", dark ? "#292929" : "#E1E6E1");
    SetBrush("TextBrush", dark ? "#F5F5F5" : "#151815");
    SetBrush("MutedTextBrush", dark ? "#9EA3A8" : "#626962");
    SetBrush("BorderBrush", dark ? "#2D2D2D" : "#D9DED9");
    SetBrush("AccentBrush", "#35E04B");
    SetBrush("AccentTextBrush", "#071409");
    SetBrush("DangerBrush", dark ? "#D95B5B" : "#B52727");
    SetBrush("SelectionBrush", dark ? "#173A1C" : "#D9F6DD");

    if (window is not null)
    {
      window.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#050505" : "#F3F5F3"));
      window.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#F5F5F5" : "#151815"));
    }

    if (window is not null && new WindowInteropHelper(window).Handle is var handle && handle != 0)
    {
      var enabled = dark ? 1 : 0;
      DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
      var cornerPreference = 2;
      DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
    }
  }

  public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

  private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
  {
    if (_mode != ThemeMode.System) return;
    Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System, Application.Current.MainWindow));
  }

  private static bool IsSystemDark()
  {
    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
    return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
  }

  private static void SetBrush(string key, string hex)
  {
    var color = (Color)ColorConverter.ConvertFromString(hex);
    if (Application.Current.Resources[key] is SolidColorBrush { IsFrozen: false } brush)
    {
      brush.Color = color;
      return;
    }
    Application.Current.Resources[key] = new SolidColorBrush(color);
  }

  [DllImport("dwmapi.dll")]
  private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int valueSize);
}
