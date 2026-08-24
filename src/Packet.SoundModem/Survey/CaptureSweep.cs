using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Survey;

/// <summary>One reading of a capture: a frame, and how it was read.</summary>
/// <param name="Mode">The catalogue mode that read it.</param>
/// <param name="CentreHz">The audio centre it was read at.</param>
/// <param name="Frame">The frame bytes.</param>
/// <param name="Quality">The receiver's own diagnostics.</param>
/// <param name="Source">Source callsign, where the frame yields one.</param>
/// <param name="Destination">Destination callsign, same.</param>
public sealed record CaptureReading(
    string Mode,
    double CentreHz,
    byte[] Frame,
    FrameQuality Quality,
    string? Source,
    string? Destination);

/// <summary>
/// Reads one survey capture with every mode that could plausibly have carried it, pointed at
/// the centre the survey measured.
/// </summary>
/// <remarks>
/// <para>
/// The narrow, station-side cousin of the <c>pdn-decode</c> tool. Where that sweeps the whole
/// catalogue over an unlabelled file and is allowed to take twenty seconds about it, this runs
/// unattended beside a live receiver and has to be cheap: one centre (the survey measured it),
/// one DSP rate (the station's own), and only the modes that could be what the capture holds.
/// </para>
/// <para>
/// <b>Only the station's own DSP rate.</b> A 12 kHz station's captures are 12 kHz, and this
/// deliberately does not resample them to try the 48 kHz modes: a station that cannot run a mode
/// gains nothing from being told it heard one, and resampling every capture twice is most of the
/// cost for the half of the catalogue the answer can never be.
/// </para>
/// <para>
/// <b>Not the HF data waveforms.</b> They are most of the running time of a full sweep and they
/// are not what an unread packet burst on a packet channel turns out to be. A station wanting
/// that answer has <c>pdn-decode</c> and no deadline.
/// </para>
/// </remarks>
public static class CaptureSweep
{
    /// <summary>Modes worth trying against a capture at <paramref name="dspRate"/>.</summary>
    /// <remarks>
    /// Stated as an exclusion so a mode added to the catalogue joins automatically - the safe
    /// direction, since the failure mode here is a station never being told about traffic it
    /// could read. The baseband <c>fsk*</c>/<c>c4fsk*</c> family is included and simply ignores
    /// the centre, occupying DC upwards.
    /// </remarks>
    public static IReadOnlyList<string> ModesFor(int dspRate)
    {
        var modes = new List<string>();
        foreach (string mode in ModemCatalog.KnownModes)
        {
            if (ModemCatalog.DspRateFor(mode) == dspRate && IsPacketMode(mode))
            {
                modes.Add(mode);
            }
        }

        return modes;
    }

    /// <summary>Whether a mode is packet-radio lineage rather than an HF data waveform.</summary>
    public static bool IsPacketMode(string mode) =>
        !mode.StartsWith("freedv-", StringComparison.Ordinal)
        && !mode.StartsWith("ms110d-", StringComparison.Ordinal);

    /// <summary>
    /// Runs <paramref name="modes"/> over <paramref name="audio"/> at <paramref name="centreHz"/>
    /// and returns everything any of them read.
    /// </summary>
    /// <param name="audio">The capture, at <paramref name="dspRate"/>.</param>
    /// <param name="dspRate">Its sample rate, which must be a rate the modes run at.</param>
    /// <param name="centreHz">Where the survey measured the signal.</param>
    /// <param name="modes">What to try; <see cref="ModesFor"/> by default.</param>
    /// <param name="shouldStop">Polled between modes so a shutting-down station stops promptly
    /// rather than finishing a sweep nobody will read.</param>
    public static IReadOnlyList<CaptureReading> Run(
        ReadOnlySpan<float> audio,
        int dspRate,
        double centreHz,
        IReadOnlyList<string> modes,
        Func<bool>? shouldStop = null)
    {
        ArgumentNullException.ThrowIfNull(modes);

        // Half a second of silence, so a capture that ends flush with its last closing flag
        // still flushes that frame out of the demodulator's pipeline. A live stream never ends,
        // so no modem does this for itself - and a survey capture ends by construction.
        var samples = new float[audio.Length + (dspRate / 2)];
        audio.CopyTo(samples);

        var readings = new List<CaptureReading>();
        foreach (string mode in modes)
        {
            if (shouldStop?.Invoke() == true)
            {
                break;
            }

            RunOne(mode, samples, dspRate, centreHz, readings);
        }

        return readings;
    }

    private static void RunOne(
        string mode, float[] samples, int dspRate, double centreHz, List<CaptureReading> readings)
    {
        IModem? modem = null;
        try
        {
            // A mode with no centre to point occupies DC upwards and refuses one (issue #39);
            // it is run where it lives, which is everywhere.
            var options = ModemCatalog.AcceptsCentreFrequency(mode)
                ? new ModemOptions(CentreFrequencyHz: centreHz)
                : default;

            // The sink is required and deliberately ignored: FrameDecoded is the honest source,
            // being a superset that includes the frame an IL2P+CRC receiver read and would not
            // have handed to a host. That frame is exactly the interesting one here - it is the
            // evidence that this station is running the wrong framing rather than the wrong
            // frequency.
            modem = ModemCatalog.Create(mode, dspRate, _ => { }, options);
            modem.FrameDecoded += (frame, quality) =>
            {
                bool addressed = Waterfall.Ax25AddressParser.TryParse(
                    frame, out string source, out string destination);
                readings.Add(new CaptureReading(
                    mode,
                    centreHz,
                    frame,
                    quality,
                    addressed ? source : null,
                    addressed && destination.Length > 0 ? destination : null));
            };

            // In blocks, because a diversity bank compares its branches at chunk boundaries and
            // behaves as it does on the air only when fed something like air-sized pieces.
            const int block = 4096;
            for (int offset = 0; offset < samples.Length; offset += block)
            {
                modem.Process(samples.AsSpan(offset, Math.Min(block, samples.Length - offset)));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Too wide to sit where it was pointed. Arithmetic, not a fault.
        }
#pragma warning disable CA1031 // one broken mode must not take the sweep down with it
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            (modem as IDisposable)?.Dispose();
        }
    }
}
