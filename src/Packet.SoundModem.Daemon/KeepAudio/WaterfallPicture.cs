namespace Packet.SoundModem.Daemon.KeepAudio;

/// <summary>
/// A waterfall of a kept window as a PNG: time runs left to right, audio frequency bottom to top,
/// with a strip along the top marking every stretch the station called busy.
/// </summary>
/// <remarks>
/// <para><b>Layout.</b> One column per <see cref="ColumnSeconds"/>. The top
/// <see cref="StripHeight"/> rows are the carrier-sense strip: red where the station said busy,
/// blue where it was transmitting itself, dark where it said clear, grey where nothing was
/// sampled. Below a one-row gap is the spectrum, 0 Hz at the bottom to half the sample rate at the
/// top in <see cref="Bins"/> rows. Along the bottom, a tick every 10 s and a taller one every
/// minute from the window's start. No text: the times are in the CSV beside it.</para>
/// <para><b>Brightness</b> is relative to the window itself: dark at a little above its median
/// level and white at its loudest, so a quiet band and a loud one both show their structure.</para>
/// </remarks>
internal static class WaterfallPicture
{
    /// <summary>Seconds of audio per column.</summary>
    internal const double ColumnSeconds = 0.25;

    /// <summary>Rows of the busy strip.</summary>
    internal const int StripHeight = 12;

    /// <summary>Spectrum rows, half the transform length.</summary>
    internal const int Bins = 256;

    /// <summary>Rows of the time axis.</summary>
    internal const int AxisHeight = 6;

    private const int FftSize = Bins * 2;

    internal static readonly (byte R, byte G, byte B) BusyColour = (220, 40, 40);
    internal static readonly (byte R, byte G, byte B) TransmittingColour = (60, 120, 255);
    internal static readonly (byte R, byte G, byte B) ClearColour = (25, 60, 25);
    internal static readonly (byte R, byte G, byte B) UnsampledColour = (90, 90, 90);

    /// <summary>The picture's height in pixels, whatever the window.</summary>
    internal const int Height = StripHeight + 1 + Bins + AxisHeight;

    /// <summary>Draws the window and encodes it.</summary>
    /// <param name="audio">The window's audio.</param>
    /// <param name="sampleRate">Its rate.</param>
    /// <param name="from">When its first sample was.</param>
    /// <param name="samples">The carrier-sense samples over the window.</param>
    public static byte[] Render(
        float[] audio, int sampleRate, DateTimeOffset from, IReadOnlyList<DetectorSample> samples)
    {
        double seconds = (double)audio.Length / sampleRate;
        int width = Math.Max(1, (int)Math.Ceiling(seconds / ColumnSeconds));
        var rgb = new byte[width * Height * 3];

        float[] levels = Spectrum(audio, sampleRate, width);
        (float floor, float top) = Range(levels);

        for (int x = 0; x < width; x++)
        {
            DateTimeOffset start = from + TimeSpan.FromSeconds(x * ColumnSeconds);
            DateTimeOffset end = start + TimeSpan.FromSeconds(ColumnSeconds);
            (byte R, byte G, byte B) mark = StripColour(samples, start, end);
            for (int y = 0; y < StripHeight; y++)
            {
                Put(rgb, width, x, y, mark);
            }

            for (int bin = 0; bin < Bins; bin++)
            {
                float level = levels[x * Bins + bin];
                double t = Math.Clamp((level - floor) / (top - floor), 0, 1);
                Put(rgb, width, x, StripHeight + 1 + (Bins - 1 - bin), Palette(t));
            }

            // Ticks on the column where each 10 s and each minute begins.
            int second = (int)Math.Floor(x * ColumnSeconds);
            bool startsSecond = (int)Math.Floor((x - 1) * ColumnSeconds) != second || x == 0;
            if (startsSecond && second % 10 == 0)
            {
                int tall = second % 60 == 0 ? AxisHeight : AxisHeight / 2;
                for (int y = Height - tall; y < Height; y++)
                {
                    Put(rgb, width, x, y, (230, 230, 230));
                }
            }
        }

        return PngWriter.Encode(width, Height, rgb);
    }

    /// <summary>The strip's colour for one column: busy wins, then our own transmission.</summary>
    internal static (byte R, byte G, byte B) StripColour(
        IReadOnlyList<DetectorSample> samples, DateTimeOffset start, DateTimeOffset end)
    {
        bool any = false;
        bool transmitting = false;
        foreach (DetectorSample s in samples)
        {
            if (s.At < start || s.At >= end)
            {
                continue;
            }

            any = true;
            if (s.Transmitting)
            {
                transmitting = true;
            }
            else if (s.Busy)
            {
                return BusyColour;
            }
        }

        return transmitting ? TransmittingColour : any ? ClearColour : UnsampledColour;
    }

    private static float[] Spectrum(float[] audio, int sampleRate, int width)
    {
        var levels = new float[width * Bins];
        var re = new float[FftSize];
        var im = new float[FftSize];
        var window = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (FftSize - 1));
        }

        for (int x = 0; x < width; x++)
        {
            long centre = (long)((x + 0.5) * ColumnSeconds * sampleRate);
            long first = centre - FftSize / 2;
            for (int i = 0; i < FftSize; i++)
            {
                long at = first + i;
                re[i] = at >= 0 && at < audio.Length ? audio[at] * window[i] : 0;
                im[i] = 0;
            }

            M0LTE.Dsp.Fft.Forward(re, im);
            for (int bin = 0; bin < Bins; bin++)
            {
                levels[x * Bins + bin] = 10 * MathF.Log10(re[bin] * re[bin] + im[bin] * im[bin] + 1e-20f);
            }
        }

        return levels;
    }

    private static (float Floor, float Top) Range(float[] levels)
    {
        float[] sorted = (float[])levels.Clone();
        Array.Sort(sorted);
        float median = sorted[sorted.Length / 2];
        float top = sorted[^1];
        float floor = median + 3;
        return top - floor < 20 ? (floor, floor + 20) : (floor, top);
    }

    /// <summary>Black through blue, cyan and yellow to white.</summary>
    private static (byte R, byte G, byte B) Palette(double t)
    {
        ReadOnlySpan<(double At, int R, int G, int B)> stops =
        [
            (0.00, 0, 0, 0),
            (0.25, 0, 0, 160),
            (0.50, 0, 190, 220),
            (0.75, 250, 230, 0),
            (1.00, 255, 255, 255),
        ];
        for (int i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].At)
            {
                double f = (t - stops[i - 1].At) / (stops[i].At - stops[i - 1].At);
                return (
                    (byte)(stops[i - 1].R + f * (stops[i].R - stops[i - 1].R)),
                    (byte)(stops[i - 1].G + f * (stops[i].G - stops[i - 1].G)),
                    (byte)(stops[i - 1].B + f * (stops[i].B - stops[i - 1].B)));
            }
        }

        return (255, 255, 255);
    }

    private static void Put(byte[] rgb, int width, int x, int y, (byte R, byte G, byte B) c)
    {
        int at = (y * width + x) * 3;
        rgb[at] = c.R;
        rgb[at + 1] = c.G;
        rgb[at + 2] = c.B;
    }
}
