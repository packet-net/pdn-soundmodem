using Packet.SoundModem.Audio;
using Packet.SoundModem.MultiDecode;

namespace Packet.SoundModem.TncTest;

/// <summary>One track's audio, at the rate it was recorded at, with the channel already chosen.</summary>
/// <param name="Samples">The chosen channel, normalised to -1..1.</param>
/// <param name="SampleRate">The file's own rate; resampling to a mode's DSP rate happens later.</param>
/// <param name="Channels">How many channels the file had.</param>
/// <param name="Channel">Which one this is.</param>
/// <param name="ChannelLevels">Peak level, dBFS, of every channel, in file order.</param>
/// <param name="BitsPerSample">The file's word length.</param>
/// <param name="Integrity">What the container had to say about its own decode; null when it
/// carries no such claim (a WAV never does).</param>
internal sealed record TrackAudio(
    float[] Samples,
    int SampleRate,
    int Channels,
    int Channel,
    double[] ChannelLevels,
    int BitsPerSample,
    bool? Integrity)
{
    /// <summary>Running time of the audio held here.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

/// <summary>Gets a track off disk and into one channel of float samples at one rate.</summary>
internal static class TrackReader
{
    /// <summary>
    /// Reads a FLAC or WAV track and picks a channel.
    /// </summary>
    /// <param name="path">The track file.</param>
    /// <param name="requestedChannel">Which channel to score; null picks the loudest.</param>
    /// <remarks>
    /// The loudest channel is the default for the same reason <c>pdn-decode</c> uses it: a
    /// two-channel recording with the receiver on one side only is an ordinary thing to be
    /// handed, and reading the silent side is indistinguishable from a track with nothing on it.
    /// The WA8LMF discs are dual-mono, so the choice does not matter for them and the levels are
    /// printed either way, which is how anyone would notice if a future track was not.
    /// </remarks>
    public static TrackAudio Read(string path, int? requestedChannel)
    {
        float[][] channels;
        int sampleRate;
        int bitsPerSample;
        bool? integrity;

        if (Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            FlacAudio flac = FlacReader.Read(path);
            channels = flac.Channels;
            sampleRate = flac.SampleRate;
            bitsPerSample = flac.BitsPerSample;
            integrity = flac.Md5Verified;
        }
        else
        {
            // The first read reports the channel count, which is what says whether there are
            // any more to read; only a genuinely multi-channel file costs a second pass.
            var (first, rate, count) = WavFile.ReadChannel(path);
            channels = new float[count][];
            channels[0] = first;
            for (int c = 1; c < count; c++)
            {
                channels[c] = WavFile.ReadMono(path, c).Samples;
            }

            sampleRate = rate;
            bitsPerSample = 16;                              // WavFile reads nothing else
            integrity = null;
        }

        if (channels.Length == 0 || channels[0].Length == 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} holds no audio");
        }

        var levels = new double[channels.Length];
        for (int c = 0; c < channels.Length; c++)
        {
            levels[c] = PeakDbfs(channels[c]);
        }

        int chosen = requestedChannel ?? Array.IndexOf(levels, levels.Max());
        if (chosen < 0 || chosen >= channels.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedChannel),
                $"{Path.GetFileName(path)} has {channels.Length} channels, so there is no channel {chosen}");
        }

        float[] samples = channels[chosen];

        // Let the channels we are not scoring go before the resampler asks for its own arrays:
        // a 40 minute stereo track is a quarter of a gigabyte per channel.
        for (int c = 0; c < channels.Length; c++)
        {
            if (c != chosen)
            {
                channels[c] = [];
            }
        }

        return new TrackAudio(
            samples, sampleRate, channels.Length, chosen, levels, bitsPerSample, integrity);
    }

    /// <summary>Takes the requested window out of a track, in seconds of the track's own clock.</summary>
    public static TrackAudio Slice(TrackAudio track, double skipSeconds, double? lengthSeconds)
    {
        if (skipSeconds <= 0 && lengthSeconds is null)
        {
            return track;
        }

        int from = Math.Clamp((int)(skipSeconds * track.SampleRate), 0, track.Samples.Length);
        int to = lengthSeconds is double length
            ? Math.Clamp(from + (int)(length * track.SampleRate), from, track.Samples.Length)
            : track.Samples.Length;

        return track with { Samples = track.Samples[from..to] };
    }

    /// <summary>
    /// Brings a track to a mode's DSP rate, returning the track untouched when it is already there.
    /// </summary>
    /// <remarks>
    /// The library has no resampler on purpose - a station's capture rate must be an integer
    /// multiple of its DSP rate and the daemon refuses anything else by name rather than
    /// quietly interpolating. A CD is 44100 Hz and 12000 does not divide it, so the conversion
    /// belongs here, in the tool, where it is printed in the report rather than assumed. This is
    /// the same <c>Resampler</c> <c>pdn-decode</c> uses, shared rather than copied so that a
    /// benchmark and a forensic decode of the same file cannot disagree about the audio.
    /// </remarks>
    public static float[] AtRate(TrackAudio track, int dspRate) =>
        track.SampleRate == dspRate
            ? track.Samples
            : Resampler.Resample(track.Samples, track.SampleRate, dspRate);

    private static double PeakDbfs(float[] samples)
    {
        float peak = 0;
        foreach (float sample in samples)
        {
            float magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak <= 0 ? double.NegativeInfinity : 20 * Math.Log10(peak);
    }
}
