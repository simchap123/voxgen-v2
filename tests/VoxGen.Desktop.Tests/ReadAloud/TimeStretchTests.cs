using System;
using System.Linq;
using VoxGen.Desktop.ReadAloud;
using Xunit;

namespace VoxGen.Desktop.Tests.ReadAloud;

public class TimeStretchTests
{
    private const int Rate = 22050;

    private static float[] Tone(double hz, double seconds) =>
        Enumerable.Range(0, (int)(Rate * seconds)).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();

    /// <summary>Upward zero crossings per second ≈ the tone's frequency.</summary>
    private static double Pitch(float[] s, int from, int to)
    {
        var crossings = 0;
        for (var i = from + 1; i < to; i++) if (s[i - 1] < 0 && s[i] >= 0) crossings++;
        return crossings / ((to - from) / (double)Rate);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    public void Stretch_ChangesLength_KeepsPitch(double speed)
    {
        var input = Tone(220, 3);

        var output = TimeStretch.Stretch(input, 0, Rate, speed);

        Assert.InRange(output.Length, input.Length / speed * 0.95, input.Length / speed * 1.05);
        // Away from the fade-in/out edges the pitch is still 220 Hz.
        var pitch = Pitch(output, output.Length / 5, output.Length * 4 / 5);
        Assert.InRange(pitch, 210, 230);
    }

    [Fact]
    public void Stretch_IsSmooth_NoClicksOrDropouts()
    {
        var input = Tone(220, 2);
        var output = TimeStretch.Stretch(input, 0, Rate, 2.0);

        // A clean stretch of a steady tone keeps a steady level: no window seams poking holes in it.
        for (var block = 1; block < output.Length / 1000 - 1; block++)
        {
            var peak = output.Skip(block * 1000).Take(1000).Max(Math.Abs);
            Assert.InRange(peak, 0.4f, 0.6f);
        }
    }

    [Fact]
    public void Stretch_FromMiddle_StartsThere_AndRateOneIsACopy()
    {
        var input = Tone(220, 2);
        Assert.Equal(input.Length - 1000, TimeStretch.Stretch(input, 1000, Rate, 1.0).Length);
        Assert.Empty(TimeStretch.Stretch(input, input.Length, Rate, 2.0));
    }
}
