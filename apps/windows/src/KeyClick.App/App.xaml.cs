using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;
using KeyClick.Updater;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace KeyClick.App;

public partial class App : Application
{
  private Mutex? _mutex;
  private EventWaitHandle? _activateEvent;
  private EventWaitHandle? _shutdownEvent;
  private CancellationTokenSource? _activateStop;
  private SqliteAppStore? _store;
  private XAudio2SoundEngine? _audio;
  private RawInputService? _rawInput;
  private StatisticsService? _statistics;
  private WellnessService? _wellness;
  private GlobalShortcutService? _shortcuts;
  private OutcomePipeServer? _outcomePipe;
  private ThemeService? _themes;
  private LocalizationService? _localization;
  private MainViewModel? _viewModel;
  private Forms.NotifyIcon? _tray;
  private System.Drawing.Icon? _trayIcon;
  private Forms.ToolStripItem? _trayOpen;
  private Forms.ToolStripItem? _trayToggleSounds;
  private Forms.ToolStripMenuItem? _trayKeyboardStatistics;
  private Forms.ToolStripMenuItem? _trayPointerStatistics;
  private Forms.ToolStripItem? _trayExit;
  private bool _exiting;
  private int _windowGeneration;
  private volatile bool _windowOpen;

  public AppPaths Paths { get; private set; } = new();
  public bool IsExiting => _exiting;

  protected override async void OnStartup(StartupEventArgs e)
  {
    base.OnStartup(e);
    Paths = ResolvePaths(e.Args);
    var instanceId = GetInstanceId(Paths.Root);
    _mutex = new Mutex(true, $@"Local\KeyClick.Instance.{instanceId}", out var firstInstance);
    _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\KeyClick.Activate.{instanceId}");
    _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\KeyClick.Shutdown.{instanceId}");
    if (!firstInstance)
    {
      _activateEvent.Set();
      Shutdown();
      return;
    }

    _localization = new LocalizationService();
    _localization.Apply(DisplayLanguageMode.System);

    try
    {
      Paths.EnsureCreated();
      _store = new SqliteAppStore(Paths);
      await _store.InitializeAsync();
      var initialSettings = await _store.LoadSettingsAsync();
      _localization.Apply(initialSettings.DisplayLanguage);
      _audio = new XAudio2SoundEngine();
      await _audio.InitializeAsync(initialSettings.OutputDeviceId);
      _rawInput = new RawInputService();
      _shortcuts = new GlobalShortcutService();
      _themes = new ThemeService();
      _themes.Apply(initialSettings.Theme);
      var startup = new StartupService(Paths);
      var backup = new BackupService(Paths);
      var imports = new AudioImportService(Paths);
      var packImports = new SoundPackImportService(Paths, imports);
      var updates = new UpdateService();
      _viewModel = new MainViewModel(_store, _audio, _shortcuts, startup, backup, updates, imports, packImports, _themes, _localization)
      {
        DataLocation = Paths.Root
      };
      await _viewModel.InitializeAsync();
      _viewModel.SetDistributionMode(Paths.Mode);
      if (Paths.Mode == DistributionMode.Installed)
      {
        var localArtifacts = Environment.GetEnvironmentVariable("KEYCLICK_LOCAL_UPDATE_DIRECTORY")
          ?? @"C:\wamp64\www\fallax_projects\lab\keyclick\artifacts";
        _ = _viewModel.DiscoverLocalUpdateAsync(localArtifacts);
      }
      _viewModel.AttachProfiles(new ProfileTransferService(Paths, _store, _store));

      if (!_viewModel.StatisticsDisclosureConfirmed)
      {
        var disclosure = new PrivacyDisclosureWindow();
        _themes.Apply(_viewModel.Theme, disclosure);
        if (disclosure.ShowDialog() == true)
          await _viewModel.ConfirmStatisticsDisclosureAsync(disclosure.KeyboardStatisticsEnabled, disclosure.PointerStatisticsEnabled);
      }
      _statistics = new StatisticsService(_store, _viewModel.Settings);
      _viewModel.AttachStatistics(_statistics);
      _wellness = new WellnessService(_store, _viewModel.Settings);
      _viewModel.AttachWellness(_wellness);
      await _wellness.InitializeAsync();
      _viewModel.StatisticsPolicyChanged += (_, _) => _statistics?.UpdatePolicy(_viewModel.Settings);
      _viewModel.StatisticsPolicyChanged += (_, _) => _wellness?.UpdatePolicy(_viewModel.Settings);
      _wellness.NotificationRequested += (_, kind) => Dispatcher.BeginInvoke(() => ShowWellnessNotification(kind));

      _viewModel.ShowHideRequested += (_, _) => ToggleWindow();
      _viewModel.LanguageChanged += (_, _) => UpdateTrayLanguage();
      _rawInput.InputAction += (_, input) =>
      {
        _statistics.TryRecord(input);
        _wellness.TryRecord(input);
        _viewModel.HandleInputAction(input);
      };
      _rawInput.DeviceChanged += (_, device) => _viewModel.HandleDeviceChanged(device);
      _shortcuts.CommandInvoked += (_, command) => _viewModel.HandleShortcut(command);

      _outcomePipe = new OutcomePipeServer(
        () => _viewModel.IntegrationApiEnabled,
        path => _viewModel.AllowedIntegrationClients.Any(client => string.Equals(client, path, StringComparison.OrdinalIgnoreCase)),
        request => _viewModel.PlayOutcome(request));
      _viewModel.IntegrationPipeName = _outcomePipe.PipeName;
      _outcomePipe.Start();
      _rawInput.Start();
      CreateTrayIcon();
      StartActivationListener();

      var startupLaunch = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
      if (!startupLaunch || !_viewModel.StartMinimized) ShowWindow();
    }
    catch (Exception exception)
    {
      WriteStartupDiagnostic(exception);
      MessageBox.Show(_localization.Format("StartupFailedFormat", exception.Message), "KeyClick", MessageBoxButton.OK, MessageBoxImage.Error);
      ExitApplication();
    }
  }

  public void ShowWindow()
  {
    if (_viewModel is null || _themes is null) return;
    var window = MainWindow as MainWindow ?? CreateMainWindow();
    window.Show();
    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
    window.Activate();
    window.Topmost = true;
    window.Topmost = false;
    window.Focus();
  }

  public void HideWindow()
  {
    if (MainWindow is not MainWindow window) return;
    _viewModel?.SetAppFocused(false);
    window.AllowClose = true;
    MainWindow = null;
    _windowOpen = false;
    window.Close();
    var generation = Interlocked.Increment(ref _windowGeneration);
    _ = Task.Run(async () =>
    {
      await Task.Delay(2000);
      if (_exiting || generation != Volatile.Read(ref _windowGeneration) || _windowOpen) return;
      GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: false);
      GC.WaitForPendingFinalizers();
      using var process = Process.GetCurrentProcess();
      SetProcessWorkingSetSize(process.Handle, new nint(-1), new nint(-1));
    });
  }

  public void ToggleWindow()
  {
    if (MainWindow?.IsVisible == true && MainWindow.IsActive) HideWindow();
    else ShowWindow();
  }

  public async void ExitApplication()
  {
    if (_exiting) return;
    _exiting = true;
    if (_tray is not null) _tray.Visible = false;
    MainWindow?.Hide();
    _activateStop?.Cancel();

    var captureShutdown = StopCaptureAndPlaybackAsync();
    var outcomePipe = _outcomePipe;
    _outcomePipe = null;
    var outcomeShutdown = outcomePipe?.DisposeAsync().AsTask();
    _shortcuts?.Dispose();
    _shortcuts = null;
    _viewModel?.Dispose();
    _viewModel = null;

    var store = _store;
    _store = null;
    try
    {
      await captureShutdown;
      if (outcomeShutdown is not null) await outcomeShutdown;
      if (store is not null) await store.DisposeAsync();
    }
    catch { }
    finally { Shutdown(); }
  }

  protected override void OnExit(ExitEventArgs e)
  {
    _activateStop?.Cancel();
    _tray?.Dispose();
    _trayIcon?.Dispose();
    if (_outcomePipe is not null) _ = _outcomePipe.DisposeAsync().AsTask();
    _outcomePipe = null;
    if (_rawInput is not null || _statistics is not null || _wellness is not null || _audio is not null)
      _ = StopCaptureAndPlaybackAsync();
    _shortcuts?.Dispose();
    _viewModel?.Dispose();
    if (_store is not null) _ = _store.DisposeAsync().AsTask();
    _store = null;
    _themes?.Dispose();
    _activateEvent?.Dispose();
    _shutdownEvent?.Dispose();
    _activateStop?.Dispose();
    if (_mutex is not null)
    {
      try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
      _mutex.Dispose();
    }
    base.OnExit(e);
  }

  private async Task StopCaptureAndPlaybackAsync()
  {
    _rawInput?.Dispose();
    _rawInput = null;
    _audio?.Dispose();
    _audio = null;

    var statistics = _statistics;
    _statistics = null;
    if (statistics is not null) await statistics.DisposeAsync();

    var wellness = _wellness;
    _wellness = null;
    if (wellness is not null) await wellness.DisposeAsync();
  }

  private void WriteStartupDiagnostic(Exception exception)
  {
    try
    {
      Directory.CreateDirectory(Paths.Logs);
      File.WriteAllText(Path.Combine(Paths.Logs, "startup-error.log"), exception.ToString());
    }
    catch
    {
      // Startup diagnostics must never hide the original failure.
    }
  }

  private void CreateTrayIcon()
  {
    var menu = new Forms.ContextMenuStrip();
    _trayOpen = menu.Items.Add(_localization!.Get("TrayOpen"), null, (_, _) => Dispatcher.BeginInvoke(ShowWindow));
    _trayToggleSounds = menu.Items.Add(_localization.Get("TrayToggleSounds"), null, (_, _) => Dispatcher.BeginInvoke(() => _viewModel?.ToggleSoundsCommand.Execute(null)));
    _trayKeyboardStatistics = new Forms.ToolStripMenuItem(_localization.Get("TrayKeyboardStatistics")) { CheckOnClick = true, Checked = _viewModel?.KeyboardStatisticsEnabled == true };
    _trayKeyboardStatistics.Click += (_, _) => Dispatcher.BeginInvoke(() => { if (_viewModel is not null) _viewModel.KeyboardStatisticsEnabled = _trayKeyboardStatistics.Checked; });
    menu.Items.Add(_trayKeyboardStatistics);
    _trayPointerStatistics = new Forms.ToolStripMenuItem(_localization.Get("TrayPointerStatistics")) { CheckOnClick = true, Checked = _viewModel?.PointerStatisticsEnabled == true };
    _trayPointerStatistics.Click += (_, _) => Dispatcher.BeginInvoke(() => { if (_viewModel is not null) _viewModel.PointerStatisticsEnabled = _trayPointerStatistics.Checked; });
    menu.Items.Add(_trayPointerStatistics);
    menu.Items.Add(new Forms.ToolStripSeparator());
    _trayExit = menu.Items.Add(_localization.Get("TrayExit"), null, (_, _) => Dispatcher.BeginInvoke(ExitApplication));
    _trayIcon = LoadApplicationIcon();
    _tray = new Forms.NotifyIcon
    {
      Icon = _trayIcon,
      Text = TrimTrayText(_viewModel?.AppTitle ?? "KeyClick"),
      Visible = true,
      ContextMenuStrip = menu
    };
    _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowWindow);
    if (_viewModel is not null)
    {
      _viewModel.PropertyChanged += (_, args) =>
      {
        if (args.PropertyName == nameof(MainViewModel.AppTitle) && _tray is not null)
          _tray.Text = TrimTrayText(_viewModel.AppTitle);
        if (args.PropertyName == nameof(MainViewModel.KeyboardStatisticsEnabled) && _trayKeyboardStatistics is not null)
          _trayKeyboardStatistics.Checked = _viewModel.KeyboardStatisticsEnabled;
        if (args.PropertyName == nameof(MainViewModel.PointerStatisticsEnabled) && _trayPointerStatistics is not null)
          _trayPointerStatistics.Checked = _viewModel.PointerStatisticsEnabled;
      };
    }
  }

  private void UpdateTrayLanguage()
  {
    if (_localization is null) return;
    if (_trayOpen is not null) _trayOpen.Text = _localization.Get("TrayOpen");
    if (_trayToggleSounds is not null) _trayToggleSounds.Text = _localization.Get("TrayToggleSounds");
    if (_trayKeyboardStatistics is not null) _trayKeyboardStatistics.Text = _localization.Get("TrayKeyboardStatistics");
    if (_trayPointerStatistics is not null) _trayPointerStatistics.Text = _localization.Get("TrayPointerStatistics");
    if (_trayExit is not null) _trayExit.Text = _localization.Get("TrayExit");
  }

  private MainWindow CreateMainWindow()
  {
    Interlocked.Increment(ref _windowGeneration);
    var window = new MainWindow(_viewModel!);
    _windowOpen = true;
    MainWindow = window;
    _themes!.Apply(_viewModel!.Theme, window);
    window.SourceInitialized += (_, _) => _themes.Apply(_viewModel.Theme, window);
    window.Activated += (_, _) => _viewModel.SetAppFocused(true);
    window.Deactivated += (_, _) => _viewModel.SetAppFocused(false);
    return window;
  }

  private void StartActivationListener()
  {
    _activateStop = new CancellationTokenSource();
    var token = _activateStop.Token;
    var activateEvent = _activateEvent;
    var shutdownEvent = _shutdownEvent;
    if (activateEvent is null || shutdownEvent is null) return;
    _ = Task.Run(() =>
    {
      try
      {
        var handles = new WaitHandle[] { activateEvent, shutdownEvent, token.WaitHandle };
        while (true)
        {
          switch (WaitHandle.WaitAny(handles))
          {
            case 0:
              Dispatcher.BeginInvoke(ShowWindow);
              break;
            case 1:
              Dispatcher.BeginInvoke(ExitApplication);
              return;
            default:
              return;
          }
        }
      }
      catch (ObjectDisposedException) { }
    }, token);
  }

  private static string GetInstanceId(string root) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)).AsSpan(0, 8));

  private static string TrimTrayText(string value) => value.Length <= 63 ? value : value[..63];

  private static AppPaths ResolvePaths(string[] args)
  {
    var rootIndex = Array.FindIndex(args, value => value.Equals("--data-root", StringComparison.OrdinalIgnoreCase));
    var launcherIndex = Array.FindIndex(args, value => value.Equals("--launcher", StringComparison.OrdinalIgnoreCase));
    var portable = args.Contains("--distribution-portable", StringComparer.OrdinalIgnoreCase);
    var root = rootIndex >= 0 && rootIndex + 1 < args.Length ? Path.GetFullPath(args[rootIndex + 1]) : null;
    var launcher = launcherIndex >= 0 && launcherIndex + 1 < args.Length ? Path.GetFullPath(args[launcherIndex + 1]) : null;
    return new AppPaths(root, portable ? DistributionMode.Portable : DistributionMode.Installed, launcher);
  }

  private static System.Drawing.Icon LoadApplicationIcon()
  {
    if (Environment.ProcessPath is string path && System.Drawing.Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
    return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
  }

  private void ShowWellnessNotification(string kind)
  {
    if (_tray is null) return;
    _tray.BalloonTipTitle = _localization!.Get(kind == "break" ? "BreakReminderTitle" : "GoalReachedTitle");
    _tray.BalloonTipText = _localization.Get(kind switch
    {
      "break" => "BreakReminderMessage",
      "goal:keyboard" => "KeyboardGoalReached",
      "goal:pointer" => "PointerGoalReached",
      _ => "ActiveGoalReached"
    });
    _tray.ShowBalloonTip(6000);
  }

  [DllImport("kernel32.dll")]
  private static extern bool SetProcessWorkingSetSize(nint process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);
}
