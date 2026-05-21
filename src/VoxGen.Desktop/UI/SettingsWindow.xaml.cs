using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoxGen.Desktop.Audio;
using VoxGen.Desktop.Auth;
using VoxGen.Desktop.Backend;
using VoxGen.Desktop.Hotkeys;
using VoxGen.Desktop.License;
using VoxGen.Desktop.Logging;
using VoxGen.Desktop.Settings;

namespace VoxGen.Desktop.UI;

/// <summary>
/// The single settings window — PRD §8.9. Tray-resident app, so closing hides rather than
/// disposes; call <see cref="ForceClose"/> from <c>App.OnExit</c> at real shutdown.
///
/// State flow follows PRD §10 strictly: user changes a control → SettingsService.TryUpdate →
/// service raises Changed → this window re-reads SettingsService.Current and updates the UI.
/// Controls are never optimistic on their own; if TryUpdate fails, the service rolls back and
/// re-fires Changed, so the UI reverts naturally. App reacts to the same Changed event to
/// re-initialize capture / re-register the hotkey.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ILogger _logger;
    private readonly IAudioDeviceEnumerator _deviceEnumerator;
    private readonly SessionManager? _sessions;
    private readonly Func<CancellationToken, Task<LicenseStatus?>>? _getLicenseStatus;

    private bool _suppress;
    private bool _recordingHotkey;
    private bool _accountBusy;

    /// <summary>Sentinel device representing "let Windows pick" (persisted as a null microphone id).</summary>
    private static readonly AudioDevice DefaultDevice = new() { Id = string.Empty, Name = "System default" };

    /// <param name="sessions">
    /// The signed-in session, or <c>null</c> when Supabase auth isn't configured (no-login local build).
    /// When null, the Account tab shows a "not available" note instead of sign-in controls.
    /// </param>
    public SettingsWindow(
        SettingsService settings,
        ILogger logger,
        IAudioDeviceEnumerator deviceEnumerator,
        SessionManager? sessions = null,
        Func<CancellationToken, Task<LicenseStatus?>>? getLicenseStatus = null)
    {
        _settings = settings;
        _logger = logger;
        _deviceEnumerator = deviceEnumerator;
        _sessions = sessions;
        _getLicenseStatus = getLicenseStatus;

        InitializeComponent();

        PopulateMicrophones();

        _settings.Changed += OnSettingsChanged;
        if (_sessions is not null) _sessions.Changed += OnSessionChanged;
        Closing += OnClosing;

        ApplySettingsToUi(_settings.Current);
        RefreshAccountUi();
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

    // ============ account / sign-in ============

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        // SessionManager.Changed can fire off the UI thread (sign-out completes on a pool thread).
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshAccountUi);
            return;
        }
        RefreshAccountUi();
    }

    private void RefreshAccountUi()
    {
        // No auth configured (local-only build): hide the controls, show the note.
        if (_sessions is null)
        {
            AccountStatusText.Text = "Local mode";
            AccountStatusDetail.Text = "This build transcribes on your device — no account needed.";
            SignInButton.Visibility = Visibility.Collapsed;
            SignOutButton.Visibility = Visibility.Collapsed;
            PlanCard.Visibility = Visibility.Collapsed;
            AccountUnavailableText.Visibility = Visibility.Collapsed;
            return;
        }

        if (_sessions.IsSignedIn)
        {
            AccountStatusText.Text = string.IsNullOrEmpty(_sessions.Email)
                ? "Signed in"
                : $"Signed in as {_sessions.Email}";
            AccountStatusDetail.Text = "VoxGen is using managed cloud transcription.";
            SignInButton.Visibility = Visibility.Collapsed;
            SignOutButton.Visibility = Visibility.Visible;

            // Show the plan card and fetch the real trial/subscription status.
            PlanCard.Visibility = Visibility.Visible;
            PlanText.Text = "Plan";
            PlanDetail.Text = "Checking…";
            UpgradeButton.Visibility = Visibility.Visible;
            ManageBillingButton.Visibility = Visibility.Collapsed;
            _ = LoadLicenseAsync();
        }
        else
        {
            AccountStatusText.Text = "Not signed in";
            AccountStatusDetail.Text = "Sign in to use VoxGen's managed transcription.";
            SignInButton.Visibility = Visibility.Visible;
            SignOutButton.Visibility = Visibility.Collapsed;
            PlanCard.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadLicenseAsync()
    {
        if (_getLicenseStatus is null)
        {
            PlanCard.Visibility = Visibility.Collapsed;
            return;
        }

        LicenseStatus? status = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            status = await _getLicenseStatus(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.Warning("License status fetch failed", new() { ["error"] = ex.Message });
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyLicenseToUi(status));
            return;
        }
        ApplyLicenseToUi(status);
    }

    private void ApplyLicenseToUi(LicenseStatus? status)
    {
        // Guard: the user may have signed out while the fetch was in flight.
        if (_sessions is null || !_sessions.IsSignedIn)
        {
            PlanCard.Visibility = Visibility.Collapsed;
            return;
        }

        PlanCard.Visibility = Visibility.Visible;

        if (status is null)
        {
            PlanText.Text = "Plan";
            PlanDetail.Text = "Couldn't check your plan right now — you can still upgrade.";
            UpgradeButton.Visibility = Visibility.Visible;
            ManageBillingButton.Visibility = Visibility.Collapsed;
            return;
        }

        switch (status.State)
        {
            case LicenseState.Trial:
                PlanText.Text = "Free Trial";
                PlanDetail.Text = status.TrialDaysLeft > 0
                    ? $"{status.TrialDaysLeft} day{(status.TrialDaysLeft == 1 ? "" : "s")} left — upgrade anytime."
                    : "Trial ending today — upgrade to keep dictating.";
                UpgradeButton.Visibility = Visibility.Visible;
                ManageBillingButton.Visibility = Visibility.Collapsed;
                break;

            case LicenseState.Active:
                PlanText.Text = string.IsNullOrWhiteSpace(status.PlanName) ? "Active" : status.PlanName;
                PlanDetail.Text = "Your subscription is active.";
                UpgradeButton.Visibility = Visibility.Collapsed;
                ManageBillingButton.Visibility = Visibility.Visible;
                break;

            default: // Expired / NotActivated
                PlanText.Text = status.State == LicenseState.Expired ? "Plan expired" : "No active plan";
                PlanDetail.Text = "Upgrade to keep using VoxGen's cloud transcription.";
                UpgradeButton.Visibility = Visibility.Visible;
                ManageBillingButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private void OnUpgradeClick(object sender, RoutedEventArgs e) => OpenWebsite("/#pricing");

    private void OnManageBillingClick(object sender, RoutedEventArgs e) => OpenWebsite("/account.html");

    /// <summary>Open a page on the VoxGen website (shares the backend domain) in the default browser.</summary>
    private void OpenWebsite(string path)
    {
        try
        {
            var baseUrl = BackendConfig.VoxGenBackendBaseUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl) || baseUrl == "REPLACE_AT_BUILD") return;
            Process.Start(new ProcessStartInfo { FileName = baseUrl + path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to open website", new() { ["error"] = ex.Message, ["path"] = path });
        }
    }

    private void OnAccountSignInClick(object sender, RoutedEventArgs e)
    {
        if (_sessions is null || _accountBusy) return;
        try
        {
            var window = new SignInWindow(_sessions, _logger) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to open sign-in window", new() { ["error"] = ex.Message });
        }
        RefreshAccountUi(); // SignInWindow also fires Changed, but refresh defensively.
    }

    private void OnAccountSignOutClick(object sender, RoutedEventArgs e)
    {
        if (_sessions is null || _accountBusy) return;
        _ = SignOutAsync();
    }

    private async Task SignOutAsync()
    {
        _accountBusy = true;
        SignOutButton.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _sessions!.SignOutAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.Error("Sign-out failed", new() { ["error"] = ex.Message });
        }
        finally
        {
            _accountBusy = false;
            SignOutButton.IsEnabled = true;
            RefreshAccountUi();
        }
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
        HotkeyDisplay.Text = "Press keys…";
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
        if (candidate is null) return; // a modifier on its own with more to come — keep waiting

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
        // Bare modifier → a modifier-only hotkey (e.g. RightAlt).
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
    private void OnNavAccountClick(object sender, RoutedEventArgs e) => ShowSection("Account");
    private void OnNavAboutClick(object sender, RoutedEventArgs e) => ShowSection("About");

    private void ShowSection(string name)
    {
        GeneralPanel.Visibility = name == "General" ? Visibility.Visible : Visibility.Collapsed;
        AccountPanel.Visibility = name == "Account" ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Closes for real — called from <c>App.OnExit</c> so the Changed subscription is released.</summary>
    public void ForceClose()
    {
        _forceClosing = true;
        _settings.Changed -= OnSettingsChanged;
        if (_sessions is not null) _sessions.Changed -= OnSessionChanged;
        Close();
    }
}
