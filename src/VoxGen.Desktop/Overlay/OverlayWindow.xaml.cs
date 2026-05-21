using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using VoxGen.Desktop.Audio;

namespace VoxGen.Desktop.Overlay;

/// <summary>
/// The on-screen recording indicator (PRD §8.10) — a small, unobtrusive bottom-centre pill.
/// Implements <see cref="IRecordingOverlay"/>.
///
/// <para><b>Focus is sacred (PRD §8.5).</b> The overlay must never become the foreground window:
/// the handle captured the instant recording starts is what restores focus and targets the paste,
/// and a visible overlay that activated would clobber it. This is enforced three ways:
/// <list type="bullet">
///   <item><c>ShowActivated=False</c> on the Window (set in XAML) — never activates on Show.</item>
///   <item>Extended styles <c>WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW</c> applied in
///         <see cref="OnSourceInitialized"/> — never takes activation, is click-through, and stays out
///         of alt-tab.</item>
///   <item>We use <see cref="UIElement.Visibility"/> (Visible/Hidden), never <c>Activate()</c>.</item>
/// </list></para>
///
/// <para>All members are expected on the UI thread (the controller marshals via IUiDispatcher), but
/// <see cref="SetState"/> / <see cref="ShowError"/> defensively re-dispatch onto this window's own
/// Dispatcher if called from elsewhere.</para>
///
/// <para>The constructor does <b>not</b> Show the window — the initial state is <see cref="OverlayState.Hidden"/>.</para>
/// </summary>
public partial class OverlayWindow : Window, IRecordingOverlay
{
    /// <summary>How long an error pill stays up before auto-hiding (PRD §13 — non-blocking).</summary>
    private static readonly TimeSpan ErrorDisplayDuration = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer _errorTimer;
    private Storyboard? _pulseStoryboard;
    private OverlayState _state = OverlayState.Hidden;
    private bool _showingError;

    // Live waveform + elapsed timer (recording state).
    private const int BarCount = 13;
    private readonly List<Rectangle> _bars = new(BarCount);
    private DispatcherTimer? _waveTimer;
    private DispatcherTimer? _elapsedTimer;
    private DateTime _recordingStartUtc;
    private double _wavePhase;

    public OverlayWindow()
    {
        InitializeComponent();

        BuildWaveformBars();

        _errorTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = ErrorDisplayDuration,
        };
        _errorTimer.Tick += OnErrorTimerTick;

        // Reposition when the working area changes (taskbar move, resolution / DPI change) so the
        // pill never ends up off-screen or under the taskbar.
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += OnClosed;

        // Initial state — built but not shown.
        Visibility = Visibility.Hidden;
    }

    // ============ IRecordingOverlay ============

    public void SetState(OverlayState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetState(state));
            return;
        }

        // No-op redundant transitions so we don't restart the pulse/ellipsis animations on a
        // repeated SetState(Recording) etc. An in-flight error pill is always interrupted, though —
        // a real state change must win over a transient error.
        if (state == _state && !_showingError)
        {
            return;
        }

        _showingError = false;
        _errorTimer.Stop();
        _state = state;

        switch (state)
        {
            case OverlayState.Hidden:
                StopPulse();
                StopEllipsisAnimation();
                StopWaveform();
                StopElapsedTimer();
                Visibility = Visibility.Hidden;
                return;

            case OverlayState.Recording:
                StopEllipsisAnimation();
                ApplyRecordingVisuals();
                ShowPill();
                StartPulse();
                break;

            case OverlayState.Transcribing:
                StopPulse();
                ApplyTranscribingVisuals();
                ShowPill();
                StartEllipsisAnimation();
                break;

            default:
                // Unknown state — fail safe to hidden rather than showing a stale pill.
                StopPulse();
                StopEllipsisAnimation();
                Visibility = Visibility.Hidden;
                break;
        }
    }

    public void ShowError(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowError(message));
            return;
        }

        StopPulse();
        StopEllipsisAnimation();
        _showingError = true;
        _state = OverlayState.Hidden; // logical resting state once the error clears

        ApplyErrorVisuals(string.IsNullOrWhiteSpace(message) ? "Something went wrong" : message.Trim());
        ShowPill();

        _errorTimer.Stop();
        _errorTimer.Start();
    }

    // ============ Visual states ============

    private void ApplyRecordingVisuals()
    {
        StatusDot.Visibility = Visibility.Visible;
        StatusDot.Fill = BrushFor("DestructiveBrush", Colors.Red);
        StatusDot.Opacity = 1.0;
        PillRoot.Background = BrushFor("BackgroundBrush", Color.FromRgb(0xF8, 0xF4, 0xEC));
        PillRoot.BorderBrush = BrushFor("BorderBrush", Color.FromRgb(0xE6, 0xE2, 0xD8));

        // Recording look: dot + elapsed timer + live waveform, no status word (mockup style).
        StatusLabel.Visibility = Visibility.Collapsed;
        TimerText.Visibility = Visibility.Visible;
        TimerText.Text = "0:00";
        Waveform.Visibility = Visibility.Visible;

        _recordingStartUtc = DateTime.UtcNow;
        StartElapsedTimer();
        StartWaveform();
    }

    private void ApplyTranscribingVisuals()
    {
        StopWaveform();
        StopElapsedTimer();
        TimerText.Visibility = Visibility.Collapsed;
        Waveform.Visibility = Visibility.Collapsed;

        // A small sage dot keeps the pill balanced once recording's red dot is gone.
        StatusDot.Visibility = Visibility.Visible;
        StatusDot.Fill = BrushFor("PrimaryBrush", Color.FromRgb(0x4C, 0x8C, 0x6B));
        StatusDot.Opacity = 1.0;
        ResetDotScale();
        PillRoot.Background = BrushFor("BackgroundBrush", Color.FromRgb(0xF8, 0xF4, 0xEC));
        PillRoot.BorderBrush = BrushFor("BorderBrush", Color.FromRgb(0xE6, 0xE2, 0xD8));
        StatusLabel.Visibility = Visibility.Visible;
        StatusLabel.Foreground = BrushFor("ForegroundBrush", Color.FromRgb(0x21, 0x26, 0x2E));
        StatusLabel.Text = "Transcribing";

        // The "in progress" cue (PRD §8.10 polish): play once on entry to Transcribing.
        SoundCue.PlayTranscribing();
    }

    private void ApplyErrorVisuals(string message)
    {
        StopWaveform();
        StopElapsedTimer();
        TimerText.Visibility = Visibility.Collapsed;
        Waveform.Visibility = Visibility.Collapsed;

        // Red pill — destructive background, white text. Opens settings on click is a controller
        // concern (the idle/active overlay states there); here we just surface the message briefly.
        StatusDot.Visibility = Visibility.Collapsed;
        StatusDot.Opacity = 1.0;
        ResetDotScale();
        PillRoot.Background = BrushFor("DestructiveBrush", Color.FromRgb(0xDC, 0x2C, 0x2C));
        PillRoot.BorderBrush = BrushFor("DestructiveBrush", Color.FromRgb(0xDC, 0x2C, 0x2C));
        StatusLabel.Visibility = Visibility.Visible;
        StatusLabel.Foreground = BrushFor("PrimaryForegroundBrush", Colors.White);
        StatusLabel.Text = message;
    }

    // ============ Live waveform + elapsed timer ============

    private void BuildWaveformBars()
    {
        var bar = BrushFor("PrimaryBrush", Color.FromRgb(0x4C, 0x8C, 0x6B));
        for (int i = 0; i < BarCount; i++)
        {
            var rect = new Rectangle
            {
                Width = 2.5,
                Height = 3,
                RadiusX = 1.25,
                RadiusY = 1.25,
                Margin = new Thickness(1.25, 0, 1.25, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = bar,
            };
            _bars.Add(rect);
            Waveform.Children.Add(rect);
        }
    }

    private void StartWaveform()
    {
        _waveTimer ??= new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(55),
        };
        _waveTimer.Tick -= OnWaveTick;
        _waveTimer.Tick += OnWaveTick;
        _waveTimer.Start();
    }

    private void StopWaveform()
    {
        _waveTimer?.Stop();
        // Settle the bars flat so a re-show doesn't flash a stale shape.
        foreach (var b in _bars) b.Height = 3;
    }

    private void OnWaveTick(object? sender, EventArgs e)
    {
        // Two travelling sine components give an organic, lively shape (not a flat scroll).
        _wavePhase += 0.45;
        const double min = 3, max = 13;
        for (int i = 0; i < _bars.Count; i++)
        {
            double wave = 0.5 * (1 + Math.Sin(_wavePhase + i * 0.55))
                        * 0.7
                        + 0.3 * (0.5 * (1 + Math.Sin(_wavePhase * 1.7 + i * 0.9)));
            _bars[i].Height = min + (max - min) * Math.Clamp(wave, 0, 1);
        }
    }

    private void StartElapsedTimer()
    {
        _elapsedTimer ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _elapsedTimer.Tick -= OnElapsedTick;
        _elapsedTimer.Tick += OnElapsedTick;
        _elapsedTimer.Start();
        OnElapsedTick(null, EventArgs.Empty);
    }

    private void StopElapsedTimer() => _elapsedTimer?.Stop();

    private void OnElapsedTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _recordingStartUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        TimerText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    /// <summary>
    /// Resolve a brand brush by key, falling back to a hard-coded brand colour if the resource
    /// dictionary isn't merged (e.g. the window is constructed in isolation in a test host).
    /// Frozen so it's safe to assign from any access and cheap to reuse.
    /// </summary>
    private Brush BrushFor(string resourceKey, Color fallback)
    {
        if (TryFindResource(resourceKey) is Brush brush)
        {
            return brush;
        }

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }

    // ============ Animations ============

    private void StartPulse()
    {
        _pulseStoryboard ??= (Storyboard?)PillRoot.Resources["PulseStoryboard"];
        _pulseStoryboard?.Begin(this, isControllable: true);
    }

    private void StopPulse()
    {
        if (_pulseStoryboard is not null)
        {
            _pulseStoryboard.Stop(this);
        }
        StatusDot.Opacity = 1.0;
        ResetDotScale();
    }

    private void ResetDotScale()
    {
        DotScale.ScaleX = 1.0;
        DotScale.ScaleY = 1.0;
    }

    // "Transcribing" + animated ellipsis. Driven by a lightweight timer rather than a string
    // animation so the label stays a plain TextBlock and there's nothing to leak.
    private DispatcherTimer? _ellipsisTimer;
    private int _ellipsisDots;

    private void StartEllipsisAnimation()
    {
        _ellipsisDots = 0;
        _ellipsisTimer ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _ellipsisTimer.Tick -= OnEllipsisTick; // guard against double subscription
        _ellipsisTimer.Tick += OnEllipsisTick;
        _ellipsisTimer.Start();
    }

    private void StopEllipsisAnimation()
    {
        _ellipsisTimer?.Stop();
    }

    private void OnEllipsisTick(object? sender, EventArgs e)
    {
        _ellipsisDots = (_ellipsisDots + 1) % 4; // 0..3 dots
        StatusLabel.Text = "Transcribing" + new string('.', _ellipsisDots);
    }

    // ============ Positioning ============

    private void ShowPill()
    {
        // Make measurable so SizeToContent has produced a real ActualWidth/Height before we place it.
        Visibility = Visibility.Visible;
        // Defer positioning until layout has run for the current content.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionBottomCenter));
        PositionBottomCenter();
    }

    /// <summary>
    /// Bottom-centre of the primary working area (the desktop minus the taskbar), with a small
    /// margin above the taskbar. Uses WPF <see cref="SystemParameters"/> (device-independent units),
    /// not WinForms Screen, so DPI scaling is handled by WPF.
    /// </summary>
    private void PositionBottomCenter()
    {
        const double bottomMargin = 24; // gap above the taskbar

        double workLeft = SystemParameters.WorkArea.Left;
        double workTop = SystemParameters.WorkArea.Top;
        double workWidth = SystemParameters.WorkArea.Width;
        double workHeight = SystemParameters.WorkArea.Height;

        // ActualWidth/Height are only meaningful once the window has been laid out; fall back to the
        // declared minimums (+ the 12px outer margin on each side for the shadow) until then.
        double pillWidth = ActualWidth > 0 ? ActualWidth : 112 + 24;
        double pillHeight = ActualHeight > 0 ? ActualHeight : 24 + 24;

        Left = workLeft + ((workWidth - pillWidth) / 2);
        Top = workTop + workHeight - pillHeight - bottomMargin;
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea) && Visibility == Visibility.Visible)
        {
            if (Dispatcher.CheckAccess())
            {
                PositionBottomCenter();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(PositionBottomCenter));
            }
        }
    }

    // ============ Timers / lifetime ============

    private void OnErrorTimerTick(object? sender, EventArgs e)
    {
        _errorTimer.Stop();
        if (_showingError)
        {
            // Auto-hide back to the resting state (PRD §13 — error returns to Hidden).
            SetState(OverlayState.Hidden);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Detach the static handler so a closed overlay can't keep the window alive or fire after close.
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _errorTimer.Stop();
        _errorTimer.Tick -= OnErrorTimerTick;
        if (_ellipsisTimer is not null)
        {
            _ellipsisTimer.Stop();
            _ellipsisTimer.Tick -= OnEllipsisTick;
        }
        if (_waveTimer is not null)
        {
            _waveTimer.Stop();
            _waveTimer.Tick -= OnWaveTick;
        }
        if (_elapsedTimer is not null)
        {
            _elapsedTimer.Stop();
            _elapsedTimer.Tick -= OnElapsedTick;
        }
    }

    // ============ Win32 — extended window styles (focus / click-through) ============

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return;

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        // NOACTIVATE  : never take foreground/activation when shown or clicked.
        // TRANSPARENT : hit-test transparent — clicks fall through to the app underneath (click-through).
        // TOOLWINDOW  : keep the overlay out of the alt-tab list and taskbar.
        exStyle |= WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // GetWindowLongPtr/SetWindowLongPtr would be the 64-bit-clean names, but for GWL_EXSTYLE the
    // value fits in 32 bits and the user32 GetWindowLong/SetWindowLong shims forward to the Ptr
    // variants on 64-bit Windows. Kept simple and correct for the style bits we set.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
