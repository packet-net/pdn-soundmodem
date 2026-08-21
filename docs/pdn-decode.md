# pdn-decode - decoding recordings whose mode nobody wrote down

`sm-decode` answers "decode this file as qpsk3600". `pdn-decode` answers the question you
actually have when somebody hands you a WAV: **what is in this, and what mode is it?**

```
pdn-decode *.wav
```

It sweeps every mode the modem has over each file, prints every frame any of them recovered as
hex and printable ASCII, and says which modes read it and how cleanly.

## What it does

For each file, in order:

1. Reads the WAV. Multi-channel files use the **loudest** channel unless `--channel N` says
   otherwise - a two-channel capture with the radio on one side is ordinary, and reading the
   silent side is indistinguishable from a file with nothing in it.
2. Resamples to each mode's DSP rate (12000 or 48000 Hz), once per rate, whatever the file's own
   rate is. See [resampling](#resampling) for why that lives here and not in the library.
3. Runs every mode in the sweep set over the whole file, one after another.
4. Groups identical frames, names the mode that read each one most confidently, and lists the
   others that also read it.

## The sweep set

**By default, everything.** Every mode in `ModemCatalog.KnownModes`, with each PSK mode run twice
under both detectors (see below).
The whole point of the tool is not having to have guessed right, and on a short file the widest
net costs only wall clock - about 15 seconds for a 7-second file.

Narrow it if you know roughly what you are looking at and want the answer sooner:

| Flag | Set | Why |
|---|---|---|
| *(none)* | every catalogue mode | the default |
| `--packet` | everything except `freedv-*` and `ms110d-*` | the HF data waveforms are most of the running time and no VHF or UHF radio carries them |
| `--fm` | the FM-native modes only | fastest, and **narrower than you probably want** - see below |
| `--modes a,b,c` | exactly these | when you already know |

### Why `--fm` is not the default, which is a mistake worth not repeating

The obvious reading of "it came off an FM radio" is `FmModeProfiles.IsFmMode`, and that reading is
wrong. That table answers **"which modes reach the air as frequency modulation"** - a question
about modulators and deviation targets. The question this tool is asked is **"what can arrive
through an FM receiver"**, and the shaped-PSK modes answer yes to the second and no to the first.
Nino's own switch map says so outright: switch 1000 is grouped "Shaped PSK - SSB radios, **or FM
radios**" ([mode-modulation-reference.md](mode-modulation-reference.md)), and switch 0101
(`qpsk3600`) is a speaker-and-mic mode grouped with the FM AFSK ones.

This is not hypothetical. The first real off-air corpus this tool was pointed at - five noisy
captures Tom recorded off an FM radio on 2026-08-21 - is four files of **`bpsk1200` IL2P+CRC**
(a BPQ chat session between N2IRZ-2 and WA2M-2) and one of **`qpsk600`**. The FM-native sweep read
exactly none of it. The wider sweep recovered the session. `SweepTests` pins the lesson so a
future tidy-up cannot quietly re-narrow the default.

That corpus is worth reading about in its own right: the `qpsk600` file
([`samples/offair/2026-08-21/`](../samples/offair/2026-08-21/README.md)) holds a clean burst at
+1.7 dB that nothing in the tree can copy, and working out why is what this tool is for.

**Every PSK mode runs twice**, differential and coherent. Differential is the catalogue default
and that is the right default - it was measured to copy 9 of 9 of the NinoTNC corpus where
coherent copied 0 to 2 of 3, and BPSK was reversed to it in issues #40/#42 because coherent's
narrow Costas loop cannot acquire real carriers. But a file reaching this tool is by definition
one that something else could not read, which is exactly where the losing branch occasionally
wins: coherent buys a decibel or two when it can lock, and a long burst on an on-frequency
carrier is where it can. It costs one more pass and removes a class of "we did not try".

The `--packet` set is stated as an **exclusion**, not a list, so a mode added to the catalogue
joins it automatically. That is the safe direction for a tool whose failure mode is a silent miss.

## Reading the output

```
packet-24501.wav  48000 Hz mono, 7.40 s
  frame 1  62 bytes  via bpsk1200  (il2p+crc ok, 2 byte(s) FEC-corrected, -2 Hz off centre, il2p Type1)
          also read by: bpsk1200-multi
    N2IRZ-2>WA2M-2  I ns=0 nr=6 P/F  pid=CF
    0000  ae 82 64 9a 40 40 e4 9c  64 92 a4 b4 40 65 d0 cf  |..d.@@..d...@e..|
    0010  9c 64 92 a4 b4 40 64 ae  82 64 9a 40 40 12 07 e7  |.d...@d..d.@@...|
    0020  b1 00 00 05 5b 42 50 51  43 68 61 74 53 65 72 76  |....[BPQChatServ|
    0030  65 72 2d 36 2e 30 2e 32  31 2e 34 30 5d 0d        |er-6.0.21.40].|
    text  .d...@d..d.@@.......[BPQChatServer-6.0.21.40].

  5 distinct frame(s), 16 decode(s); 46 modes tried, 42 silent, 13.7 s.
```

- **`via <mode>`** is the mode that read the frame most confidently, not merely the first to read
  it. A verified CRC beats Reed-Solomon standing alone, and both beat a frame the receiver read
  and would not have handed to a host.
- **`also read by`** is every other mode that produced the identical bytes. Several modes reading
  one burst is normal, not a problem: `afsk1200`, its diversity bank and the FX.25 receiver all
  read plain AX.25. It is also the answer to "would my other TNC have seen this?".
- **The parenthesised diagnostics** are the receiver's own account of how hard the frame was to
  read: `fcs ok`, `il2p+crc ok`, bytes repaired by FEC, bytes erased, bits chased, how far off
  centre the winning branch sat, and how much pre-emphasis it needed. A persistent non-zero
  correction count is a link quietly consuming its error budget.
- **The AX.25 header line** is printed only when the bytes really are shaped like an AX.25 frame.
  See [trusting the output](#trusting-the-output).
- **`text`** is the information field, printable characters only.

## Trusting the output

Not every decode is a frame somebody sent, and the tool is built to say so rather than to look
confident.

A Reed-Solomon-only decode of noise is a real thing this sweep will occasionally produce - running
forty-six receivers over a recording is running forty-six chances for one of them to find
structure that is not there, and widening the default deliberately traded some of that for
coverage. Two things keep it honest:

- **`MONITOR ONLY, a crc link would not deliver this`** on a frame that was read but has no
  verified CRC behind it. This is not decoration: it is the same distinction the station makes on
  the air, where an IL2P+CRC link reports such a frame to its display and does not pass it to its
  host. The plain-IL2P tolerance is deliberately left at its default here, because switching it on
  would deliver those frames and hide the fact that a real link would not have.
- **No AX.25 header line at all** when the address field does not validate as shifted ASCII with
  plausible callsign characters and a proper termination. A tool that prints confident-looking
  callsigns off bytes that were never an AX.25 frame is worse than one that prints none.

Here is the shape of a false positive, from a real run over an SSB `bpsk300` recording:

```
  frame 1  16 bytes  via c4fsk19200  (plain il2p, reed-solomon only, 1 byte(s) FEC-corrected,
                                      il2p Type1, MONITOR ONLY, a crc link would not deliver this)
    0000  aa 64 7e 50 a8 6a e4 7a  6e 54 7e 50 b8 63 13 f0  |.d~P.j.znT~P.c..|
```

Sixteen plausible-looking bytes, no CRC, no callsigns, and every badge saying not to believe it.

## Resampling

The library deliberately has no resampler. A live station's capture rate must be an integer
multiple of its DSP rate, and `ModemCatalog` and the daemon refuse anything else by name rather
than quietly resampling, because a station silently running through an interpolator is a
performance question nobody asked.

That rule is right for a station and wrong for a forensic tool, where the file is whatever the
person who recorded it had their soundcard set to and 44100 Hz is a perfectly ordinary answer. So
the conversion lives in the tool, in `Resampler.cs`, and appears in the output rather than being
assumed. It is standard L/M polyphase with a Blackman-Harris windowed sinc, its length chosen from
the ratio so the transition band stays about a tenth of the lower Nyquist however severe the
decimation - which matters because 48000 to 12000 is the common case and a short filter would fold
everything from 6 to 24 kHz back onto the band we are trying to decode.

`SweepTests.A_Capture_At_An_Awkward_Rate_Still_Decodes` pins the path against a real modem rather
than against a spectrum.

## Options

```
--packet          skip the HF data waveforms (freedv-*, ms110d-*)
--fm              sweep only the FM-native modes (narrower than you probably want)
--modes a,b,c     sweep only these modes
--list            print the sweep set and exit
--channel N       read channel N (default: the loudest channel in the file)
--quiet           one summary line per file, no frames
```

Arguments may be files, directories (every `.wav` in them), or globs. Globs are expanded by the
tool as well as by the shell, so a quoted pattern still works.

Exit status is 0 if anything decoded, 1 if nothing did, 2 for a usage or input error.

## What is tested

`tests/Packet.SoundModem.Tests/MultiDecode/` holds the round trip that is the tool's whole claim:
every packet mode's own modulator, rendered at the 48 kHz card rate a real capture arrives at,
swept, and checked that the exact bytes come back and that the mode is named among those that read
them. That is also the only coverage `c4fsk9600` and `c4fsk19200` have from a rendered capture,
since `sm-samples` does not carry them.

One thing that test taught, worth knowing before writing another: build test frames as proper
AX.25 **command** frames, with the command bit set on the destination and clear on the source.
IL2P's Type 1 header carries one command/response bit for the pair, so a hand-built frame with
both bits clear - legal in AX.25 1.x, and what a test frame falls into by accident - comes back
from the plain-IL2P reading normalised to a response, and a byte-exact assertion then fails on a
frame nobody would ever transmit.
