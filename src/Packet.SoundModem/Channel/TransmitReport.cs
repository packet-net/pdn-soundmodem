namespace Packet.SoundModem.Channel;

/// <summary>
/// What the channel did with one transmitted frame, beyond putting it on the air.
/// </summary>
/// <param name="TrimHz">
/// How far the burst was shifted off the nominal centre to suit the station it was addressed to;
/// 0 when it went out straight.
/// </param>
/// <param name="HeldFor">
/// How long the frame waited between being handed to the channel and the transmitter picking it
/// up - carrier sense, the p-persistence roll, the turnaround hold and
/// <see cref="SoundModemChannel.TransmitInhibit"/>, all of it, because from the host's side they
/// are one wait and it has no way to tell them apart.
/// </param>
/// <remarks>
/// <para><b>Why a station records this.</b> A KISS host cannot see it. It writes a frame to a
/// socket and the write returns; whether the frame went out then or four minutes later is
/// invisible to it, so its own retry timers run against the wrong event and it queues retries for
/// a frame that has not been sent. Those retries pile up behind the first one and go out together
/// in a single keyup the moment the channel opens - identical polls, one after another, none of
/// which can be answered because the station is deaf for the length of its own transmission. The
/// only party that knows how long the wait was is the channel, so it is the channel that has to
/// write it down.</para>
/// <para>Measured to the pickup rather than to the end of the audio: what an operator is asking
/// when they ask how long a frame was held is how long it sat waiting, not how long it took to
/// send once it started. The two differ by the frame's airtime, which is a property of the mode
/// and already knowable from the length.</para>
/// </remarks>
public readonly record struct TransmitReport(double TrimHz, System.TimeSpan HeldFor);
