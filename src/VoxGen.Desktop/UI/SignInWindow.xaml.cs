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
            ShowError(FriendlyAuthError(ex));
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
        StatusContainer.Visibility = Visibility.Visible;
        InvalidateMeasure(); // window is SizeToContent — let it grow for the error.
    }

    private void HideError() => StatusContainer.Visibility = Visibility.Collapsed;

    /// <summary>Turn raw Supabase auth errors into something a tester can act on.</summary>
    private static string FriendlyAuthError(SupabaseAuthException ex)
    {
        // 429 = Supabase auth/email rate limit. The most common cause is "Confirm email" being
        // enabled (every signup tries to send a mail and trips the limit). Surfaced plainly here.
        if ((int)ex.StatusCode == 429)
        {
            return "Too many attempts right now. Wait a minute and try again.";
        }

        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("Invalid login", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("invalid credentials", StringComparison.OrdinalIgnoreCase))
        {
            return "That email or password doesn't match. Try again, or create an account.";
        }
        if (msg.Contains("without a usable session", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("confirm", StringComparison.OrdinalIgnoreCase))
        {
            return "Your account needs email confirmation before you can sign in.";
        }
        return string.IsNullOrWhiteSpace(msg) ? "Sign-in failed. Please try again." : msg;
    }
}
