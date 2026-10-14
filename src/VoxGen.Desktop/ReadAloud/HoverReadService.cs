using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.ReadAloud;

/// <summary>
/// Hold Ctrl+Shift and rest the mouse on something → VoxGen reads it aloud.
///
/// A background loop polls the key state and cursor every 60 ms (no global hooks needed). When the
/// keys are held and the cursor has stayed put for <see cref="DwellTime"/>, it grabs the text under
/// the cursor via UI Automation — the selection if there is one, else the paragraph or label — and
/// hands it to the <see cref="SpeechPlayer"/>. Reading continues after the keys are released (so the
/// control bar can be used); pointing at something new with Ctrl+Shift interrupts.
/// </summary>
public sealed class HoverReadService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan DwellTime = TimeSpan.FromMilliseconds(250);
    // First UI Automation call in a process can take several seconds (COM/provider spin-up).
    private static readonly TimeSpan UiaTimeout = TimeSpan.FromSeconds(4);
    private const int MoveTolerancePx = 12;
    private static readonly TimeSpan MinTapTime = TimeSpan.FromMilliseconds(80);

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly SpeechPlayer _player;
    private readonly Func<bool> _enabled;
    private readonly Func<string> _shortcut;
    private readonly Action? _pressedWhileDisabled;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <param name="pressedWhileDisabled">Called when the shortcut is pressed but reading is off,
    /// so the user gets told why nothing happens instead of silence.</param>
    public HoverReadService(SpeechPlayer player, Func<bool> enabled, Func<string> shortcut, ILogger logger,
                            Action? pressedWhileDisabled = null)
    {
        _pressedWhileDisabled = pressedWhileDisabled;
        _shortcut = shortcut ?? throw new ArgumentNullException(nameof(shortcut));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Start()
    {
        // Warm up UI Automation so the first real hover answers quickly instead of timing out.
        _ = Task.Run(() =>
        {
            var (x, y) = ScreenTextReader.CursorPosition();
            try { ScreenTextReader.GetTextAt(x, y); } catch { /* warm-up only */ }
            try { _ = ScreenOcr.Available; } catch { /* warm-up only */ }
        });
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>The configured pair held and the third modifier not (so 3-key shortcuts don't trigger reading).</summary>
    private bool TriggerHeld() => _shortcut() == "CtrlAlt"
        ? IsDown(VK_CONTROL) && IsDown(VK_MENU) && !IsDown(VK_SHIFT)
        : IsDown(VK_CONTROL) && IsDown(VK_SHIFT) && !IsDown(VK_MENU);

    /// <summary>True if any key other than the modifiers and mouse buttons is down — i.e. the user is
    /// typing a shortcut like Ctrl+Shift+T, not asking for a read.</summary>
    private static bool OtherKeyDown()
    {
        for (var vk = 0x08; vk <= 0xFE; vk++)
        {
            if (vk is VK_SHIFT or VK_CONTROL or VK_MENU or >= 0xA0 and <= 0xA5) continue; // modifiers (incl. L/R)
            if (vk is 0x5B or 0x5C) continue;                                          // Win keys
            if (IsDown(vk)) return true;
        }
        return false;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        (int X, int Y) anchor = (int.MinValue, int.MinValue);
        var restingSince = DateTime.UtcNow;
        var readHere = false;
        string? lastSpoken = null;

        // Per-press tracking: supports both "hold and rest" and a quick tap (read on release).
        var wasHeld = false;
        var heldSince = DateTime.UtcNow;
        var otherKeyUsed = false;
        var readDuringPress = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);

                if (!_enabled())
                {
                    if (_player.State != SpeechState.Idle) _player.Stop();
                    var heldNow = TriggerHeld();
                    if (heldNow && !wasHeld) _pressedWhileDisabled?.Invoke();
                    wasHeld = heldNow;
                    continue;
                }

                var held = TriggerHeld();
                if (!held)
                {
                    if (wasHeld)
                    {
                        var heldFor = DateTime.UtcNow - heldSince;
                        _logger.Debug("Hover keys released", new()
                        {
                            ["heldMs"] = (int)heldFor.TotalMilliseconds,
                            ["readDuringPress"] = readDuringPress,
                            ["otherKeyUsed"] = otherKeyUsed,
                        });

                        // Quick tap while pointing: read on release (unless it was part of a shortcut).
                        if (!readDuringPress && !otherKeyUsed && heldFor >= MinTapTime)
                        {
                            var pos0 = ScreenTextReader.CursorPosition();
                            var (_, tapText) = await ReadTextWithTimeoutAsync(pos0.X, pos0.Y).ConfigureAwait(false);
                            if (tapText is not null) _player.Speak(tapText);
                        }
                    }

                    // Keys released: keep reading, but allow the same text to be re-triggered later.
                    wasHeld = false;
                    lastSpoken = null;
                    readHere = false;
                    anchor = (int.MinValue, int.MinValue);
                    continue;
                }

                if (!wasHeld)
                {
                    wasHeld = true;
                    heldSince = DateTime.UtcNow;
                    otherKeyUsed = false;
                    readDuringPress = false;
                    _logger.Debug("Hover keys down");
                }
                if (!otherKeyUsed && OtherKeyDown()) otherKeyUsed = true;
                if (otherKeyUsed) continue;

                var pos = ScreenTextReader.CursorPosition();
                if (Math.Abs(pos.X - anchor.X) > MoveTolerancePx || Math.Abs(pos.Y - anchor.Y) > MoveTolerancePx)
                {
                    anchor = pos;
                    restingSince = DateTime.UtcNow;
                    readHere = false;
                    continue;
                }

                if (readHere || DateTime.UtcNow - restingSince < DwellTime) continue;
                readHere = true;

                var (timedOut, text) = await ReadTextWithTimeoutAsync(pos.X, pos.Y).ConfigureAwait(false);
                if (timedOut)
                {
                    // Slow target app: try again on the next poll while the keys are still held.
                    readHere = false;
                    restingSince = DateTime.UtcNow;
                    _logger.Debug("Hover read: text lookup timed out; retrying");
                    continue;
                }
                readDuringPress = true;
                if (text is null || text == lastSpoken) continue;

                lastSpoken = text;
                _player.Speak(text);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning("Hover-read loop error", new() { ["error"] = ex.Message });
            }
        }
    }

    private async Task<(bool TimedOut, string? Text)> ReadTextWithTimeoutAsync(int x, int y)
    {
        // UIA can hang on an unresponsive app; give up rather than freeze the loop.
        var read = Task.Run(() => ScreenTextReader.GetTextAt(x, y));
        var done = await Task.WhenAny(read, Task.Delay(UiaTimeout)).ConfigureAwait(false);
        if (done != read) return (true, null);

        var uia = read.Result;
        _logger.Debug("Hover lookup", new()
        {
            ["x"] = x, ["y"] = y, ["source"] = uia.Source, ["weak"] = uia.Weak,
            ["chars"] = uia.Text?.Length ?? 0,
            ["preview"] = uia.Text is null ? null : uia.Text[..Math.Min(60, uia.Text.Length)],
        });
        if (uia.Text is not null && !uia.Weak) return (false, uia.Text);

        // App doesn't expose its text (or only a window title): read the pixels with Windows OCR.
        try
        {
            var ocr = await ScreenOcr.ReadAroundAsync(x, y).ConfigureAwait(false);
            var cleaned = ScreenTextReader.Clean(ocr);
            _logger.Debug("Hover OCR", new() { ["result"] = ScreenOcr.LastDiagnostics });
            if (cleaned is not null)
            {
                _logger.Debug("Hover read via OCR", new() { ["chars"] = cleaned.Length });
                return (false, cleaned);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("OCR fallback failed", new() { ["error"] = ex.Message });
        }
        return (false, uia.Text);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { /* shutting down */ }
        _stop.Dispose();
    }
}
