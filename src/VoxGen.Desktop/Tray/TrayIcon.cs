using System;
using System.Drawing;
using WinForms = System.Windows.Forms;

namespace VoxGen.Desktop.Tray;

// Brand icon loader lives at the bottom of the file (LoadBrandIcon).

/// <summary>
/// Thin wrapper over <see cref="WinForms.NotifyIcon"/> that exposes the tray surface
/// as events for the rest of the app to consume — PRD §8.1.
///
/// Lifetime: owned by <c>App</c>, disposed on shutdown. Window lifetime is *not*
/// owned here — the tray fires <see cref="ShowSettingsRequested"/> and lets the app
/// decide what to do.
///
/// Icon: no branded asset exists yet, so we ship with <c>SystemIcons.Application</c>
/// as a placeholder. The branded .ico lands as a Resource build action in a later slice
/// (PRD §15 — branding is VoxGen everywhere).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ToolStripMenuItem _pauseItem;
    private readonly TrayMenuState _state = new();
    private readonly Icon _brandIcon;
    private bool _disposed;

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? PauseToggleRequested;
    public event EventHandler? QuitRequested;

    /// <summary>Current paused state (the menu's check mark reflects this).</summary>
    public bool IsPaused => _state.IsPaused;

    public TrayIcon()
    {
        _brandIcon = LoadBrandIcon();
        var menu = new WinForms.ContextMenuStrip();

        var openItem = new WinForms.ToolStripMenuItem("Open Settings");
        openItem.Click += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);

        _pauseItem = new WinForms.ToolStripMenuItem(_state.PauseResumeLabel) { CheckOnClick = false };
        _pauseItem.Click += (_, _) =>
        {
            _state.TogglePaused();
            RefreshPauseItem();
            PauseToggleRequested?.Invoke(this, EventArgs.Empty);
        };

        var quitItem = new WinForms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(quitItem);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _brandIcon,
            Text = _state.Tooltip,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the hover tooltip on the tray icon (NotifyIcon truncates to 63 chars).</summary>
    public void SetTooltip(string text)
    {
        ThrowIfDisposed();
        _notifyIcon.Text = text ?? string.Empty;
    }

    /// <summary>
    /// Shows a non-blocking balloon — used by background errors (PRD §13) so the user is
    /// informed without focus being yanked.
    /// </summary>
    public void ShowBalloon(string title, string body)
    {
        ThrowIfDisposed();
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = body;
        _notifyIcon.ShowBalloonTip(timeout: 4000);
    }

    private void RefreshPauseItem()
    {
        _pauseItem.Text = _state.PauseResumeLabel;
        _pauseItem.Checked = _state.IsPaused;
        _notifyIcon.Text = _state.Tooltip;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TrayIcon));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _brandIcon.Dispose();
    }

    /// <summary>Loads the branded VoxGen icon from the WPF resource; falls back to the system icon.</summary>
    private static Icon LoadBrandIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/voxgen.ico");
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info?.Stream is { } stream)
            {
                using (stream)
                {
                    return new Icon(stream, new Size(32, 32));
                }
            }
        }
        catch
        {
            // fall through to the system icon
        }
        return (Icon)SystemIcons.Application.Clone();
    }
}
