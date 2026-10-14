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
    private static readonly TimeSpan DwellTime = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan UiaTimeout = TimeSpan.FromSeconds(2);
    private const int MoveTolerancePx = 6;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly SpeechPlayer _player;
    private readonly Func<bool> _enabled;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public HoverReadService(SpeechPlayer player, Func<bool> enabled, ILogger logger)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Ctrl+Shift held, Alt not (so Ctrl+Shift+Alt shortcuts don't trigger reading).</summary>
    private static bool TriggerHeld() => IsDown(VK_CONTROL) && IsDown(VK_SHIFT) && !IsDown(VK_MENU);

    private async Task LoopAsync(CancellationToken ct)
    {
        (int X, int Y) anchor = (int.MinValue, int.MinValue);
        var restingSince = DateTime.UtcNow;
        var readHere = false;
        string? lastSpoken = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);

                if (!_enabled())
                {
                    if (_player.State != SpeechState.Idle) _player.Stop();
                    continue;
                }

                if (!TriggerHeld())
                {
                    // Keys released: keep reading, but allow the same text to be re-triggered later.
                    lastSpoken = null;
                    readHere = false;
                    anchor = (int.MinValue, int.MinValue);
                    continue;
                }

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

                var text = await ReadTextWithTimeoutAsync(pos.X, pos.Y).ConfigureAwait(false);
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

    private static async Task<string?> ReadTextWithTimeoutAsync(int x, int y)
    {
        // UIA can hang on an unresponsive app; give up rather than freeze the loop.
        var read = Task.Run(() => ScreenTextReader.GetTextAt(x, y));
        var done = await Task.WhenAny(read, Task.Delay(UiaTimeout)).ConfigureAwait(false);
        return done == read ? read.Result : null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { /* shutting down */ }
        _stop.Dispose();
    }
}
