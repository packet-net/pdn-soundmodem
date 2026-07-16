using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Dsp;

/// <summary>
/// Batches a demodulator's per-symbol decision points (see
/// <see cref="IConstellationSource"/>) into fixed-size scope frames for a constellation /
/// eye display — the spectrum waterfall's counterpart for the phase-shift-keyed modes.
/// Each frame is <c>pointsPerFrame</c> points, two signed bytes apiece (I then Q), so a
/// frame is <see cref="FrameLength"/> bytes; at qpsk2400's 1200 sym/s the default 256-point
/// frame emits ≈ 5 times a second, the same few-kB/s order as <see cref="SpectrumSource"/>.
/// </summary>
/// <remarks>
/// Frames are auto-scaled: each is normalised to its own peak component so the constellation
/// <em>shape</em> — how tightly symbols cluster at the expected phases — reads the same
/// regardless of absolute signal level, exactly as a scope's auto-range would. That trades
/// away cross-frame amplitude comparison, which the shape-focused diagnostic does not need.
/// A silent frame (peak below <see cref="SilenceFloor"/>) emits all-zero bytes rather than
/// amplifying noise to full scale.
/// </remarks>
public sealed class ConstellationSource
{
    /// <summary>Peak component magnitude below which a frame is treated as silence and
    /// emitted as zeros instead of being auto-scaled up from noise.</summary>
    public const float SilenceFloor = 1e-6f;

    private readonly Action<ReadOnlyMemory<byte>> _frameSink;
    private readonly int _pointsPerFrame;
    private readonly float[] _i;
    private readonly float[] _q;
    private readonly byte[] _frame;
    private int _filled;

    /// <summary>Creates a source delivering constellation frames to
    /// <paramref name="frameSink"/>.</summary>
    /// <param name="frameSink">Receives one <see cref="FrameLength"/>-byte frame per
    /// <paramref name="pointsPerFrame"/> symbols. The buffer is reused; consumers must copy
    /// if they keep it.</param>
    /// <param name="pointsPerFrame">Symbols per emitted frame (a "scope frame").</param>
    public ConstellationSource(Action<ReadOnlyMemory<byte>> frameSink, int pointsPerFrame = 256)
    {
        ArgumentNullException.ThrowIfNull(frameSink);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pointsPerFrame, 0);
        _frameSink = frameSink;
        _pointsPerFrame = pointsPerFrame;
        _i = new float[pointsPerFrame];
        _q = new float[pointsPerFrame];
        _frame = new byte[pointsPerFrame * 2];
    }

    /// <summary>Bytes per emitted frame: two signed bytes (I, Q) per point.</summary>
    public int FrameLength => _pointsPerFrame * 2;

    /// <summary>Symbols accumulated per emitted frame.</summary>
    public int PointsPerFrame => _pointsPerFrame;

    /// <summary>Subscribes to a modem's symbol tap so its points flow into frames. Only the
    /// PSK modes implement <see cref="IConstellationSource"/>; wiring any of them is enough.</summary>
    public void Attach(IConstellationSource modem)
    {
        ArgumentNullException.ThrowIfNull(modem);
        modem.SymbolPlotted += Add;
    }

    /// <summary>Adds one symbol's decision point; emits a frame every
    /// <see cref="PointsPerFrame"/> points.</summary>
    public void Add(ConstellationPoint point)
    {
        _i[_filled] = point.I;
        _q[_filled] = point.Q;
        if (++_filled < _pointsPerFrame)
        {
            return;
        }

        _filled = 0;

        // Auto-range to the frame's peak component (I or Q) so the cluster geometry, not the
        // absolute level, sets the scale — the differential product's magnitude tracks the
        // matched-filter output amplitude, which is not normalised to 1.
        float peak = 0;
        for (int n = 0; n < _pointsPerFrame; n++)
        {
            peak = MathF.Max(peak, MathF.Max(MathF.Abs(_i[n]), MathF.Abs(_q[n])));
        }

        if (peak < SilenceFloor)
        {
            Array.Clear(_frame);
            _frameSink(_frame);
            return;
        }

        float scale = 127f / peak;
        for (int n = 0; n < _pointsPerFrame; n++)
        {
            _frame[n * 2] = (byte)(sbyte)Math.Clamp((int)MathF.Round(_i[n] * scale), -127, 127);
            _frame[n * 2 + 1] = (byte)(sbyte)Math.Clamp((int)MathF.Round(_q[n] * scale), -127, 127);
        }

        _frameSink(_frame);
    }
}
