using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using VoxGen.Desktop.Audio;
using VoxGen.Desktop.History;
using VoxGen.Desktop.Hotkeys;
using VoxGen.Desktop.Logging;
using VoxGen.Desktop.ReadAloud;
using VoxGen.Desktop.Settings;
using VoxGen.Desktop.Transcription;
using VoxGen.Desktop.Updater;

namespace VoxGen.Desktop.UI;

/// <summary>
/// The single settings window â€” PRD Â§8.9. Tray-resident app, so closing hides rather than
/// disposes; call <see cref="ForceClose"/> from <c>App.OnExit</c> at real shutdown.
///
/// State flow follows PRD Â§10 strictly: user changes a control â†’ SettingsService.TryUpdate â†’
/// service raises Changed â†’ this window re-reads SettingsService.Current and updates the UI.
/// Controls are never optimistic on their own; if TryUpdate fails, the service rolls back and
/// re-fires Changed, so the UI reverts naturally. App reacts to the same Changed event to
/// re-initialize capture / re-register the hotkey.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ILogger _logger;
    private readonly IAudioDeviceEnumerator _deviceEnumerator;
    private readonly ApiKeyStore _apiKeys;
    private readonly HistoryStore _history;
    private readonly UpdateService _updates;
    private readonly HttpClient _groqHttp;
    private readonly Action _shutdownForUpdate;

    private bool _suppress;
    private bool _recordingHotkey;
    private UpdateInfo? _pendingUpdate;

    /// <summary>Sentinel device representing "let Windows pick" (persisted as a null microphone id).</summary>
    private static readonly AudioDevice DefaultDevice = new() { Id = string.Empty, Name = "System default" };

    public SettingsWindow(
        SettingsService settings,
        ILogger logger,
        IAudioDeviceEnumerator deviceEnumerator,
        ApiKeyStore apiKeys,
        HistoryStore history,
        UpdateService updates,
        HttpClient groqHttp,
        Action shutdownForUpdate)
    {
        _settings = settings;
        _logger = logger;
        _deviceEnumerator = deviceEnumerator;
        _apiKeys = apiKeys;
        _history = history;
        _updates = updates;
        _groqHttp = groqHttp;
        _shutdownForUpdate = shutdownForUpdate;

        InitializeComponent();

        PopulateMicrophones();
        VoiceCombo.ItemsSource = GroqSpeech.Voices;

        _settings.Changed += OnSettingsChanged;
        _apiKeys.Changed += OnApiKeyChanged;
        _history.Changed += OnHistoryChanged;
        Closing += OnClosing;

        VersionText.Text = $"Version {_updates.CurrentVersion.ToString(3)}";
        if (!_updates.CanSelfUpdate)
        {
            UpdateStatusText.Text = "Updates are available for the downloaded VoxGen.exe only (this is a dev build).";
        }

        ApplySettingsToUi(_settings.Current);
        RefreshApiKeyUi();
        RefreshHistory();
    }

    /// <summary>Opens straight onto a section, e.g. "ApiKey" on first run.</summary>
    public void ShowSectionByName(string name)
    {
        NavGeneral.IsChecked = name == "General";
        NavApiKey.IsChecked = name == "ApiKey";
        NavHistory.IsChecked = name == "History";
        NavAbout.IsChecked = name == "About";
        if (name == "History") RefreshHistory();
        ShowSection(name);
    }

    // ============ population ============

    private void PopulateMicrophones()
    {
        var devices = new List<AudioDevice> { DefaultDevice };
        devices.AddRange(_deviceEnumerator.GetCaptureDevices());
        MicrophoneCombo.ItemsSource = devices;
    }

    // ============ settings binding ============

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplySettingsToUi(e.Current));
            return;
        }
        ApplySettingsToUi(e.Current);
    }

    private void ApplySettingsToUi(AppSettings s)
    {
        _suppress = true;
        try
        {
            CleanupToggle.IsChecked = s.CleanupEnabled;
            LiveTypingToggle.IsChecked = s.LiveTypingEnabled;
            HotkeyDisplay.Text = s.HotkeyValue;
            ModeHold.IsChecked = s.HotkeyMode == HotkeyMode.Hold;
            ModeToggle.IsChecked = s.HotkeyMode == HotkeyMode.Toggle;
            HoverReadToggle.IsChecked = s.HoverReadEnabled;
            VoiceCombo.SelectedItem = Array.IndexOf(GroqSpeech.Voices, s.HoverReadVoice) >= 0 ? s.HoverReadVoice : GroqSpeech.DefaultVoice;
            SaveTextToggle.IsChecked = s.SaveTextHistoryLocal;
            SaveAudioToggle.IsChecked = s.SaveAudioHistoryLocal;
            SelectMicrophone(s.SelectedMicrophoneId);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void SelectMicrophone(string? id)
    {
        var items = (IEnumerable<AudioDevice>)MicrophoneCombo.ItemsSource;
        var match = string.IsNullOrEmpty(id)
            ? DefaultDevice
            : items.FirstOrDefault(d => d.Id == id) ?? DefaultDevice;
        MicrophoneCombo.SelectedItem = match;
    }

    // ============ API key ============

    private void OnApiKeyChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RefreshApiKeyUi); return; }
        RefreshApiKeyUi();
    }

    private void RefreshApiKeyUi()
    {
        var key = _apiKeys.Get();
        if (string.IsNullOrEmpty(key))
        {
            ApiKeyStatusText.Text = "No key saved";
            ApiKeyStatusDetail.Text = "Dictation needs a Groq API key. Groq has a free tier.";
            RemoveApiKeyButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ApiKeyStatusText.Text = $"Key saved ({ApiKeyStore.Mask(key)})";
            ApiKeyStatusDetail.Text = "Paste a new key below to replace it.";
            RemoveApiKeyButton.Visibility = Visibility.Visible;
        }
    }

    private void ShowApiKeyResult(string message)
    {
        ApiKeyResultText.Text = message;
        ApiKeyResultText.Visibility = Visibility.Visible;
    }

    private void OnApiKeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSaveApiKeyClick(sender, e);
    }

    private async void OnSaveApiKeyClick(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            ShowApiKeyResult("Paste your Groq API key first.");
            return;
        }

        try
        {
            _apiKeys.Save(key);
            ApiKeyBox.Clear();
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to save API key", new() { ["error"] = ex.Message });
            ShowApiKeyResult($"Couldn't save the key: {ex.Message}");
            return;
        }

        ShowApiKeyResult("Saved. Checking it with Groqâ€¦");
        await TestKeyAsync(key);
    }

    private async void OnTestApiKeyClick(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length == 0) key = _apiKeys.Get() ?? string.Empty;
        if (key.Length == 0)
        {
            ShowApiKeyResult("No key to test. Paste one above.");
            return;
        }
        ShowApiKeyResult("Checking with Groqâ€¦");
        await TestKeyAsync(key);
    }

    private async Task TestKeyAsync(string key)
    {
        TestApiKeyButton.IsEnabled = false;
        SaveApiKeyButton.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await GroqWhisperProvider.VerifyKeyAsync(_groqHttp, key, cts.Token);
            ShowApiKeyResult("Key works. You're ready to dictate.");
        }
        catch (InvalidApiKeyException)
        {
            ShowApiKeyResult("Groq rejected this key. Check it at console.groq.com/keys.");
        }
        catch (Exception ex)
        {
            ShowApiKeyResult($"Couldn't reach Groq to check the key: {ex.Message}");
        }
        finally
        {
            TestApiKeyButton.IsEnabled = true;
            SaveApiKeyButton.IsEnabled = true;
        }
    }

    private void OnRemoveApiKeyClick(object sender, RoutedEventArgs e)
    {
        _apiKeys.Clear();
        ShowApiKeyResult("Key removed from this PC.");
    }

    private void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenShell(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    // ============ history ============

    private sealed record HistoryRow(HistoryEntry Entry, string When, string Text, string? AudioPath)
    {
        public Visibility CopyVisibility => string.IsNullOrEmpty(Entry.Text) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PlayVisibility => AudioPath is not null && File.Exists(AudioPath) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshHistory); return; }
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        var rows = _history.LoadRecent(100).Select(e => new HistoryRow(
            e,
            e.TimestampUtc.ToLocalTime().ToString("MMM d, h:mm tt"),
            string.IsNullOrEmpty(e.Text) ? "(recording only)" : e.Text,
            _history.AudioPath(e))).ToList();
        HistoryList.ItemsSource = rows;
        HistoryEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmClearButton.Visibility = Visibility.Collapsed;
    }

    private void OnHoverReadToggleClick(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var requested = HoverReadToggle.IsChecked == true;
        if (!_settings.TryUpdate(c => c with { HoverReadEnabled = requested }, out var error))
        {
            ReportSaveFailure("read aloud", error);
        }
    }

    private void OnVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || VoiceCombo.SelectedItem is not string voice) return;
        if (!_settings.TryUpdate(c => c with { HoverReadVoice = voice }, out var error))
        {
            ReportSaveFailure("voice", error);
        }
    }

    private void OnSaveTextToggleClick(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var requested = SaveTextToggle.IsChecked == true;
        if (!_settings.TryUpdate(c => c with { SaveTextHistoryLocal = requested }, out var error))
        {
            ReportSaveFailure("save transcripts", error);
        }
    }

    private void OnSaveAudioToggleClick(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var requested = SaveAudioToggle.IsChecked == true;
        if (!_settings.TryUpdate(c => c with { SaveAudioHistoryLocal = requested }, out var error))
        {
            ReportSaveFailure("save recordings", error);
        }
    }

    private void OnHistoryCopyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryRow row) return;
        try
        {
            System.Windows.Clipboard.SetText(row.Entry.Text);
            if (sender is Button b) { b.Content = "Copied"; _ = ResetButtonLabelAsync(b, "Copy"); }
        }
        catch (Exception ex) { _logger.Warning("Copy from history failed", new() { ["error"] = ex.Message }); }
    }

    private void OnHistoryDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryRow row) return;
        // History.Changed fires on success and refreshes the list.
        if (!_history.Delete(row.Entry.Id)) RefreshHistory();
    }

    private void OnHistoryPlayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryRow { AudioPath: { } path }) OpenShell(path);
    }

    private static async Task ResetButtonLabelAsync(Button button, string label)
    {
        await Task.Delay(1200);
        button.Content = label;
    }

    private void OnOpenRecordingsClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_history.RecordingsDirectory);
        OpenShell(_history.RecordingsDirectory);
    }

    // Two-step clear instead of a modal confirm: first click reveals the destructive button.
    private void OnClearHistoryClick(object sender, RoutedEventArgs e) =>
        ConfirmClearButton.Visibility = ConfirmClearButton.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void OnConfirmClearClick(object sender, RoutedEventArgs e) => _history.Clear();

    // ============ updates ============

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking GitHubâ€¦";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            ShowUpdate(await _updates.CheckAsync(cts.Token));
        }
        catch (Exception ex)
        {
            _logger.Warning("Update check failed", new() { ["error"] = ex.Message });
            UpdateStatusText.Text = "Couldn't check for updates. Try again later.";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>Called by App when a background check finds a newer release.</summary>
    public void ShowUpdate(UpdateInfo? update)
    {
        _pendingUpdate = update;
        if (update is null)
        {
            UpdateStatusText.Text = "You're on the latest version.";
            InstallUpdateButton.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateStatusText.Text = _updates.CanSelfUpdate
            ? $"Version {update.Version.ToString(3)} is available."
            : $"Version {update.Version.ToString(3)} is available (dev build can't self-update).";
        InstallUpdateButton.Visibility = _updates.CanSelfUpdate ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateStatusText.Text = $"Downloadingâ€¦ {p:P0}");
        try
        {
            if (await _updates.DownloadAndApplyAsync(_pendingUpdate, progress, CancellationToken.None))
            {
                UpdateStatusText.Text = "Restartingâ€¦";
                _shutdownForUpdate();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Update failed", new() { ["error"] = ex.Message });
            UpdateStatusText.Text = $"Update failed: {ex.Message}";
            InstallUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); }
        catch (Exception ex) { _logger.Error("Failed to open", new() { ["target"] = target, ["error"] = ex.Message }); }
    }

    // ============ control handlers ============

    private void OnCleanupToggleClick(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var requested = CleanupToggle.IsChecked == true;
        if (!_settings.TryUpdate(c => c with { CleanupEnabled = requested }, out var error))
        {
            ReportSaveFailure("AI cleanup", error);
        }
    }

    private void OnLiveTypingToggleClick(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var requested = LiveTypingToggle.IsChecked == true;
        if (!_settings.TryUpdate(c => c with { LiveTypingEnabled = requested }, out var error))
        {
            ReportSaveFailure("live typing", error);
        }
    }

    private void OnMicrophoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (MicrophoneCombo.SelectedItem is not AudioDevice dev) return;

        var id = string.IsNullOrEmpty(dev.Id) ? null : dev.Id;
        var name = string.IsNullOrEmpty(dev.Id) ? null : dev.Name;
        if (!_settings.TryUpdate(c => c with { SelectedMicrophoneId = id, SelectedMicrophoneName = name }, out var error))
        {
            ReportSaveFailure("microphone", error);
        }
    }

    private void OnHotkeyModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var mode = ModeToggle.IsChecked == true ? HotkeyMode.Toggle : HotkeyMode.Hold;
        if (!_settings.TryUpdate(c => c with { HotkeyMode = mode }, out var error))
        {
            ReportSaveFailure("activation mode", error);
        }
    }

    // ============ hotkey recorder ============

    private void OnHotkeyRecordClick(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        HotkeyDisplay.Text = "Press keysâ€¦";
        HotkeyRecordButton.IsEnabled = false;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndHotkeyRecording(null); // cancel
            return;
        }

        var candidate = BuildHotkeyString(key, Keyboard.Modifiers);
        if (candidate is null) return; // a modifier on its own with more to come â€” keep waiting

        // Only accept combinations the parser round-trips (keeps unsupported keys out of settings).
        try { _ = HotkeyDefinition.Parse(candidate); }
        catch (System.FormatException) { return; }

        EndHotkeyRecording(candidate);
    }

    private void EndHotkeyRecording(string? value)
    {
        _recordingHotkey = false;
        HotkeyRecordButton.IsEnabled = true;

        if (value is null)
        {
            ApplySettingsToUi(_settings.Current); // restore display
            return;
        }

        if (!_settings.TryUpdate(c => c with { HotkeyValue = value }, out var error))
        {
            ReportSaveFailure("hotkey", error);
        }
        ApplySettingsToUi(_settings.Current);
    }

    /// <summary>Maps a WPF key + modifiers to the string form <see cref="HotkeyDefinition"/> parses.</summary>
    private static string? BuildHotkeyString(Key key, ModifierKeys mods)
    {
        // Bare modifier â†’ a modifier-only hotkey (e.g. RightAlt).
        switch (key)
        {
            case Key.LeftAlt: return "LeftAlt";
            case Key.RightAlt: return "RightAlt";
            case Key.LeftCtrl: return "LeftControl";
            case Key.RightCtrl: return "RightControl";
            case Key.LeftShift: return "LeftShift";
            case Key.RightShift: return "RightShift";
            case Key.LWin: return "LeftWin";
            case Key.RWin: return "RightWin";
        }

        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private void ReportSaveFailure(string what, string? error)
    {
        _logger.Error("Failed to persist setting", new() { ["setting"] = what, ["error"] = error });
        MessageBox.Show(
            this,
            $"VoxGen couldn't save the {what} setting. The change has been reverted.\n\n{error}",
            "VoxGen",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }


    // ============ sidebar nav ============

    private void OnNavGeneralClick(object sender, RoutedEventArgs e) => ShowSection("General");
    private void OnNavApiKeyClick(object sender, RoutedEventArgs e) => ShowSection("ApiKey");
    private void OnNavHistoryClick(object sender, RoutedEventArgs e) { RefreshHistory(); ShowSection("History"); }
    private void OnNavAboutClick(object sender, RoutedEventArgs e) => ShowSection("About");

    private void ShowSection(string name)
    {
        GeneralPanel.Visibility = name == "General" ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyPanel.Visibility = name == "ApiKey" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = name == "History" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = name == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============ lifetime ============

    private bool _forceClosing;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClosing) return;
        e.Cancel = true;
        Hide();
    }

    /// <summary>Closes for real â€” called from <c>App.OnExit</c> so event subscriptions are released.</summary>
    public void ForceClose()
    {
        _forceClosing = true;
        _settings.Changed -= OnSettingsChanged;
        _apiKeys.Changed -= OnApiKeyChanged;
        _history.Changed -= OnHistoryChanged;
        Close();
    }
}
