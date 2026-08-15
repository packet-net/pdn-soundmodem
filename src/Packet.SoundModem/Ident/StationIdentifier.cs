namespace Packet.SoundModem.Ident;

/// <summary>
/// When one modem owes a Morse station identification, and the audio to send when it does.
/// </summary>
/// <remarks>
/// <para><b>Why per modem rather than per station.</b> A station identifies on the signal it is
/// identifying. The modems on one channel can sit kilohertz apart in RF, so a single station-wide
/// ident would land on whichever audio frequency it was configured for and say nothing about the
/// others - and, worse, would usually land on top of one of them. Hanging the policy off a modem
/// lets the ident default to that modem's own centre, which is both the useful answer and the one
/// that needs no maintenance when the band plan moves.</para>
/// <para><b>An ident is owed only after transmitting.</b> Identification is a licence condition on
/// <em>transmissions</em>, so a station that has sent nothing owes nothing: keying up on a
/// ten-minute timer to announce a callsign nobody has heard transmit is pure QRM. So the clock
/// starts at the first transmission and an ident becomes due when one has happened since the last
/// identification and the interval has elapsed. This is also what a NinoTNC does - its manual
/// specifies a beacon every 9.5 minutes "while the station is transmitting" - and matching the
/// established behaviour on this network is worth more than inventing a better rule.</para>
/// <para>The first transmission of a session makes an ident due immediately, because at that point
/// the station has transmitted and never identified.</para>
/// <para>Wall clock through an injected <see cref="TimeProvider"/>, per the house rule, so the
/// ten-minute rule is provable with a <c>FakeTimeProvider</c> instead of ten minutes of test.</para>
/// </remarks>
public sealed class StationIdentifier
{
    private readonly TimeProvider _time;
    private readonly double _toneHz;
    private readonly double _wpm;
    private readonly double _amplitude;
    private readonly int _sampleRate;
    private readonly Lock _gate = new();
    private bool _transmittedSinceIdent;
    private DateTimeOffset? _lastIdentified;

    /// <param name="callsign">The station callsign, e.g. <c>M0LTE</c>.</param>
    /// <param name="modeSuffix">Optional text sent after the callsign - the mode name, so a
    /// listener who just heard an unfamiliar burst learns what it was. Null sends the callsign
    /// alone.</param>
    /// <param name="toneHz">Audio tone to key. On USB this lands at dial + this, which is why
    /// callers pass the modem's own audio centre.</param>
    /// <param name="wordsPerMinute">Sending speed.</param>
    /// <param name="interval">How long after an identification the next one may fall due.</param>
    /// <param name="sampleRate">The channel's audio rate.</param>
    /// <param name="amplitude">Key-down peak amplitude. The default matches the modulators'
    /// own 0.8, so the ident presents the transmitter with the same drive the data does and
    /// does not need its own ALC story.</param>
    /// <param name="time">The wall clock (injected; FakeTimeProvider under test).</param>
    public StationIdentifier(
        string callsign,
        string? modeSuffix,
        double toneHz,
        double wordsPerMinute,
        TimeSpan interval,
        int sampleRate,
        double amplitude = 0.8,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callsign);
        Text = MorseGenerator.IdText(callsign, modeSuffix);
        if (!MorseGenerator.CanEncode(Text))
        {
            throw new ArgumentException(
                $"'{Text}' contains characters with no Morse code", nameof(callsign));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wordsPerMinute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toneHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amplitude);
        if (toneHz >= sampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toneHz), toneHz,
                $"the ident tone must be below the {sampleRate / 2.0:F0} Hz Nyquist of a "
                + $"{sampleRate} Hz channel");
        }

        _time = time ?? TimeProvider.System;
        _toneHz = toneHz;
        _wpm = wordsPerMinute;
        _amplitude = amplitude;
        _sampleRate = sampleRate;
        Interval = interval;
    }

    /// <summary>The message that will be sent, e.g. <c>M0LTE</c> or <c>M0LTE FREEDV-DATAC1</c>.</summary>
    public string Text { get; }

    /// <summary>How long after an identification the next one may fall due.</summary>
    public TimeSpan Interval { get; }

    /// <summary>How long one identification occupies the channel.</summary>
    public double DurationSeconds => MorseGenerator.DurationSeconds(Text, _wpm);

    /// <summary>When this modem last identified, or null if it never has.</summary>
    public DateTimeOffset? LastIdentifiedUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastIdentified;
            }
        }
    }

    /// <summary>Records that this modem transmitted, which is what starts the clock.</summary>
    public void NoteTransmission()
    {
        lock (_gate)
        {
            _transmittedSinceIdent = true;
        }
    }

    /// <summary>
    /// Whether an identification is owed now: this modem has transmitted since it last
    /// identified, and either it never has or <see cref="Interval"/> has elapsed since.
    /// </summary>
    public bool IdentificationDue
    {
        get
        {
            lock (_gate)
            {
                return _transmittedSinceIdent
                    && (_lastIdentified is not DateTimeOffset last
                        || _time.GetUtcNow() - last >= Interval);
            }
        }
    }

    /// <summary>The keyed audio for one identification, at the channel's rate.</summary>
    public float[] Render() =>
        MorseGenerator.Real(Text, _toneHz, _amplitude, _wpm, _sampleRate);

    /// <summary>
    /// Records that an identification has been sent: stamps the clock and clears the debt, so
    /// nothing further is owed until this modem transmits again.
    /// </summary>
    public void NoteIdentified()
    {
        lock (_gate)
        {
            _lastIdentified = _time.GetUtcNow();
            _transmittedSinceIdent = false;
        }
    }
}
