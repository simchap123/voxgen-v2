using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VoxGen.Desktop.ReadAloud;

/// <summary>
/// Small floating controls shown while VoxGen reads aloud. Appears at the bottom-center of the
/// screen the mouse is on (above the dictation pill), never steals focus, and hides a few seconds
/// after reading ends. Speed changes are persisted through <see cref="_persistRate"/>.
/// </summary>
public partial class ReadAloudBar : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private static readonly Geometry PauseGlyph = Geometry.Parse("M1,0 H4 V10 H1 Z M6,0 H9 V10 H6 Z");
    private static readonly Geometry PlayGlyph = Geometry.Parse("M1,0 L10,5 L1,10 Z");
    private static readonly Geometry CopyGlyph = Geometry.Parse("M3.5,0.5 H10.5 V7.5 H3.5 Z M0.5,3.5 V10.5 H7.5");
    private static readonly Geometry CheckGlyph = Geometry.Parse("M0.5,5.5 L4,9 L10.5,1.5");

    private readonly SpeechPlayer _player;
    private readonly Action<double> _persistRate;
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public ReadAloudBar(SpeechPlayer player, Action<double> persistRate)
    {
        _player = player;
        _persistRate = persistRate;
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (_player.State == SpeechState.Idle) Hide(); };
        _player.StateChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        RateText.Text = $"{_player.Rate:0.##}×";

        switch (_player.State)
        {
            case SpeechState.Idle:
                StatusDot.Opacity = 0.3;
                PauseIcon.Data = PlayGlyph;
                PauseButton.ToolTip = "Play";
                _hideTimer.Stop();
                _hideTimer.Start();
                return;
            case SpeechState.Loading:
                StatusDot.Opacity = 0.6;
                break;
            case SpeechState.Playing:
                StatusDot.Opacity = 1;
                PauseIcon.Data = PauseGlyph;
                PauseButton.ToolTip = "Pause";
                break;
            case SpeechState.Paused:
                StatusDot.Opacity = 0.6;
                PauseIcon.Data = PlayGlyph;
                PauseButton.ToolTip = "Resume";
                break;
        }

        _hideTimer.Stop();
        if (!IsVisible)
        {
            PositionNearCursorScreen();
            Show();
        }
    }

    private void PositionNearCursorScreen()
    {
        var (x, y) = ScreenTextReader.CursorPosition();
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;

        // Screen coordinates are physical pixels; WPF positions in DIPs.
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = ActualWidth > 0 ? ActualWidth : 260;
        var height = ActualHeight > 0 ? ActualHeight : 50;
        Left = area.Left / dpi.DpiScaleX + (area.Width / dpi.DpiScaleX - width) / 2;
        Top = area.Bottom / dpi.DpiScaleY - height - 72; // clear of the dictation pill
    }

    private void OnSlower(object sender, RoutedEventArgs e) { _player.Slower(); _persistRate(_player.Rate); }
    private void OnFaster(object sender, RoutedEventArgs e) { _player.Faster(); _persistRate(_player.Rate); }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        // At the end of a read, Play starts it again from the top.
        if (_player.State == SpeechState.Idle && _player.CurrentText is { } text) _player.Speak(text);
        else _player.TogglePause();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        Hide();
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_player.CurrentText is not { } text) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
            CopyIcon.Data = CheckGlyph;
            await Task.Delay(1200);
            CopyIcon.Data = CopyGlyph;
        }
        catch
        {
            // clipboard busy — ignore
        }
    }
}
