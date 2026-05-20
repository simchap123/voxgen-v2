using VoxGen.Desktop.Tray;
using Xunit;

namespace VoxGen.Desktop.Tests.Tray;

public sealed class TrayMenuStateTests
{
    [Fact]
    public void Defaults_to_not_paused()
    {
        var state = new TrayMenuState();

        Assert.False(state.IsPaused);
        Assert.Equal("Pause", state.PauseResumeLabel);
        Assert.Equal("VoxGen", state.Tooltip);
    }

    [Fact]
    public void TogglePaused_flips_state_and_returns_new_value()
    {
        var state = new TrayMenuState();

        var first = state.TogglePaused();
        Assert.True(first);
        Assert.True(state.IsPaused);
        Assert.Equal("Resume", state.PauseResumeLabel);
        Assert.Equal("VoxGen — paused", state.Tooltip);

        var second = state.TogglePaused();
        Assert.False(second);
        Assert.False(state.IsPaused);
        Assert.Equal("Pause", state.PauseResumeLabel);
        Assert.Equal("VoxGen", state.Tooltip);
    }
}
