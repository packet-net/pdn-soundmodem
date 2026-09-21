namespace Packet.SoundModem.Modems.OfdmFm;

/// <summary>
/// The carrier layout of one OFDM-FM geometry: which bins a symbol occupies, and how many of them
/// are pilots. What a burst header's geometry id names.
/// </summary>
/// <param name="FirstCarrier">Lowest occupied bin.</param>
/// <param name="DataCarriers">Occupied bins carrying payload.</param>
/// <param name="PilotCarriers">Occupied bins carrying a known reference, spread evenly through the
/// block.</param>
/// <param name="BitLoading">Bits per data carrier as runs across the band, or null for uniform.
/// Part of the geometry rather than of the profile because it is read AGAINST the carrier layout:
/// a receiver decoding a burst on a geometry it did not transmit needs that geometry's tiers, not
/// its own.</param>
/// <remarks>
/// <para>A geometry says nothing about the sample rate, the transform size or the cyclic prefix.
/// Those are shared by every geometry a station can hear, because they are what a receiver has to
/// know before it can demodulate the header that names the geometry - see
/// <see cref="OfdmFmGeometryTable"/>.</para>
/// </remarks>
public sealed record OfdmFmGeometry(
    int FirstCarrier,
    int DataCarriers,
    int PilotCarriers,
    IReadOnlyList<OfdmFmBitLoadingTier>? BitLoading = null)
{
    /// <summary>Occupied bins, data and pilots together.</summary>
    public int TotalCarriers => DataCarriers + PilotCarriers;

    /// <summary>One past the highest occupied bin.</summary>
    public int EndCarrier => FirstCarrier + TotalCarriers;

    /// <summary>
    /// Bits each data carrier carries: the geometry's bit-loading tiers if it has them, otherwise
    /// the requested constellation uniformly across the band.
    /// </summary>
    public int[] BitsPerDataCarrier(OfdmFmConstellation uniform)
    {
        var bits = new int[DataCarriers];
        if (BitLoading is null || BitLoading.Count == 0)
        {
            Array.Fill(bits, uniform.BitsPerCarrier());
            return bits;
        }

        // Counted before anything is written, so a tier set that covers too MANY carriers is
        // caught. Filling and then checking where the cursor stopped cannot see that case: the
        // fill clamps at the end of the array, the cursor lands on exactly DataCarriers, and a
        // profile asking for more carriers than it has is silently truncated instead.
        int covered = 0;
        foreach (OfdmFmBitLoadingTier tier in BitLoading)
        {
            if (tier.Bits is < 1 or > 8)
            {
                throw new InvalidOperationException(
                    $"bit-loading tier of {tier.Bits} bits is not a constellation we have");
            }

            if (tier.Carriers < 0)
            {
                throw new InvalidOperationException(
                    $"bit-loading tier covers {tier.Carriers} carriers");
            }

            covered += tier.Carriers;
        }

        if (covered != DataCarriers)
        {
            throw new InvalidOperationException(
                $"bit-loading tiers cover {covered} carriers, profile has {DataCarriers}");
        }

        int at = 0;
        foreach (OfdmFmBitLoadingTier tier in BitLoading)
        {
            for (int c = 0; c < tier.Carriers; c++)
            {
                bits[at++] = tier.Bits;
            }
        }

        return bits;
    }

    /// <summary>Which occupied positions carry pilots: spread as evenly as the count allows, so a
    /// channel estimate has references across the whole block rather than at one end.</summary>
    public bool[] PilotMap()
    {
        var map = new bool[TotalCarriers];
        if (PilotCarriers == 0)
        {
            return map;
        }

        double step = TotalCarriers / (double)PilotCarriers;
        for (int p = 0; p < PilotCarriers; p++)
        {
            int position = (int)Math.Round((p + 0.5) * step);
            map[Math.Clamp(position, 0, TotalCarriers - 1)] = true;
        }

        return map;
    }

    /// <summary>Validates the layout against a transform size, throwing with the reason.</summary>
    public void Validate(int fftSize)
    {
        if (FirstCarrier < 1 || EndCarrier > (fftSize / 2))
        {
            throw new InvalidOperationException(
                $"carriers {FirstCarrier}..{EndCarrier - 1} do not fit the "
                + $"{fftSize / 2} usable bins of a {fftSize}-point real transform (DC and Nyquist "
                + "are never occupied)");
        }

        if (DataCarriers < 1 || PilotCarriers < 0)
        {
            throw new InvalidOperationException("a profile needs at least one data carrier");
        }

        BitsPerDataCarrier(OfdmFmConstellation.Qpsk);
    }

    /// <summary>Whether two layouts occupy the same bins with the same loading. Records compare
    /// lists by reference, and a bit-loading list read from a file is never the same reference
    /// as one built in code, so this compares what the wire would see.</summary>
    public bool SameLayoutAs(OfdmFmGeometry other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (FirstCarrier != other.FirstCarrier
            || DataCarriers != other.DataCarriers
            || PilotCarriers != other.PilotCarriers)
        {
            return false;
        }

        IReadOnlyList<OfdmFmBitLoadingTier> mine = BitLoading ?? [];
        IReadOnlyList<OfdmFmBitLoadingTier> theirs = other.BitLoading ?? [];
        return mine.SequenceEqual(theirs);
    }
}

/// <summary>
/// The ordered table a burst header's 4-bit geometry id indexes.
/// </summary>
/// <remarks>
/// <para><b>The order is a wire format.</b> The header says "geometry 5" and nothing about what
/// geometry 5 is, so both ends must hold the same table. Appending is safe; reordering is not: a
/// receiver holding an older table would decode a renumbered burst on the wrong carriers and fail
/// its CRC, which looks exactly like a bad link. Exactly the pattern the coding index already
/// establishes in <see cref="OfdmFmBurstCodec"/>.</para>
/// <para><b>Id 0 is the acquisition geometry.</b> The sync symbol, the preamble and the header of
/// EVERY burst are sent on it, whatever the payload uses, because a receiver has to demodulate the
/// header before it can learn what the payload's layout is. So id 0 is what a station with no
/// information falls back to, and every other entry is a layout the header can name for the
/// payload. The table can be sparse: an id nobody has filled in is one this station cannot hear
/// or send, and a header naming it is refused rather than guessed at.</para>
/// <para>Every entry shares the sample rate, transform size and cyclic prefix of the profile the
/// table is used with. The table does not carry them because the header cannot: a receiver needs
/// them to demodulate the header at all.</para>
/// <para>The table itself is never on the wire. It is built from the profiles a station holds,
/// one entry per distinct <c>geometryId</c> they declare, so a station's own layouts can join one
/// without anything here being recompiled: see <see cref="OfdmFmParameters.TableOf"/>.</para>
/// <para><b>No shipped preset declares an id</b>, so nothing built from
/// <see cref="OfdmFmPresets"/> uses this and each preset acquires on its own layout. The reason
/// is stated there rather than repeated here.</para>
/// </remarks>
public sealed class OfdmFmGeometryTable
{
    /// <summary>Four bits of geometry id.</summary>
    public const int MaxEntries = 16;

    /// <summary>The id every burst's sync, preamble and header are sent on.</summary>
    public const int AcquisitionId = 0;

    private readonly OfdmFmGeometry?[] _entries = new OfdmFmGeometry?[MaxEntries];

    /// <summary>Builds a table from entries by id. Nulls are ids nobody has filled in.</summary>
    /// <exception cref="ArgumentException">More than sixteen entries, or no entry at id 0, which
    /// is the one a receiver needs before it can read anything.</exception>
    public OfdmFmGeometryTable(IReadOnlyList<OfdmFmGeometry?> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaxEntries)
        {
            throw new ArgumentException(
                $"a geometry table holds at most {MaxEntries} entries, was given {entries.Count}",
                nameof(entries));
        }

        if (entries.Count == 0 || entries[AcquisitionId] is null)
        {
            throw new ArgumentException(
                $"a geometry table needs an entry at id {AcquisitionId}: that is the acquisition "
                + "geometry every burst's header is read on",
                nameof(entries));
        }

        for (int id = 0; id < entries.Count; id++)
        {
            _entries[id] = entries[id];
        }
    }

    /// <summary>A table with one geometry, at id 0. What a profile with no table runs on: its
    /// own layout is the acquisition layout, and the header's geometry id is always 0, which is
    /// exactly the waveform this was before geometry was signalled.</summary>
    public static OfdmFmGeometryTable Single(OfdmFmGeometry geometry) => new([geometry]);

    /// <summary>The acquisition geometry, on which every burst's header is read.</summary>
    public OfdmFmGeometry Acquisition => _entries[AcquisitionId]!;

    /// <summary>The geometry an id names, or null if the id is not filled in.</summary>
    public OfdmFmGeometry? this[int id] =>
        id >= 0 && id < MaxEntries ? _entries[id] : null;

    /// <summary>Every filled-in id, ascending.</summary>
    public IEnumerable<int> Ids
    {
        get
        {
            for (int id = 0; id < MaxEntries; id++)
            {
                if (_entries[id] is not null)
                {
                    yield return id;
                }
            }
        }
    }

    /// <summary>The id whose entry has this layout, or null if none does.</summary>
    public int? IdOf(OfdmFmGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        for (int id = 0; id < MaxEntries; id++)
        {
            if (_entries[id] is OfdmFmGeometry entry && entry.SameLayoutAs(geometry))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>Validates every entry against a transform size.</summary>
    public void Validate(int fftSize)
    {
        foreach (int id in Ids)
        {
            try
            {
                _entries[id]!.Validate(fftSize);
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"geometry {id}: {e.Message}", e);
            }
        }
    }
}
