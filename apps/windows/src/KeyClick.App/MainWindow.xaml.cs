using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KeyClick.Infrastructure.Windows;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace KeyClick.App;

public partial class MainWindow : Window
{
  private readonly MainViewModel _viewModel;
  private readonly FunStatsShareService _funStatsShare = new();
  private bool _spaceEnteredOnKeyDown;
  private bool _uiActionRunning;
  private static LocalizationService L => LocalizationService.Current;
  internal bool AllowClose { get; set; }

  public MainWindow(MainViewModel viewModel)
  {
    _viewModel = viewModel;
    DataContext = viewModel;
    InitializeComponent();
    System.Windows.DataObject.AddPastingHandler(ChallengeInput, ChallengeInput_Pasting);
    if (_viewModel.TypingChallenges is not null) _viewModel.TypingChallenges.SessionDisplayChanged += (_, _) => Dispatcher.BeginInvoke(RenderChallengeText);
  }

  private async void Navigation_Checked(object sender, RoutedEventArgs e)
  {
    if (sender is System.Windows.Controls.RadioButton { Tag: string tag } && int.TryParse(tag, out var page) && PageTabs is not null)
    {
      PageTabs.SelectedIndex = page;
      if (page == 4 && _viewModel.PointerStudio is { } studio)
        await RunUiActionAsync(sender, studio.OnPageOpenedAsync);
    }
  }

  private async void ImportSound_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog { Title = L.Get("DialogImportSound"), Filter = L.Get("FilterSoundFiles") };
    if (dialog.ShowDialog(this) != true) return;
    await RunUiActionAsync(sender, async () =>
    {
      try { await _viewModel.ImportMappingSoundAsync(dialog.FileName); }
      catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("DialogImportFailed"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    });
  }

  private async void ImportSoundPack_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog { Title = L.Get("DialogImportSoundPack"), Filter = L.Get("FilterSoundPacks") };
    if (dialog.ShowDialog(this) != true) return;
    await RunUiActionAsync(sender, async () =>
    {
      try { await _viewModel.ImportSoundPackAsync(dialog.FileName); }
      catch (SoundPackImportException exception)
      {
        MessageBox.Show(this, L.Format(exception.ResourceKey, exception.Arguments), L.Get("DialogSoundPackImportFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
      }
      catch (Exception)
      {
        MessageBox.Show(this, L.Get("SoundPackArchiveInvalid"), L.Get("DialogSoundPackImportFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
      }
    });
  }

  private async void ChallengeStart_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.TypingChallenges is not { } challenges) return;
    if (!challenges.DisclosureConfirmed)
    {
      var disclosure = new ChallengePrivacyWindow { Owner = this };
      disclosure.SourceInitialized += (_, _) => _viewModel.ApplyTheme(disclosure);
      if (disclosure.ShowDialog() != true) return;
      await challenges.ConfirmDisclosureAsync();
    }
    if (challenges.SourceIndex == 1 && challenges.SaveCustomPrompt
      && MessageBox.Show(this, L.Get("ChallengeSavePromptQuestion"), L.Get("ChallengeSavePrompt"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    await RunUiActionAsync(sender, async () =>
    {
      try
      {
        await challenges.StartAsync();
        RenderChallengeText();
        ChallengeInput.Focus();
      }
      catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("NavTypingChallenge"), MessageBoxButton.OK, MessageBoxImage.Information); }
    });
  }

  private void ChallengeInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
  {
    if (_viewModel.TypingChallenges is not { IsSessionActive: true } challenges) return;
    if (_spaceEnteredOnKeyDown && e.Text == " ")
    {
      _spaceEnteredOnKeyDown = false;
      e.Handled = true;
      return;
    }
    _spaceEnteredOnKeyDown = false;
    challenges.Input(e.Text);
    e.Handled = true;
    UpdateChallengeInput(challenges);
  }

  private void ChallengeInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
  {
    if (_viewModel.TypingChallenges is not { IsSessionActive: true } challenges) return;
    if (e.Key == Key.Back)
    {
      challenges.Backspace();
      e.Handled = true;
      UpdateChallengeInput(challenges);
    }
    else if (e.Key == Key.Space)
    {
      challenges.Input(" ");
      _spaceEnteredOnKeyDown = true;
      e.Handled = true;
      UpdateChallengeInput(challenges);
    }
    else if (e.Key == Key.Enter)
    {
      challenges.Input("\n");
      e.Handled = true;
      UpdateChallengeInput(challenges);
    }
  }

  private void ChallengeInput_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
  {
    if (e.Key == Key.Space) _spaceEnteredOnKeyDown = false;
  }

  private void ChallengeInput_Pasting(object sender, DataObjectPastingEventArgs e)
  {
    e.CancelCommand();
    MessageBox.Show(this, L.Get("ChallengePasteBlocked"), L.Get("NavTypingChallenge"), MessageBoxButton.OK, MessageBoxImage.Information);
  }

  private async void ChallengeFinish_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.TypingChallenges is not { } challenges) return;
    await RunUiActionAsync(sender, async () =>
    {
      await challenges.FinishAsync();
      RenderChallengeText();
    });
  }

  private void ChallengeCancel_Click(object sender, RoutedEventArgs e) => _viewModel.TypingChallenges?.Cancel();
  private void ChallengeResume_Click(object sender, RoutedEventArgs e) { _viewModel.TypingChallenges?.Resume(); ChallengeInput.Focus(); }
  private void ChallengeShowSetup_Click(object sender, RoutedEventArgs e) => _viewModel.TypingChallenges?.ShowSetup();
  private async void ChallengeShowHistory_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.TypingChallenges is { } value) await RunUiActionAsync(sender, () => value.ShowHistoryAsync());
  }
  private void ChallengeRandom_Click(object sender, RoutedEventArgs e) => _viewModel.TypingChallenges?.SelectRandomPassage();
  private void ChallengeFavorite_Click(object sender, RoutedEventArgs e) => _viewModel.TypingChallenges?.ToggleFavorite();
  private void ChallengeCompareSelected_Click(object sender, RoutedEventArgs e) => _viewModel.TypingChallenges?.UseSelectedForComparison();

  private async void ChallengeExport_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.TypingChallenges is not { } challenges) return;
    var dialog = new SaveFileDialog { Title = L.Get("ExportCsv"), Filter = L.Get("FilterCsv"), FileName = $"KeyClick-challenges-{DateTime.Now:yyyy-MM-dd}.csv" };
    if (dialog.ShowDialog(this) == true) await RunUiActionAsync(sender, () => challenges.ExportCsvAsync(dialog.FileName));
  }

  private async void ChallengeDeleteSelected_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.TypingChallenges is not { CanDeleteSelected: true } challenges) return;
    if (MessageBox.Show(this, L.Get("ChallengeDeleteSelectedQuestion"), L.Get("ChallengeDeleteSelected"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    await RunUiActionAsync(sender, async () =>
    {
      await _viewModel.CreateBackupNowAsync();
      await challenges.DeleteSelectedAsync();
    });
  }

  private async void ChallengeDeletePeriod_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.TypingChallenges is not { } challenges) return;
    if (MessageBox.Show(this, L.Get("ChallengeDeletePeriodQuestion"), L.Get("ChallengeDeletePeriod"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    await RunUiActionAsync(sender, async () =>
    {
      await _viewModel.CreateBackupNowAsync();
      await challenges.DeleteVisiblePeriodAsync();
    });
  }

  private async void ChallengeDeletePrompt_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.TypingChallenges?.SelectedSavedPrompt is null) return;
    if (MessageBox.Show(this, L.Get("ChallengeDeletePromptQuestion"), L.Get("RemoveSelected"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    await RunUiActionAsync(sender, () => _viewModel.TypingChallenges.DeleteSelectedPromptAsync());
  }

  private void UpdateChallengeInput(TypingChallengeViewModel challenges)
  {
    ChallengeInput.Text = challenges.ResponseText;
    ChallengeInput.CaretIndex = ChallengeInput.Text.Length;
    RenderChallengeText();
  }

  private void RenderChallengeText()
  {
    if (ChallengeTargetText is null || _viewModel.TypingChallenges is not { } challenges) return;
    ChallengeTargetText.Inlines.Clear();
    var target = TextElements(challenges.TargetText);
    var response = TextElements(challenges.ResponseText);
    var normal = (System.Windows.Media.Brush)FindResource("TextBrush");
    var muted = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
    var danger = (System.Windows.Media.Brush)FindResource("DangerBrush");
    var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
    for (var index = 0; index < target.Count; index++)
    {
      var run = new Run(target[index]) { Foreground = index < response.Count ? (target[index] == response[index] ? muted : danger) : normal };
      if (index == response.Count) run.TextDecorations = TextDecorations.Underline;
      if (index == response.Count) run.Foreground = accent;
      ChallengeTargetText.Inlines.Add(run);
    }
  }

  private static IReadOnlyList<string> TextElements(string value)
  {
    var values = new List<string>();
    var enumerator = StringInfo.GetTextElementEnumerator(value);
    while (enumerator.MoveNext()) values.Add(enumerator.GetTextElement());
    return values;
  }

  private void SoundPackList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
  {
    e.Handled = true;
    SoundPacksScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
    {
      RoutedEvent = UIElement.MouseWheelEvent,
      Source = SoundPacksScrollViewer
    });
  }

  private void PointerThemeGallery_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
  {
    e.Handled = true;
    PointerStudioScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
    {
      RoutedEvent = UIElement.MouseWheelEvent,
      Source = PointerStudioScrollViewer
    });
  }

  private async void EditShortcut_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.SelectedShortcut is not { } selected) return;
    var editor = new ShortcutEditorWindow(selected) { Owner = this };
    if (editor.ShowDialog() != true || editor.Result is null) return;
    await RunUiActionAsync(sender, async () =>
    {
      try { await _viewModel.SaveShortcutAsync(editor.Result); }
      catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("DialogShortcutUnavailable"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    });
  }

  private async void RestoreShortcuts_Click(object sender, RoutedEventArgs e)
  {
    if (MessageBox.Show(this, L.Get("RestoreShortcutsQuestion"), L.Get("RestoreShortcutsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
    await RunUiActionAsync(sender, async () =>
    {
      try
      {
        foreach (var binding in KeyClick.Core.BuiltInCatalog.DefaultShortcuts) await _viewModel.SaveShortcutAsync(binding);
      }
      catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("RestoreShortcutsFailed"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    });
  }

  private void AddExclusion_Click(object sender, RoutedEventArgs e)
  {
    _viewModel.AddExcludedExecutable(ExclusionPath.Text);
    ExclusionPath.Clear();
  }

  private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
  {
    if (ExclusionList.SelectedItem is string value) _viewModel.RemoveExcludedExecutable(value);
  }

  private void AddStatisticsExclusion_Click(object sender, RoutedEventArgs e)
  {
    _viewModel.AddStatisticsExcludedExecutable(StatisticsExclusionPath.Text);
    StatisticsExclusionPath.Clear();
  }

  private void RemoveStatisticsExclusion_Click(object sender, RoutedEventArgs e)
  {
    if (StatisticsExclusionList.SelectedItem is string value) _viewModel.RemoveStatisticsExcludedExecutable(value);
  }

  private void PageTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    if (!ReferenceEquals(e.Source, PageTabs)) return;
    var statisticsVisible = PageTabs.SelectedIndex == 1;
    var homeVisible = PageTabs.SelectedIndex == 0;
    _viewModel.Statistics?.SetVisible(homeVisible || statisticsVisible);
    _viewModel.Statistics?.SetHomeVisible(homeVisible);
    _viewModel.Statistics?.SetHeatmapVisible(homeVisible || statisticsVisible && StatisticsSectionTabs?.SelectedIndex == 2);
    _viewModel.Statistics?.SetApplicationsVisible(statisticsVisible && StatisticsSectionTabs?.SelectedIndex == 3);
  }

  private void StatisticsSectionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    if (!ReferenceEquals(e.Source, StatisticsSectionTabs)) return;
    _viewModel.Statistics?.SetHeatmapVisible(PageTabs?.SelectedIndex == 1 && StatisticsSectionTabs.SelectedIndex == 2);
    _viewModel.Statistics?.SetApplicationsVisible(PageTabs?.SelectedIndex == 1 && StatisticsSectionTabs.SelectedIndex == 3);
  }

  private void MetricCard_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.Statistics is not { } statistics || sender is not FrameworkElement { Tag: string cardId }) return;
    var details = statistics.CardDetails(cardId);
    var dialog = new FunStatDetailWindow(details.Title, details.Value, details.Facts) { Owner = this };
    dialog.SourceInitialized += (_, _) => _viewModel.ApplyTheme(dialog);
    dialog.ShowDialog();
    statistics.CardWasClicked(cardId);
  }

  private void ManageFunStats_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.Statistics is not { } statistics) return;
    var dialog = new FunStatsManageWindow(statistics) { Owner = this };
    dialog.SourceInitialized += (_, _) => _viewModel.ApplyTheme(dialog);
    dialog.ShowDialog();
  }

  private void CustomizeChart_Click(object sender, RoutedEventArgs e) => ManageFunStats_Click(sender, e);

  private void ShuffleFunStats_Click(object sender, RoutedEventArgs e) => _viewModel.Statistics?.ShuffleFunFacts();

  private async void CopyFunStats_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.Statistics is not { } statistics || sender is not FrameworkElement { Tag: string location }) return;
    var home = location == "home";
    var tiles = home ? statistics.HomeFunStatsTiles : statistics.FunStatsTiles;
    await RunUiActionAsync(sender, async () =>
    {
      await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
      try
      {
        var background = FindResource("WindowBackgroundBrush") as SolidColorBrush;
        var color = background?.Color ?? Colors.Black;
        var dark = color.R + color.G + color.B < 384;
        _funStatsShare.Copy(tiles, home ? statistics.HomeFunStatsPeriodLabel : statistics.CurrentPeriodLabel,
          statistics.BuildShareCaption(home), statistics.FunStatsCopyMode, this, dark);
        _viewModel.ReportStatus(L.Get("FunCopied"));
      }
      catch (Exception exception)
      {
        MessageBox.Show(this, L.Format("FunCopyFailedFormat", exception.Message), L.Get("Copy"), MessageBoxButton.OK, MessageBoxImage.Warning);
      }
    });
  }

  private async void ExportStatistics_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.Statistics?.Snapshot is null) return;
    var dialog = new SaveFileDialog { Title = L.Get("ExportCsv"), Filter = L.Get("FilterCsv"), FileName = $"KeyClick-statistics-{DateTime.Now:yyyy-MM-dd}.csv" };
    if (dialog.ShowDialog(this) == true) await RunUiActionAsync(sender, () => _viewModel.Statistics.ExportAsync(dialog.FileName));
  }

  private async void DeleteStatistics_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.Statistics is null) return;
    var dialog = new DeleteStatisticsWindow { Owner = this };
    if (dialog.ShowDialog() != true) return;
    if (MessageBox.Show(this, L.Get("DeleteStatisticsQuestion"), L.Get("DeleteStatistics"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    await RunUiActionAsync(sender, async () =>
    {
      if (dialog.Request.CreateSafetyBackup) await _viewModel.CreateBackupNowAsync();
      await _viewModel.Statistics.DeleteAsync(dialog.Request);
      if ((dialog.Request.DeleteTypingChallengeResults || dialog.Request.DeleteTypingChallengeAchievements) && _viewModel.TypingChallenges is { } challenges)
        await challenges.DeleteFromStatisticsDialogAsync(dialog.Request);
      await _viewModel.Statistics.RefreshAsync();
    });
  }

  private async void ExportProfile_Click(object sender, RoutedEventArgs e)
  {
    var options = new ProfileExportWindow { Owner = this };
    if (options.ShowDialog() != true) return;
    var dialog = new SaveFileDialog { Title = L.Get("ExportProfile"), Filter = L.Get("FilterProfile"), FileName = $"KeyClick-{DateTime.Now:yyyy-MM-dd}.keyclickprofile" };
    if (dialog.ShowDialog(this) != true) return;
    await RunUiActionAsync(sender, async () =>
    {
      try
      {
        await _viewModel.ExportProfileAsync(dialog.FileName, options.Options);
        MessageBox.Show(this, L.Get("ProfileExported"), L.Get("ExportProfile"), MessageBoxButton.OK, MessageBoxImage.Information);
      }
      catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("ExportProfile"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    });
  }

  private async void ImportProfile_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog { Title = L.Get("ImportProfile"), Filter = L.Get("FilterProfile") };
    if (dialog.ShowDialog(this) != true) return;
    await RunUiActionAsync(sender, async () =>
    {
      string? password = null;
      try
      {
        if (await _viewModel.ProfileRequiresPasswordAsync(dialog.FileName))
        {
          var passwordDialog = new ProfilePasswordWindow { Owner = this };
          if (passwordDialog.ShowDialog() != true) return;
          password = passwordDialog.Password;
        }
        var preview = await _viewModel.PreviewProfileAsync(dialog.FileName, password);
        var merge = new ProfileImportWindow(preview) { Owner = this };
        if (merge.ShowDialog() != true) return;
        await _viewModel.ImportProfileAsync(dialog.FileName, password, merge.UseImportedMedia);
        MessageBox.Show(this, L.Get("ProfileImported"), L.Get("ImportProfile"), MessageBoxButton.OK, MessageBoxImage.Information);
      }
      catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("ImportProfile"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    });
  }

  private void UseInstalledData_Click(object sender, RoutedEventArgs e)
  {
    if (!_viewModel.IsPortable) return;
    if (MessageBox.Show(this, L.Get("UseInstalledDataQuestion"), L.Get("UseInstalledData"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
    var launcher = ((App)Application.Current).Paths.Launcher;
    Process.Start(new ProcessStartInfo(launcher, "--use-installed-data") { UseShellExecute = true });
    ((App)Application.Current).ExitApplication();
  }

  private void AddIntegrationClient_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog { Title = L.Get("AllowIntegrationClient"), Filter = L.Get("FilterApplications") };
    if (dialog.ShowDialog(this) == true) _viewModel.AddIntegrationClient(dialog.FileName);
  }

  private void RemoveIntegrationClient_Click(object sender, RoutedEventArgs e)
  {
    if (IntegrationClientList.SelectedItem is string value) _viewModel.RemoveIntegrationClient(value);
  }

  private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
  {
    Process.Start(new ProcessStartInfo("explorer.exe", _viewModel.DataLocation) { UseShellExecute = true });
  }

  private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog { Title = L.Get("RestoreBackupDialog"), Filter = L.Get("FilterBackup") };
    if (dialog.ShowDialog(this) != true) return;
    if (MessageBox.Show(this, L.Get("RestoreBackupQuestion"), L.Get("RestoreBackupTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    await RunUiActionAsync(sender, async () =>
    {
      try
      {
        await _viewModel.PrepareRestoreAsync(dialog.FileName);
        var launcher = ((App)Application.Current).Paths.Launcher;
        if (!File.Exists(launcher)) throw new InvalidOperationException(L.Get("RestoreLauncherRequired"));
        var start = new ProcessStartInfo(launcher) { UseShellExecute = true };
        start.ArgumentList.Add("--restore-backup");
        start.ArgumentList.Add(dialog.FileName);
        Process.Start(start);
        ((App)Application.Current).ExitApplication();
      }
      catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("RestoreFailed"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    });
  }

  private async void ResetSettings_Click(object sender, RoutedEventArgs e)
  {
    if (MessageBox.Show(this, L.Get("ResetQuestion"), L.Get("ResetSettings"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    await RunUiActionAsync(sender, async () =>
    {
      try { await _viewModel.ResetSettingsAsync(); }
      catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("ResetFailed"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    });
  }

  private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
  {
    await RunUiActionAsync(sender, async () =>
    {
      try
      {
        var update = await _viewModel.CheckForUpdateAsync();
        if (update is null)
        {
          MessageBox.Show(this, L.Get("UpToDate"), L.Get("UpdatesTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
          return;
        }
        if (_viewModel.IsPortable)
        {
          var confirmation = MessageBox.Show(this, L.Format("PortableInstallUpdateQuestionFormat", update.Version),
            L.Get("UpdateAvailableTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
          if (confirmation != MessageBoxResult.Yes) return;
          var path = await _viewModel.PrepareUpdateAsync(update);
          await _viewModel.LaunchPreparedUpdateAsync(update, path);
          ((App)Application.Current).ExitApplication();
          return;
        }
        MessageBox.Show(this, L.Format("UpdateDetectedFormat", update.Version), L.Get("UpdateAvailableTitle"),
          MessageBoxButton.OK, MessageBoxImage.Information);
      }
      catch (Exception exception)
      {
        MessageBox.Show(this, exception.Message, L.Get("UpdateFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
      }
    });
  }

  private async void UpdateNow_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.IsPortable || _viewModel.AvailableUpdate is not { } update) return;
    await RunUiActionAsync(sender, async () =>
    {
      try
      {
        var confirmation = MessageBox.Show(this, L.Format("InstallUpdateQuestionFormat", update.Version),
          L.Get("UpdateAvailableTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes) return;
        var path = await _viewModel.PrepareUpdateAsync(update);
        await _viewModel.LaunchPreparedUpdateAsync(update, path);
        ((App)Application.Current).ExitApplication();
      }
      catch (Exception exception)
      {
        MessageBox.Show(this, exception.Message, L.Get("UpdateFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
      }
    });
  }

  private async Task RunUiActionAsync(object sender, Func<Task> action)
  {
    if (_uiActionRunning) return;
    _uiActionRunning = true;
    var control = sender as UIElement;
    var wasEnabled = control?.IsEnabled == true;
    var previousCursor = Mouse.OverrideCursor;
    try
    {
      control?.SetCurrentValue(IsEnabledProperty, false);
      await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
      Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
      await action();
    }
    catch (Exception exception)
    {
      MessageBox.Show(this, L.Format("ActionFailedFormat", exception.Message), L.Get("ActionFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    finally
    {
      Mouse.OverrideCursor = previousCursor;
      control?.SetCurrentValue(IsEnabledProperty, wasEnabled);
      _uiActionRunning = false;
    }
  }

  private void Window_Closing(object? sender, CancelEventArgs e)
  {
    var app = (App)Application.Current;
    if (AllowClose || app.IsExiting) return;
    e.Cancel = true;
    Dispatcher.BeginInvoke(ShouldHideOnClose(_viewModel.CloseToTray, AllowClose, app.IsExiting)
      ? app.HideWindow
      : app.ExitApplication);
  }

  internal static bool ShouldHideOnClose(bool closeToTray, bool allowClose, bool appIsExiting) =>
    closeToTray && !allowClose && !appIsExiting;

  private void Window_StateChanged(object? sender, EventArgs e)
  {
    if (WindowState == WindowState.Minimized && _viewModel.CloseToTray)
      Dispatcher.BeginInvoke(() => ((App)Application.Current).HideWindow());
  }

  private void Window_Deactivated(object? sender, EventArgs e) => _viewModel.TypingChallenges?.Pause();

  private void Window_SourceInitialized(object sender, EventArgs e) => ((App)Application.Current).MainWindow = this;
}
