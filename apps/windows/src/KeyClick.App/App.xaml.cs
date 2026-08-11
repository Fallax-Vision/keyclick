using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace KeyClick.App;

public partial class App : Application
{
  private const string InstanceMutexName = @"Local\KeyClick.Instance.v1";
  private const string ActivateEventName = @"Local\KeyClick.Activate.v1";
  private Mutex? _mutex;
  private EventWaitHandle? _activateEvent;
  private CancellationTokenSource? _activateStop;
  private SqliteAppStore? _store;
  private XAudio2SoundEngine? _audio;
  private RawInputService? _rawInput;
  private GlobalShortcutService? _shortcuts;
  private OutcomePipeServer? _outcomePipe;
  private ThemeService? _themes;
  private LocalizationService? _localization;
  private MainViewModel? _viewModel;
  private Forms.NotifyIcon? _tray;
  private Forms.ToolStripItem? _trayOpen;
  private Forms.ToolStripItem? _trayToggleSounds;
  private Forms.ToolStripItem? _trayExit;
  private bool _exiting;
  private int _windowGeneration;
  private volatile bool _windowOpen;

  public AppPaths Paths { get; } = new();
  public bool IsExiting => _exiting;

  protected override async void OnStartup(StartupEventArgs e)
  {
    base.OnStartup(e);
    _mutex = new Mutex(true, InstanceMutexName, out var firstInstance);
    _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
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
      var updates = new UpdateService(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
      _viewModel = new MainViewModel(_store, _audio, _shortcuts, startup, backup, updates, imports, _themes, _localization)
      {
        DataLocation = Paths.Root
      };
      await _viewModel.InitializeAsync();

      _viewModel.ShowHideRequested += (_, _) => ToggleWindow();
      _viewModel.LanguageChanged += (_, _) => UpdateTrayLanguage();
      _rawInput.InputReleased += (_, input) => _viewModel.HandleInputReleased(input);
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

  public void ExitApplication()
  {
    if (_exiting) return;
    _exiting = true;
    Shutdown();
  }

  protected override void OnExit(ExitEventArgs e)
  {
    _activateStop?.Cancel();
    _tray?.Dispose();
    if (_outcomePipe is not null) _outcomePipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
    _rawInput?.Dispose();
    _shortcuts?.Dispose();
    _viewModel?.Dispose();
    _audio?.Dispose();
    if (_store is not null) _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    _themes?.Dispose();
    _activateEvent?.Dispose();
    _activateStop?.Dispose();
    if (_mutex is not null)
    {
      try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
      _mutex.Dispose();
    }
    base.OnExit(e);
  }

  private void CreateTrayIcon()
  {
    var menu = new Forms.ContextMenuStrip();
    _trayOpen = menu.Items.Add(_localization!.Get("TrayOpen"), null, (_, _) => Dispatcher.BeginInvoke(ShowWindow));
    _trayToggleSounds = menu.Items.Add(_localization.Get("TrayToggleSounds"), null, (_, _) => Dispatcher.BeginInvoke(() => _viewModel?.ToggleSoundsCommand.Execute(null)));
    menu.Items.Add(new Forms.ToolStripSeparator());
    _trayExit = menu.Items.Add(_localization.Get("TrayExit"), null, (_, _) => Dispatcher.BeginInvoke(ExitApplication));
    _tray = new Forms.NotifyIcon
    {
      Icon = System.Drawing.SystemIcons.Application,
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
      };
    }
  }

  private void UpdateTrayLanguage()
  {
    if (_localization is null) return;
    if (_trayOpen is not null) _trayOpen.Text = _localization.Get("TrayOpen");
    if (_trayToggleSounds is not null) _trayToggleSounds.Text = _localization.Get("TrayToggleSounds");
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
    _ = Task.Run(() =>
    {
      while (!token.IsCancellationRequested)
      {
        if (_activateEvent?.WaitOne(500) == true) Dispatcher.BeginInvoke(ShowWindow);
      }
    }, token);
  }

  private static string TrimTrayText(string value) => value.Length <= 63 ? value : value[..63];

  [DllImport("kernel32.dll")]
  private static extern bool SetProcessWorkingSetSize(nint process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);
}
