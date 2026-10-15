using System;

namespace VoxGen.Desktop.ReadAloud;

/// <summary>
/// Speech-quality speed change without a pitch change (WSOLA: waveform-similarity overlap-add).
/// Cuts the voice into short overlapping windows and lays them back down closer together (faster)
/// or further apart (slower), nudging each window to the spot where its waveform lines up with the
/// previous one so there's no phasing or warble. Works on a whole chunk in memory — read-aloud
/// chunks are at most a few seconds of mono audio.
/// </summary>
public static class TimeStretch
{
    /// <summary>Returns <paramref name="input"/>[<paramref name="start"/>..] played at <paramref name="rate"/>× (mono).</summary>
    public static float[] Stretch(float[] input, int start, int sampleRate, double rate)
    {
        start = Math.Clamp(start, 0, input.Length);
        var length = input.Length - start;
        if (length == 0) return Array.Empty<float>();
        if (Math.Abs(rate - 1.0) < 0.001)
        {
            var copy = new float[length];
            Array.Copy(input, start, copy, 0, length);
            return copy;
        }

        var win = Math.Max(64, (int)(sampleRate * 0.030)) & ~1;   // 30 ms windows
        var hopOut = win / 2;                                        // 50% overlap, Hann sums to 1
        var tolerance = (int)(sampleRate * 0.010);                   // search ±10 ms for the best fit
        var hopIn = hopOut * rate;

        var window = new float[win];
        for (var i = 0; i < win; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / win));

        var outLength = (int)(length / rate) + win;
        var output = new float[outLength];
        var prevPos = start;  // where the previous window was taken from
        var frames = (int)((length - win) / hopIn);

        for (var k = 0; k <= Math.Max(0, frames); k++)
        {
            var nominal = start + (int)Math.Round(k * hopIn);
            var pos = k == 0 ? nominal : BestMatch(input, prevPos + hopOut, nominal, tolerance, hopOut);
            var outPos = k * hopOut;
            for (var i = 0; i < win && pos + i < input.Length && outPos + i < outLength; i++)
            {
                output[outPos + i] += input[pos + i] * window[i];
            }
            prevPos = pos;
        }

        var used = Math.Min(outLength, Math.Max(0, frames) * hopOut + win);
        if (used == outLength) return output;
        var trimmed = new float[used];
        Array.Copy(output, trimmed, used);
        return trimmed;
    }

    /// <summary>The offset near <paramref name="nominal"/> whose waveform best continues the audio
    /// at <paramref name="natural"/> (cross-correlation, every 2nd sample for speed).</summary>
    private static int BestMatch(float[] input, int natural, int nominal, int tolerance, int compare)
    {
        var best = nominal;
        var bestScore = double.NegativeInfinity;
        var lo = Math.Max(0, nominal - tolerance);
        var hi = Math.Min(input.Length - compare - 1, nominal + tolerance);
        if (natural + compare >= input.Length || hi < lo) return Math.Clamp(nominal, 0, Math.Max(0, input.Length - 1));

        for (var cand = lo; cand <= hi; cand++)
        {
            double dot = 0, energy = 1e-9;
            for (var i = 0; i < compare; i += 2)
            {
                var c = input[cand + i];
                dot += c * input[natural + i];
                energy += c * c;
            }
            var score = dot / Math.Sqrt(energy);
            if (score > bestScore) { bestScore = score; best = cand; }
        }
        return best;
    }
}
