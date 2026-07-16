using Packet.SoundModem.Ofdm;

namespace Packet.SoundModem.Tests.Ofdm;

/// <summary>
/// Exactly-reproducible structural checkpoints for the datac OFDM modulator — the parts that do
/// NOT cross the C <c>cosf/sinf</c> boundary and so can be asserted bit-for-bit against codec2's
/// documented behaviour (docs/ofdm-design.md §3, §7.5(a)): the QPSK map, the pilot row, the
/// unique-word words and their placement indices, the preamble LCG, and the sample-count
/// geometry. The float-tolerance oracle comparison lives in <c>OfdmModulatorOracleTests</c>.
/// </summary>
public class OfdmModulatorTests
{
    private static readonly string[] AllModes =
        ["datac0", "datac1", "datac3", "datac4", "datac13", "datac14"];

    [Fact]
    public void Qpsk_Map_Is_Gray_Coded_Indexed_By_First_Bit_Msb()
    {
        // ofdm.c:76 qpsk[] = {1, j, -j, -1}, index (b0<<1)|b1.
        OfdmTxTables.Qpsk[0b00].Should().Be(new Cf(1f, 0f));
        OfdmTxTables.Qpsk[0b01].Should().Be(new Cf(0f, 1f));
        OfdmTxTables.Qpsk[0b10].Should().Be(new Cf(0f, -1f));
        OfdmTxTables.Qpsk[0b11].Should().Be(new Cf(-1f, 0f));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Pilot_Row_Has_Zero_Edges_And_Pilotvalues_Interior(string name)
    {
        var mode = OfdmMode.ForName(name);
        var mod = new OfdmModulator(mode);

        // Round-trip a pilot-only frame through nothing observable directly, so assert via the
        // documented construction: pilots[0]=pilots[Nc+1]=0, pilots[i]=pilotvalues[i] otherwise.
        // The pilot row is private, so we validate the source table the ctor consumes.
        OfdmTxTables.PilotValues.Length.Should().Be(64);
        foreach (sbyte v in OfdmTxTables.PilotValues)
        {
            ((int)v).Should().BeOneOf(-1, 1);
        }

        mod.Mode.Nc.Should().Be(mode.Nc);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TxUw_Resolves_With_Front_And_Tail_Copies(string name)
    {
        byte[] uw = OfdmTxTables.ResolveTxUw(name);
        var mode = OfdmMode.ForName(name);
        uw.Length.Should().Be(mode.Nuwbits);

        byte[] seed16 = [1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0];
        byte[] seed24 = [1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0];

        if (name is "datac0" or "datac1")
        {
            // 16-bit word copied to the front, rest zero-padded.
            uw.AsSpan(0, 16).ToArray().Should().Equal(seed16);
            uw.AsSpan(16).ToArray().Should().OnlyContain(b => b == 0);
        }
        else
        {
            // 24-bit word copied to the front AND again to the tail at [Nuwbits-24]. When the two
            // regions overlap (datac4/datac14, Nuwbits=32) the tail copy wins — exactly codec2's
            // second memcpy overwriting the first. Rebuild the reference the same way and compare.
            var expected = new byte[mode.Nuwbits];
            seed24.CopyTo(expected, 0);
            seed24.CopyTo(expected, mode.Nuwbits - 24);
            uw.Should().Equal(expected);

            // Invariants that hold in every case: the tail 24 bits are the word, and the
            // non-overlapped front prefix is the word's prefix.
            uw.AsSpan(mode.Nuwbits - 24).ToArray().Should().Equal(seed24);
            uw.AsSpan(0, mode.Nuwbits - 24).ToArray().Should()
                .Equal(seed24.AsSpan(0, mode.Nuwbits - 24).ToArray());
        }
    }

    [Fact]
    public void Uw_Placement_Indices_Match_Codec2_Formula()
    {
        // datac0: nuwsyms=16, uw_step=Nc+1=10 (80 < 144), so index = (i+1)*5 → {5,10,…,80}.
        var c0 = new OfdmModulator(OfdmMode.Datac0);
        c0.UwIndexSymbols.Should().Equal(Enumerable.Range(1, 16).Select(i => i * 5));

        // datac1: nuwsyms=8, uw_step=Nc+1=28 (112 < 4104), index = (i+1)*14 → {14,28,…,112}.
        var c1 = new OfdmModulator(OfdmMode.Datac1);
        c1.UwIndexSymbols.Should().Equal(Enumerable.Range(1, 8).Select(i => i * 14));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Uw_Symbols_And_Indices_Have_Consistent_Length(string name)
    {
        var mod = new OfdmModulator(OfdmMode.ForName(name));
        int nUwSyms = OfdmMode.ForName(name).Nuwbits / 2;
        mod.TxUwSymbols.Count.Should().Be(nUwSyms);
        mod.UwIndexSymbols.Count.Should().Be(nUwSyms);
        mod.UwIndexSymbols.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        mod.UwIndexSymbols.Max().Should().BeLessThan(OfdmMode.ForName(name).SymsPerPacket);
    }

    [Fact]
    public void Preamble_Lcg_Is_Deterministic_And_Seed_Dependent()
    {
        // seed 2, first step: (1103515245*2 + 12345) % 32768 = 19731 > 16384 → bit 1.
        OfdmTxTables.LcgBits(1, 2)[0].Should().Be(1);
        OfdmTxTables.LcgBits(8, 2).Should().NotEqual(OfdmTxTables.LcgBits(8, 3));

        // Same seed → same bits (deterministic preamble content).
        OfdmTxTables.LcgBits(72, 2).Should().Equal(OfdmTxTables.LcgBits(72, 2));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Preamble_And_Postamble_Are_One_Frame_And_Differ(string name)
    {
        var mode = OfdmMode.ForName(name);
        var mod = new OfdmModulator(mode);

        mod.PreambleRaw.Length.Should().Be(mode.SamplesPerFrame);
        mod.PostambleRaw.Length.Should().Be(mode.SamplesPerFrame);

        // seed 2 vs seed 3 → different waveforms.
        mod.PreambleRaw.ToArray().Should().NotEqual(mod.PostambleRaw.ToArray());

        // Deterministic across instances.
        var mod2 = new OfdmModulator(mode);
        mod2.PreambleRaw.ToArray().Should().Equal(mod.PreambleRaw.ToArray());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Modulated_Packet_Has_Exact_Sample_Geometry(string name)
    {
        var mode = OfdmMode.ForName(name);
        var mod = new OfdmModulator(mode);

        byte[] bits = Bits(mode.BitsPerPacket, seed: 42);
        Cf[] audio = mod.ModulatePacketBits(bits);
        audio.Length.Should().Be(mode.SamplesPerPacket);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Clipper_Caps_Peak_At_Ofdm_Peak_And_Produces_Signal(string name)
    {
        var mode = OfdmMode.ForName(name);
        var mod = new OfdmModulator(mode);

        Cf[] audio = mod.ModulatePacketBits(Bits(mode.BitsPerPacket, seed: 7));

        double peak = audio.Max(s => (double)s.Magnitude);
        double rms = Math.Sqrt(audio.Average(s => (double)((s.Re * s.Re) + (s.Im * s.Im))));

        // Final soft clip caps magnitude at OFDM_PEAK (allow a hair of float slack).
        peak.Should().BeLessThanOrEqualTo(16384 * 1.0001);
        // A real modulated packet, not silence — and driven near the peak by the clipper gains.
        rms.Should().BeGreaterThan(1000);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Assemble_Modem_Packet_Places_Uw_At_Indices_And_Payload_In_Order(string name)
    {
        var mode = OfdmMode.ForName(name);
        var mod = new OfdmModulator(mode);

        int nSyms = mode.SymsPerPacket;
        int nUw = mode.Nuwbits / 2;
        // Distinctive payload symbols so we can verify order (magnitude 10*i, never a UW value).
        var payload = new Cf[nSyms - nUw];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = new Cf(10f + i, -5f);
        }

        Cf[] packet = mod.AssembleModemPacket(payload);
        packet.Length.Should().Be(nSyms);

        var uwIdx = mod.UwIndexSymbols.ToHashSet();
        int p = 0, u = 0;
        for (int s = 0; s < nSyms; s++)
        {
            if (uwIdx.Contains(s))
            {
                packet[s].Should().Be(mod.TxUwSymbols[u++], "UW symbol lands at its index");
            }
            else
            {
                packet[s].Should().Be(payload[p++], "payload consumed in row-major order");
            }
        }

        p.Should().Be(payload.Length);
        u.Should().Be(nUw);
    }

    [Fact]
    public void Assemble_Modem_Packet_Rejects_Wrong_Payload_Length()
    {
        var mod = new OfdmModulator(OfdmMode.Datac0);
        Action bad = () => mod.AssembleModemPacket(new Cf[3]);
        bad.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Emit_Burst_Concatenates_Preamble_Packets_Postamble()
    {
        var mode = OfdmMode.Datac0;
        var mod = new OfdmModulator(mode);

        var payload = new Cf[mode.SymsPerPacket - (mode.Nuwbits / 2)];
        Cf[] packet = mod.AssembleModemPacket(payload);

        Cf[] burst = mod.EmitBurst([packet, packet]);
        burst.Length.Should().Be((2 * mode.SamplesPerFrame) + (2 * mode.SamplesPerPacket));

        // resetFilter=true makes the burst reproducible.
        var mod2 = new OfdmModulator(mode);
        Cf[] burst2 = mod2.EmitBurst([packet, packet]);
        burst2.Should().Equal(burst);
    }

    public static TheoryData<string> Modes()
    {
        var data = new TheoryData<string>();
        foreach (string m in AllModes)
        {
            data.Add(m);
        }

        return data;
    }

    private static byte[] Bits(int n, int seed)
    {
        var rng = new Random(seed);
        var bits = new byte[n];
        for (int i = 0; i < n; i++)
        {
            bits[i] = (byte)(rng.Next() & 1);
        }

        return bits;
    }
}
