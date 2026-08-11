using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

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
    if (!AllowClose && !app.IsExiting && _viewModel.CloseToTray)
    {
      e.Cancel = true;
      Dispatcher.BeginInvoke(app.HideWindow);
    }
  }

  private void Window_StateChanged(object? sender, EventArgs e)
  {
    if (WindowState == WindowState.Minimized && _viewModel.CloseToTray)
      Dispatcher.BeginInvoke(() => ((App)Application.Current).HideWindow());
  }

  private void Window_SourceInitialized(object sender, EventArgs e) => ((App)Application.Current).MainWindow = this;
}
