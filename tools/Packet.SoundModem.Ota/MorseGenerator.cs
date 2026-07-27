using System.Text;

namespace Packet.SoundModem.Ota;

/// <summary>
/// Morse station identification, rendered as a keyed complex-baseband carrier for the Flex
/// waveform IQ path.
/// </summary>
/// <remarks>
/// <para>Required once transmissions go to a real antenna: a data waveform carries no
/// human-readable identification, so the station has to say who it is in a form anyone
/// listening can decode without our software. Sending the mode name alongside the callsign
/// also tells a listener what the unfamiliar signal they just heard actually was.</para>
/// <para><b>Edges are shaped, deliberately.</b> Hard-keying a carrier splatters — the
/// transform of a rectangular envelope has sidelobes decaying at only 6 dB/octave, which on
/// the air is key clicks either side of the signal. A raised-cosine rise and fall of a few
/// milliseconds costs nothing and removes them. This matters more here than for an ordinary
/// CW station, because we are about to publish spectral measurements of our own transmitter
/// and would rather not be measuring our own ID's clicks.</para>
/// <para>Timing is the PARIS standard: one dot unit = 1.2 / wpm seconds, dash = 3 units,
/// intra-character gap = 1, inter-character gap = 3, word gap = 7.</para>
/// </remarks>
public static class MorseGenerator
{
    /// <summary>Duration of one dot at a given speed, in seconds (PARIS: 1.2/wpm).</summary>
    public static double DotSeconds(double wordsPerMinute)
    {
        if (wordsPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wordsPerMinute));
        }

        return 1.2 / wordsPerMinute;
    }

    private static readonly Dictionary<char, string> Code = new()
    {
        ['A'] = ".-", ['B'] = "-...", ['C'] = "-.-.", ['D'] = "-..", ['E'] = ".",
        ['F'] = "..-.", ['G'] = "--.", ['H'] = "....", ['I'] = "..", ['J'] = ".---",
        ['K'] = "-.-", ['L'] = ".-..", ['M'] = "--", ['N'] = "-.", ['O'] = "---",
        ['P'] = ".--.", ['Q'] = "--.-", ['R'] = ".-.", ['S'] = "...", ['T'] = "-",
        ['U'] = "..-", ['V'] = "...-", ['W'] = ".--", ['X'] = "-..-", ['Y'] = "-.--",
        ['Z'] = "--..",
        ['0'] = "-----", ['1'] = ".----", ['2'] = "..---", ['3'] = "...--", ['4'] = "....-",
        ['5'] = ".....", ['6'] = "-....", ['7'] = "--...", ['8'] = "---..", ['9'] = "----.",
        ['/'] = "-..-.", ['?'] = "..--..", ['.'] = ".-.-.-", [','] = "--..--",
        ['-'] = "-....-", ['='] = "-...-", ['+'] = ".-.-.",
    };

    /// <summary>Whether every character of <paramref name="text"/> can be sent.</summary>
    public static bool CanEncode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text.ToUpperInvariant())
        {
            if (c != ' ' && !Code.ContainsKey(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The dot/dash string for one character, or null if it cannot be sent.</summary>
    public static string? Symbols(char c) =>
        Code.TryGetValue(char.ToUpperInvariant(c), out string? s) ? s : null;

    /// <summary>
    /// The keying pattern for a message: alternating on/off intervals in seconds, always
    /// beginning with a key-down and ending with a key-up.
    /// </summary>
    public static IReadOnlyList<(bool KeyDown, double Seconds)> KeyingPattern(
        string text, double wordsPerMinute)
    {
        ArgumentNullException.ThrowIfNull(text);
        double dot = DotSeconds(wordsPerMinute);
        var pattern = new List<(bool, double)>();
        string upper = text.Trim().ToUpperInvariant();

        bool firstCharacter = true;
        foreach (string word in upper.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!firstCharacter)
            {
                // A word gap is 7 units; 3 of them were already emitted as the previous
                // character gap, so only 4 more are owed.
                Extend(pattern, false, 4 * dot);
            }

            bool firstOfWord = true;
            foreach (char c in word)
            {
                if (Symbols(c) is not string symbols)
                {
                    continue;
                }

                if (!firstCharacter && !firstOfWord)
                {
                    Extend(pattern, false, 3 * dot);
                }
                else if (!firstCharacter && firstOfWord)
                {
                    Extend(pattern, false, 3 * dot);
                }

                for (int i = 0; i < symbols.Length; i++)
                {
                    if (i > 0)
                    {
                        Extend(pattern, false, dot);
                    }

                    pattern.Add((true, symbols[i] == '-' ? 3 * dot : dot));
                }

                firstCharacter = false;
                firstOfWord = false;
            }
        }

        return pattern;

        static void Extend(List<(bool, double)> p, bool keyDown, double seconds)
        {
            if (p.Count > 0 && p[^1].Item1 == keyDown)
            {
                p[^1] = (keyDown, p[^1].Item2 + seconds);
            }
            else if (p.Count > 0 || keyDown)
            {
                p.Add((keyDown, seconds));
            }
        }
    }

    /// <summary>Total airtime of a message, in seconds.</summary>
    public static double DurationSeconds(string text, double wordsPerMinute)
    {
        double total = 0;
        foreach ((bool _, double seconds) in KeyingPattern(text, wordsPerMinute))
        {
            total += seconds;
        }

        return total;
    }

    /// <summary>
    /// Renders a message as interleaved I,Q — a carrier at <paramref name="toneHz"/> keyed by
    /// the message, with raised-cosine edges.
    /// </summary>
    /// <param name="text">The message, e.g. <c>"M0LTE MS110D"</c>.</param>
    /// <param name="toneHz">Carrier offset from the waveform centre.</param>
    /// <param name="amplitude">Key-down amplitude.</param>
    /// <param name="wordsPerMinute">Sending speed.</param>
    /// <param name="sampleRate">Complex sample rate (24000 for the Flex waveform path).</param>
    /// <param name="edgeSeconds">Rise/fall time. 5 ms is the usual click-free compromise;
    /// it must stay well under a dot (40 ms at 30 wpm).</param>
    public static float[] Complex(
        string text, double toneHz, double amplitude, double wordsPerMinute,
        int sampleRate, double edgeSeconds = 0.005)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!CanEncode(text))
        {
            throw new ArgumentException($"'{text}' contains characters with no Morse code", nameof(text));
        }

        double dot = DotSeconds(wordsPerMinute);
        if (edgeSeconds * 2 >= dot)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edgeSeconds), edgeSeconds,
                $"rise+fall must fit inside one {dot * 1000:F0} ms dot at {wordsPerMinute} wpm");
        }

        IReadOnlyList<(bool KeyDown, double Seconds)> pattern = KeyingPattern(text, wordsPerMinute);
        int total = 0;
        foreach ((bool _, double seconds) in pattern)
        {
            total += (int)Math.Round(seconds * sampleRate);
        }

        var envelope = new double[total];
        int at = 0;
        foreach ((bool keyDown, double seconds) in pattern)
        {
            int n = (int)Math.Round(seconds * sampleRate);
            if (keyDown)
            {
                int edge = (int)Math.Round(edgeSeconds * sampleRate);
                for (int k = 0; k < n; k++)
                {
                    double e = 1.0;
                    if (k < edge)
                    {
                        e = 0.5 * (1.0 - Math.Cos(Math.PI * k / edge));
                    }
                    else if (n - 1 - k < edge)
                    {
                        e = 0.5 * (1.0 - Math.Cos(Math.PI * (n - 1 - k) / edge));
                    }

                    envelope[at + k] = e;
                }
            }

            at += n;
        }

        var iq = new float[2 * total];
        double phase = 0;
        double step = 2.0 * Math.PI * toneHz / sampleRate;
        for (int k = 0; k < total; k++)
        {
            double a = amplitude * envelope[k];
            iq[2 * k] = (float)(a * Math.Cos(phase));
            iq[(2 * k) + 1] = (float)(a * Math.Sin(phase));
            phase += step;
            if (phase > Math.PI)
            {
                phase -= 2.0 * Math.PI;
            }
        }

        return iq;
    }

    /// <summary>
    /// Renders a message as mono real audio — a cosine tone at <paramref name="toneHz"/> keyed by
    /// the message, with raised-cosine edges. The DAX-audio counterpart of <see cref="Complex"/>.
    /// </summary>
    /// <remarks>On the DAX route the radio's own DIGU SSB modulator carries this audio onto the
    /// air (dial + <paramref name="toneHz"/>), so there is no complex baseband — the keyed carrier
    /// is a real tone. The shaped edges matter for exactly the same reason as the complex version:
    /// hard-keying splatters, and we are about to publish spectral measurements of our own
    /// transmitter.</remarks>
    /// <param name="text">The message, e.g. <c>"M0LTE MS110D"</c>.</param>
    /// <param name="toneHz">Audio tone frequency (lands at dial + this on DIGU).</param>
    /// <param name="amplitude">Key-down amplitude.</param>
    /// <param name="wordsPerMinute">Sending speed.</param>
    /// <param name="sampleRate">Audio sample rate (24000 for the reduced-bandwidth DAX path).</param>
    /// <param name="edgeSeconds">Rise/fall time. 5 ms is the usual click-free compromise;
    /// it must stay well under a dot (40 ms at 30 wpm).</param>
    public static float[] Real(
        string text, double toneHz, double amplitude, double wordsPerMinute,
        int sampleRate, double edgeSeconds = 0.005)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!CanEncode(text))
        {
            throw new ArgumentException($"'{text}' contains characters with no Morse code", nameof(text));
        }

        double dot = DotSeconds(wordsPerMinute);
        if (edgeSeconds * 2 >= dot)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edgeSeconds), edgeSeconds,
                $"rise+fall must fit inside one {dot * 1000:F0} ms dot at {wordsPerMinute} wpm");
        }

        IReadOnlyList<(bool KeyDown, double Seconds)> pattern = KeyingPattern(text, wordsPerMinute);
        int total = 0;
        foreach ((bool _, double seconds) in pattern)
        {
            total += (int)Math.Round(seconds * sampleRate);
        }

        var envelope = new double[total];
        int at = 0;
        foreach ((bool keyDown, double seconds) in pattern)
        {
            int n = (int)Math.Round(seconds * sampleRate);
            if (keyDown)
            {
                int edge = (int)Math.Round(edgeSeconds * sampleRate);
                for (int k = 0; k < n; k++)
                {
                    double e = 1.0;
                    if (k < edge)
                    {
                        e = 0.5 * (1.0 - Math.Cos(Math.PI * k / edge));
                    }
                    else if (n - 1 - k < edge)
                    {
                        e = 0.5 * (1.0 - Math.Cos(Math.PI * (n - 1 - k) / edge));
                    }

                    envelope[at + k] = e;
                }
            }

            at += n;
        }

        var audio = new float[total];
        double phase = 0;
        double step = 2.0 * Math.PI * toneHz / sampleRate;
        for (int k = 0; k < total; k++)
        {
            audio[k] = (float)(amplitude * envelope[k] * Math.Cos(phase));
            phase += step;
            if (phase > Math.PI)
            {
                phase -= 2.0 * Math.PI;
            }
        }

        return audio;
    }

    /// <summary>The identification message for a session: callsign then mode.</summary>
    public static string IdText(string callsign, string? mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callsign);
        var sb = new StringBuilder(callsign.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(mode))
        {
            sb.Append(' ').Append(mode.Trim().ToUpperInvariant());
        }

        return sb.ToString();
    }
}
