using Packet.Ax25;
using Packet.Core;

namespace Packet.SoundModem.Modems;

internal static class Ax25TransmitOptimization
{
    private enum Kind { Information, UnnumberedInformation, ReceiverReady }

    private sealed record Candidate(byte[] Bytes, int AddressLength, Kind Kind);

    private sealed class FrameComparer : IEqualityComparer<byte[]>
    {
        internal static readonly FrameComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }
    }

    internal static int[] FindSurvivors(IReadOnlyList<byte[]?> frames)
    {
        var survivors = new int[frames.Count];
        Candidate? previous = null;
        var duplicates = new Dictionary<byte[], int>(FrameComparer.Instance);
        int runStart = 0;

        for (int i = 0; i < frames.Count; i++)
        {
            survivors[i] = i;
            Candidate? current = Classify(frames[i]);
            if (current is null || previous is null || current.Kind != previous.Kind
                || !current.Bytes.AsSpan(0, current.AddressLength)
                    .SequenceEqual(previous.Bytes.AsSpan(0, previous.AddressLength)))
            {
                duplicates.Clear();
                runStart = i;
            }

            if (current is { Kind: Kind.ReceiverReady })
            {
                // Queue order, not numerical N(R), decides which acknowledgement is newest.
                if (i > runStart)
                {
                    survivors[i - 1] = i;
                }
            }
            else if (current is not null)
            {
                if (!duplicates.TryAdd(current.Bytes, i))
                {
                    survivors[i] = duplicates[current.Bytes];
                }
            }

            previous = current;
        }

        for (int i = survivors.Length - 1; i >= 0; i--)
        {
            survivors[i] = survivors[survivors[i]];
        }

        return survivors;
    }

    private static Candidate? Classify(byte[]? bytes)
    {
        if (bytes is null
            || !Ax25Frame.TryParse(bytes, Ax25ParseOptions.Strict, out Ax25Frame? frame)
            || frame is null || frame.PollFinal)
        {
            return null;
        }

        int addressLength = 14 + (7 * frame.Digipeaters.Count);
        // The codec tolerates low bits in callsign octets; optimization must not normalize them.
        for (int i = 0; i < addressLength; i++)
        {
            if (i % 7 != 6 && (bytes[i] & 1) != 0)
            {
                return null;
            }
        }

        if ((frame.Control & 0x0F) == 0x01 && bytes.Length == addressLength + 1)
        {
            return new(bytes, addressLength, Kind.ReceiverReady);
        }

        if (frame.IsUi)
        {
            return new(bytes, addressLength, Kind.UnnumberedInformation);
        }

        if ((frame.Control & 1) == 0)
        {
            // Without negotiated modulo, an I-frame must be non-polling under either reading.
            if (Ax25Frame.TryParse(bytes, Ax25ParseOptions.Strict, extended: true, out Ax25Frame? extended)
                && extended is { PollFinal: true })
            {
                return null;
            }

            return new(bytes, addressLength, Kind.Information);
        }

        return null;
    }
}
