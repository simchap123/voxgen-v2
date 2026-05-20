using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VoxGen.Desktop.Auth;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.UI;

/// <summary>
/// Email/password sign-in + account creation (PRD §8.2). On success it closes with
/// <see cref="Window.DialogResult"/>/<see cref="Succeeded"/> set; the app then has a live session.
/// </summary>
public partial class SignInWindow : Window
{
    private readonly SessionManager _sessions;
    private readonly ILogger _logger;
    private bool _busy;

    public bool Succeeded { get; private set; }

    public SignInWindow(SessionManager sessions, ILogger logger)
    {
        _sessions = sessions;
        _logger = logger;
        InitializeComponent();
        Loaded += (_, _) => EmailBox.Focus();
    }

    private void OnSignInClick(object sender, RoutedEventArgs e) =>
        _ = AuthenticateAsync(signUp: false);

    private void OnSignUpClick(object sender, RoutedEventArgs e) =>
        _ = AuthenticateAsync(signUp: true);

    private async Task AuthenticateAsync(bool signUp)
    {
        if (_busy) return;

        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;
        if (email.Length == 0 || !email.Contains('@') || password.Length == 0)
        {
            ShowError("Enter a valid email and password.");
            return;
        }

        SetBusy(true);
        HideError();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (signUp)
            {
                await _sessions.SignUpAsync(email, password, cts.Token);
            }
            else
            {
                await _sessions.SignInAsync(email, password, cts.Token);
            }

            Succeeded = true;
            Close();
        }
        catch (SupabaseAuthException ex)
        {
            _logger.Warning("Sign-in failed", new() { ["status"] = (int)ex.StatusCode, ["error"] = ex.Message });
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Error("Sign-in error", new() { ["error"] = ex.Message });
            ShowError("Something went wrong. Check your connection and try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SignInButton.IsEnabled = !busy;
        SignUpButton.IsEnabled = !busy;
        EmailBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        SignInButton.Content = busy ? "Signing in…" : "Sign in";
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideError() => StatusText.Visibility = Visibility.Collapsed;
}
