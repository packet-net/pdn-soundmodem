using System.Buffers.Binary;

namespace Packet.SoundModem.Daemon;

/// <summary>
/// Continuous raw capture of a channel's receive audio: chunked 16-bit mono WAVs under a byte
/// budget, so a station can record everything it heard and the whole run can be re-scored
/// offline against a later receiver. This is the instrument the GB7RDG 24 h benchmark
/// improvised with sox and a second radio, built in: the <see cref="Survey.SignalSurvey"/>
/// keeps curated bursts with cooldowns and per-hour caps - the right economy for a Pi left
/// running for months - while this keeps the unedited stream, which is the only record a
/// future demodulator can be honestly measured against (a burst detector's blind spots
/// otherwise decide what the corpus contains).
/// </summary>
/// <remarks>
/// <para>Fed from a <see cref="Channel.SoundModemChannel.AddReceiveTap"/>, so it sees exactly
/// the samples the modems see, at the channel DSP rate, and stops while the station
/// transmits (its own transmissions are not receive evidence). Chunks are named
/// <c>raw-&lt;UTC&gt;.wav</c> from the wall clock at the moment the chunk opens, which is what
/// lets a frame-log row be traced to the audio that carried it.</para>
/// <para>Writes happen synchronously on the audio thread through a large
/// <see cref="FileStream"/> buffer - at 12-24 kHz mono that is one flush every few tens of
/// seconds against a local disk, absorbed by the audio input's own buffering. The RIFF sizes
/// are re-patched every few seconds of audio, so a crash or power cut leaves every chunk
/// readable to within seconds of the end. A disk failure disables the writer with one journal
/// line rather than taking the audio thread down with it.</para>
/// </remarks>
public sealed class RawCaptureWriter : IDisposable
{
    /// <summary>WAV header bytes reserved at the front of each chunk.</summary>
    private const int HeaderLength = 44;

    /// <summary>Audio seconds between RIFF size patches - the crash-tolerance window.</summary>
    private const int PatchIntervalSeconds = 5;

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly long _chunkSamples;
    private readonly long _patchSamples;
    private readonly int _sampleRate;
    private readonly TimeProvider _time;
    private readonly byte[] _blockBuffer = new byte[8192];
    private FileStream? _chunk;
    private long _chunkSampleCount;
    private long _samplesSincePatch;
    private bool _failed;

    /// <summary>Creates the writer; chunks appear under <paramref name="directory"/> (created
    /// if needed) as they fill.</summary>
    /// <param name="directory">Where chunks are written.</param>
    /// <param name="maxBytes">Directory byte budget; the oldest chunks are pruned to fit.</param>
    /// <param name="chunkMinutes">Audio minutes per chunk.</param>
    /// <param name="sampleRate">The channel DSP rate the tap delivers.</param>
    /// <param name="time">Wall clock for chunk names; null = system.</param>
    public RawCaptureWriter(
        string directory, long maxBytes, int chunkMinutes, int sampleRate, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(chunkMinutes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        _directory = directory;
        _maxBytes = maxBytes;
        _chunkSamples = (long)chunkMinutes * 60 * sampleRate;
        _patchSamples = (long)PatchIntervalSeconds * sampleRate;
        _sampleRate = sampleRate;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
    }

    /// <summary>One line for the journal when the disk lets the station down; null-safe.</summary>
    public Action<string>? Failed { get; set; }

    /// <summary>Appends a block of receive audio. Wire as a channel receive tap.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        if (_failed)
        {
            return;
        }

        try
        {
            while (!samples.IsEmpty)
            {
                if (_chunk is null || _chunkSampleCount >= _chunkSamples)
                {
                    Roll();
                }

                int take = Math.Min(samples.Length, _blockBuffer.Length / 2);
                for (int i = 0; i < take; i++)
                {
                    float clamped = Math.Clamp(samples[i], -1f, 1f);
                    BinaryPrimitives.WriteInt16LittleEndian(
                        _blockBuffer.AsSpan(i * 2), (short)(clamped * short.MaxValue));
                }

                _chunk!.Write(_blockBuffer, 0, take * 2);
                _chunkSampleCount += take;
                _samplesSincePatch += take;
                samples = samples[take..];

                if (_samplesSincePatch >= _patchSamples)
                {
                    PatchSizes();
                    _chunk.Flush();
                    _samplesSincePatch = 0;
                }
            }
        }
        catch (IOException e)
        {
            Fail(e);
        }
        catch (UnauthorizedAccessException e)
        {
            Fail(e);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_chunk is not null)
        {
            try
            {
                PatchSizes();
                _chunk.Dispose();
            }
            catch (IOException)
            {
                // Losing the final patch on a dying disk is the existing failure, not a new one.
            }

            _chunk = null;
        }
    }

    private void Roll()
    {
        if (_chunk is not null)
        {
            PatchSizes();
            _chunk.Dispose();
            _chunk = null;
        }

        Prune();
        string name = $"raw-{_time.GetUtcNow():yyyyMMdd'T'HHmmss'Z'}.wav";
        _chunk = new FileStream(
            System.IO.Path.Combine(_directory, name), FileMode.Create, FileAccess.ReadWrite,
            FileShare.Read, bufferSize: 1 << 20);
        WriteHeader();
        _chunkSampleCount = 0;
        _samplesSincePatch = 0;
    }

    private void WriteHeader()
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);          // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);          // mono
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], _sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], _sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 2);          // block align
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);         // bits per sample
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], 0);
        _chunk!.Write(header);
    }

    private void PatchSizes()
    {
        long position = _chunk!.Position;
        int dataBytes = (int)(position - HeaderLength);
        Span<byte> size = stackalloc byte[4];
        _chunk.Position = 4;
        BinaryPrimitives.WriteInt32LittleEndian(size, 36 + dataBytes);
        _chunk.Write(size);
        _chunk.Position = 40;
        BinaryPrimitives.WriteInt32LittleEndian(size, dataBytes);
        _chunk.Write(size);
        _chunk.Position = position;
    }

    private void Prune()
    {
        // Oldest first by name - the UTC stamp makes lexicographic chronological.
        var chunks = Directory.GetFiles(_directory, "raw-*.wav");
        Array.Sort(chunks, StringComparer.Ordinal);
        long total = 0;
        foreach (string file in chunks)
        {
            total += new FileInfo(file).Length;
        }

        int victim = 0;
        while (total > _maxBytes && victim < chunks.Length - 1)
        {
            total -= new FileInfo(chunks[victim]).Length;
            File.Delete(chunks[victim]);
            victim++;
        }
    }

    private void Fail(Exception e)
    {
        _failed = true;
        Failed?.Invoke(
            $"raw capture: DISABLED after a disk error ({e.Message}); the station keeps "
            + "decoding, but nothing more is recorded");
        try
        {
            _chunk?.Dispose();
        }
        catch (IOException)
        {
        }

        _chunk = null;
    }
}
