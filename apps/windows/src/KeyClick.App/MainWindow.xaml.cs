using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
  private static LocalizationService L => LocalizationService.Current;
  internal bool AllowClose { get; set; }

  public MainWindow(MainViewModel viewModel)
  {
    _viewModel = viewModel;
    DataContext = viewModel;
    InitializeComponent();
  }

  private void Navigation_Click(object sender, RoutedEventArgs e)
  {
    if (sender is Button { Tag: string tag } && int.TryParse(tag, out var page)) PageTabs.SelectedIndex = page;
  }

  private async void ImportSound_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog { Title = L.Get("DialogImportSound"), Filter = L.Get("FilterSoundFiles") };
    if (dialog.ShowDialog(this) != true) return;
    try { await _viewModel.ImportMappingSoundAsync(dialog.FileName); }
    catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("DialogImportFailed"), MessageBoxButton.OK, MessageBoxImage.Warning); }
  }

  private async void ImportSoundPack_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog { Title = L.Get("DialogImportSoundPack"), Filter = L.Get("FilterSoundPacks") };
    if (dialog.ShowDialog(this) != true) return;
    try { await _viewModel.ImportSoundPackAsync(dialog.FileName); }
    catch (SoundPackImportException exception)
    {
      MessageBox.Show(this, L.Format(exception.ResourceKey, exception.Arguments), L.Get("DialogSoundPackImportFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    catch (Exception)
    {
      MessageBox.Show(this, L.Get("SoundPackArchiveInvalid"), L.Get("DialogSoundPackImportFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
  }

  private async void EditShortcut_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.SelectedShortcut is not { } selected) return;
    var editor = new ShortcutEditorWindow(selected) { Owner = this };
    if (editor.ShowDialog() != true || editor.Result is null) return;
    try { await _viewModel.SaveShortcutAsync(editor.Result); }
    catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("DialogShortcutUnavailable"), MessageBoxButton.OK, MessageBoxImage.Warning); }
  }

  private async void RestoreShortcuts_Click(object sender, RoutedEventArgs e)
  {
    if (MessageBox.Show(this, L.Get("RestoreShortcutsQuestion"), L.Get("RestoreShortcutsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
    try
    {
      foreach (var binding in KeyClick.Core.BuiltInCatalog.DefaultShortcuts) await _viewModel.SaveShortcutAsync(binding);
    }
    catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("RestoreShortcutsFailed"), MessageBoxButton.OK, MessageBoxImage.Warning); }
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
    _viewModel.Statistics?.SetVisible(PageTabs.SelectedIndex == 1);
  }

  private async void RefreshStatistics_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.Statistics is not null) await _viewModel.Statistics.RefreshAsync();
  }

  private async void ExportStatistics_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.Statistics?.Snapshot is null) return;
    var dialog = new SaveFileDialog { Title = L.Get("ExportCsv"), Filter = L.Get("FilterCsv"), FileName = $"KeyClick-statistics-{DateTime.Now:yyyy-MM-dd}.csv" };
    if (dialog.ShowDialog(this) == true) await _viewModel.Statistics.ExportAsync(dialog.FileName);
  }

  private async void DeleteStatistics_Click(object sender, RoutedEventArgs e)
  {
    if (_viewModel.Statistics is null) return;
    var dialog = new DeleteStatisticsWindow { Owner = this };
    if (dialog.ShowDialog() != true) return;
    if (MessageBox.Show(this, L.Get("DeleteStatisticsQuestion"), L.Get("DeleteStatistics"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    if (dialog.Request.CreateSafetyBackup) await _viewModel.CreateBackupNowAsync();
    await _viewModel.Statistics.DeleteAsync(dialog.Request);
    await _viewModel.Statistics.RefreshAsync();
  }

  private async void ExportProfile_Click(object sender, RoutedEventArgs e)
  {
    var options = new ProfileExportWindow { Owner = this };
    if (options.ShowDialog() != true) return;
    var dialog = new SaveFileDialog { Title = L.Get("ExportProfile"), Filter = L.Get("FilterProfile"), FileName = $"KeyClick-{DateTime.Now:yyyy-MM-dd}.keyclickprofile" };
    if (dialog.ShowDialog(this) != true) return;
    try
    {
      await _viewModel.ExportProfileAsync(dialog.FileName, options.Options);
      MessageBox.Show(this, L.Get("ProfileExported"), L.Get("ExportProfile"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
    catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("ExportProfile"), MessageBoxButton.OK, MessageBoxImage.Warning); }
  }

  private async void ImportProfile_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog { Title = L.Get("ImportProfile"), Filter = L.Get("FilterProfile") };
    if (dialog.ShowDialog(this) != true) return;
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
  }

  private async void ResetSettings_Click(object sender, RoutedEventArgs e)
  {
    if (MessageBox.Show(this, L.Get("ResetQuestion"), L.Get("ResetSettings"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
    try { await _viewModel.ResetSettingsAsync(); }
    catch (Exception exception) { MessageBox.Show(this, exception.Message, L.Get("ResetFailed"), MessageBoxButton.OK, MessageBoxImage.Warning); }
  }

  private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      var update = await _viewModel.CheckForUpdateAsync();
      if (update is null)
      {
        MessageBox.Show(this, L.Get("UpToDate"), L.Get("UpdatesTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        return;
      }
      var confirmation = MessageBox.Show(this,
        L.Format("UpdateQuestionFormat", update.Version),
        L.Get("UpdateAvailableTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
      if (confirmation != MessageBoxResult.Yes) return;
      var path = await _viewModel.DownloadUpdateAsync(update);
      Process.Start(new ProcessStartInfo(path, "--update") { UseShellExecute = true });
      ((App)Application.Current).ExitApplication();
    }
    catch (Exception exception)
    {
      MessageBox.Show(this, exception.Message, L.Get("UpdateFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
  }

  private void Window_Closing(object? sender, CancelEventArgs e)
  {
    var app = (App)Application.Current;
    if (AllowClose || app.IsExiting) return;
    e.Cancel = true;
    Dispatcher.BeginInvoke(app.ExitApplication);
  }

  private void Window_StateChanged(object? sender, EventArgs e)
  {
    if (WindowState == WindowState.Minimized && _viewModel.CloseToTray)
      Dispatcher.BeginInvoke(() => ((App)Application.Current).HideWindow());
  }

  private void Window_SourceInitialized(object sender, EventArgs e) => ((App)Application.Current).MainWindow = this;
}
