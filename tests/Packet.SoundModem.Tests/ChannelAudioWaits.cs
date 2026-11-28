using Packet.SoundModem.Waterfall;

namespace Packet.SoundModem.Tests;

/// <summary>Waiting for the connections to a <see cref="ChannelAudioStream"/>, without polling.</summary>
internal static class ChannelAudioWaits
{
    /// <summary>
    /// Waits until <paramref name="condition"/> holds, looked at again each time the stream says its
    /// connections, named readers or requested band have changed
    /// (<see cref="ChannelAudioStream.ClientsChanged"/>).
    /// </summary>
    public static async Task WhenClientsAsync(this ChannelAudioStream stream, Func<bool> condition, string what)
    {
        var changes = new ChangeSignal();
        void Pulse() => changes.Pulse();
        stream.ClientsChanged += Pulse;
        try
        {
            await changes.WhenAsync(
                condition, what, () => $"{stream.ClientCount} client(s), {stream.Listeners.Count} named reader(s)");
        }
        finally
        {
            stream.ClientsChanged -= Pulse;
        }
    }
}
