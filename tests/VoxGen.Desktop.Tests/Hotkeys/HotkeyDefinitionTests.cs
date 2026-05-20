using System;
using VoxGen.Desktop.Hotkeys;
using Xunit;

namespace VoxGen.Desktop.Tests.Hotkeys;

public sealed class HotkeyDefinitionTests
{
    [Fact]
    public void Parse_modifier_only_RightAlt_sets_VK_zero()
    {
        var hk = HotkeyDefinition.Parse("RightAlt");

        Assert.Equal(HotkeyModifiers.Alt, hk.Modifiers);
        Assert.Equal((uint)0, hk.VirtualKeyCode);
        Assert.True(hk.IsModifierOnly);
        Assert.Equal("RightAlt", hk.ToString());
    }

    [Fact]
    public void Parse_modifier_only_LeftAlt_sets_VK_zero()
    {
        var hk = HotkeyDefinition.Parse("LeftAlt");

        Assert.Equal(HotkeyModifiers.Alt, hk.Modifiers);
        Assert.Equal((uint)0, hk.VirtualKeyCode);
        Assert.True(hk.IsModifierOnly);
        Assert.Equal("LeftAlt", hk.ToString());
    }

    [Fact]
    public void Parse_Ctrl_Shift_Space_resolves_modifiers_and_main_key()
    {
        var hk = HotkeyDefinition.Parse("Ctrl+Shift+Space");

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, hk.Modifiers);
        Assert.Equal((uint)0x20, hk.VirtualKeyCode); // VK_SPACE
        Assert.False(hk.IsModifierOnly);
        Assert.Equal("Ctrl+Shift+Space", hk.ToString());
    }

    [Fact]
    public void Parse_Win_Alt_V_handles_letter_and_multiple_modifiers()
    {
        var hk = HotkeyDefinition.Parse("Win+Alt+V");

        Assert.Equal(HotkeyModifiers.Win | HotkeyModifiers.Alt, hk.Modifiers);
        Assert.Equal((uint)'V', hk.VirtualKeyCode);
        // Canonical emission order is Ctrl, Shift, Alt, Win.
        Assert.Equal("Alt+Win+V", hk.ToString());
    }

    [Fact]
    public void Parse_F13_handles_function_key()
    {
        var hk = HotkeyDefinition.Parse("F13");

        Assert.Equal(HotkeyModifiers.None, hk.Modifiers);
        Assert.Equal((uint)(0x70 + 12), hk.VirtualKeyCode);
        Assert.Equal("F13", hk.ToString());
    }

    [Fact]
    public void Parse_is_case_insensitive()
    {
        var hk = HotkeyDefinition.Parse("ctrl+SHIFT+space");

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, hk.Modifiers);
        Assert.Equal((uint)0x20, hk.VirtualKeyCode);
        Assert.Equal("Ctrl+Shift+Space", hk.ToString());
    }

    [Fact]
    public void Parse_is_lenient_on_whitespace()
    {
        var hk = HotkeyDefinition.Parse("  Ctrl + Shift + Space  ");

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, hk.Modifiers);
        Assert.Equal((uint)0x20, hk.VirtualKeyCode);
        Assert.Equal("Ctrl+Shift+Space", hk.ToString());
    }

    [Fact]
    public void Parse_Cmd_aliases_to_Win()
    {
        var cmd = HotkeyDefinition.Parse("Cmd+A");
        var win = HotkeyDefinition.Parse("Win+A");

        Assert.Equal(win.Modifiers, cmd.Modifiers);
        Assert.Equal(win.VirtualKeyCode, cmd.VirtualKeyCode);
        Assert.Equal(win.ToString(), cmd.ToString());
    }

    [Fact]
    public void Parse_Windows_aliases_to_Win()
    {
        var w1 = HotkeyDefinition.Parse("Windows+A");
        var w2 = HotkeyDefinition.Parse("Win+A");

        Assert.Equal(w2.Modifiers, w1.Modifiers);
        Assert.Equal(w2.VirtualKeyCode, w1.VirtualKeyCode);
    }

    [Fact]
    public void Parse_Control_aliases_to_Ctrl()
    {
        var c1 = HotkeyDefinition.Parse("Control+Shift+Space");
        var c2 = HotkeyDefinition.Parse("Ctrl+Shift+Space");

        Assert.Equal(c2.Modifiers, c1.Modifiers);
        Assert.Equal(c2.ToString(), c1.ToString());
    }

    [Fact]
    public void Parse_invalid_throws_FormatException()
    {
        var ex = Assert.Throws<FormatException>(() => HotkeyDefinition.Parse("NotAKey"));
        Assert.Contains("NotAKey", ex.Message);
    }

    [Fact]
    public void Parse_empty_throws_FormatException()
    {
        Assert.Throws<FormatException>(() => HotkeyDefinition.Parse(""));
        Assert.Throws<FormatException>(() => HotkeyDefinition.Parse("   "));
    }

    [Fact]
    public void Parse_two_main_keys_throws_FormatException()
    {
        Assert.Throws<FormatException>(() => HotkeyDefinition.Parse("Ctrl+A+B"));
    }

    [Fact]
    public void Parse_duplicate_modifier_throws_FormatException()
    {
        Assert.Throws<FormatException>(() => HotkeyDefinition.Parse("Ctrl+Ctrl+A"));
    }

    [Fact]
    public void Parse_sided_modifier_with_extra_modifier_throws_FormatException()
    {
        // "Ctrl+RightAlt" is nonsense — sided modifier must stand alone.
        Assert.Throws<FormatException>(() => HotkeyDefinition.Parse("Ctrl+RightAlt"));
    }

    [Theory]
    [InlineData("RightAlt")]
    [InlineData("LeftAlt")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("Win+Alt+V")]
    [InlineData("F13")]
    [InlineData("ctrl+SHIFT+space")]
    [InlineData("  Ctrl + Shift + Space  ")]
    [InlineData("Cmd+A")]
    public void ToString_round_trips_through_Parse(string input)
    {
        var first = HotkeyDefinition.Parse(input);
        var serialized = first.ToString();
        var second = HotkeyDefinition.Parse(serialized);

        Assert.Equal(first.Modifiers, second.Modifiers);
        Assert.Equal(first.VirtualKeyCode, second.VirtualKeyCode);
        Assert.Equal(first.ToString(), second.ToString());
    }

    [Fact]
    public void TryParse_returns_false_on_invalid()
    {
        Assert.False(HotkeyDefinition.TryParse("NotAKey", out var def));
        Assert.Null(def);
    }

    [Fact]
    public void TryParse_returns_true_on_valid()
    {
        Assert.True(HotkeyDefinition.TryParse("Ctrl+Shift+Space", out var def));
        Assert.NotNull(def);
        Assert.Equal("Ctrl+Shift+Space", def!.ToString());
    }
}
