# Decode a recording

You will get the frames out of a WAV file, whether or not you know what mode it holds.

Three tools do this, and the .deb ships none of them. They live in the source tree and you build them from a checkout.

- `pdn-decode` sweeps every mode the modem has over a recording whose mode nobody wrote down.
- `sm-decode` reads one file in one mode you already know.
- `sm-pocsag` decodes and encodes POCSAG pager transmissions.

## Before you start

- A checkout of the source: `git clone https://github.com/packet-net/pdn-soundmodem.git`, then work from that directory.
- The .NET 10 SDK on the same machine. `dotnet --list-sdks` should show a 10.x entry.
- A recording in WAV form, at any sample rate. `samples/demo/` holds nine recordings, one per mode family, if you want something to practise on.

## Sweep a recording with pdn-decode

`scripts/pdn-decode` builds the tool on first use, and again whenever a source file is newer than the binary, then runs it. `--rebuild` forces a build.

```
scripts/pdn-decode samples/demo/01-afsk1200-aprs-position.wav
```

Put it on your PATH with `ln -s "$PWD/scripts/pdn-decode" ~/.local/bin/pdn-decode` and it answers to `pdn-decode` anywhere.

Arguments are files, directories (every `.wav` in them) or globs. The tool expands globs itself, so a quoted pattern still works.

For each file it reads the WAV, resamples it to each mode's DSP rate of 12 kHz or 48 kHz whatever the file's own rate is, runs every mode in the sweep set over the whole file, and groups identical frames. A multi-channel file is read on its loudest channel unless `--channel N` says otherwise. A two-channel capture with the radio on one side is ordinary, and reading the silent side looks the same as a file with nothing in it.

### How wide to sweep

By default it runs every mode in the catalogue, with each PSK mode run twice under the differential and the coherent detector. That is 46 passes: about 3 seconds for a one-second recording and about 14 seconds for a six-second one on a desktop.

| Flag | Passes | What it sweeps |
|---|---|---|
| *(none)* | 46 | every catalogue mode |
| `--packet` | 30 | everything except `freedv-*` and `ms110d-*`. The HF data waveforms are most of the running time and no VHF or UHF radio carries them |
| `--fm` | 13 | the FM-native modes only. Narrower than you probably want, because it leaves out the shaped-PSK modes, which an FM radio carries perfectly well |
| `--modes a,b,c` | as listed | when you already know |

`--list` prints the sweep set and exits. The mode strings are in [05-modes.md](05-modes.md).

### Where each mode listens

Each mode listens at its catalogue centre, 1700 Hz for `afsk300` and 1500 Hz for most PSK modes. A survey capture is by definition somewhere else, so tell the tool where.

Three ways to say where to listen instead:

- `--centre HZ` runs every mode that has a centre at that audio frequency, and wins over the other two.
- A sidecar. When the modem's signal survey (see [11-logging-and-metrics.md](11-logging-and-metrics.md)) wrote a `.json` beside the WAV, the centre it measured is used with nothing on the command line, or added to the grid when you also pass `--sweep`.
- `--sweep` tries a grid of eleven centres, 500 to 2500 Hz in 200 Hz steps, plus each mode's own. That is twelve runs per mode, so pair it with `--packet` or `--modes`. Asking for `--centre` and `--sweep` together is a usage error.

A sidecar that will not parse, or that carries no centre, gets a line in the report and the sweep carries on at the catalogue centres.

Here is a survey capture from an HF station, checked in under `samples/offair/`, holding a 300 baud AFSK beacon at 1134 Hz, well below where `afsk300` normally listens. The sidecar puts the sweep on it.

```
$ pdn-decode --modes afsk300,afsk300-il2p samples/offair/2026-08-24/20260824-152242-1134hz-unclaimed.wav
20260824-152242-1134hz-unclaimed.wav  12000 Hz mono, 6.43 s
  centre 1134 Hz from 20260824-152242-1134hz-unclaimed.json (unclaimed capture, 230 Hz wide)
  frame 1  116 bytes  via afsk300 @ 1134 Hz  (fcs ok, -19 Hz off centre)
    PD4R-12>ALL  UI  pid=F0
    (hex dump elided)
    text  :>>>>> PD4R-12 <<<<< qrv on 144.925 (fm 1k2) 144.775 (ssb) 14.105 (ssb) 7.049 (ssb) 438.175 (fm 9k6)

  1 distinct frame(s), 1 decode(s); 2 modes tried, 1 silent, 0.8 s.
```

The baseband `fsk*` and `c4fsk*` modes occupy DC upwards and have no centre, so they run unchanged whatever you ask for. Modes too wide to sit where they were pointed are skipped with one line naming them.

## Read the report

```
01-afsk1200-aprs-position.wav  48000 Hz mono, 1.20 s
  frame 1  55 bytes  via afsk1200  (fcs ok)
          also read by: afsk1200-fx25, afsk1200-fx25rx, afsk1200-multi
    M0LTE>APRS  UI  pid=F0
    0000  82 a0 a4 a6 40 40 e0 9a  60 98 a8 8a 40 61 03 f0  |....@@..`...@a..|
    0010  21 35 31 33 32 2e 30 37  4e 2f 30 30 30 30 35 2e  |!5132.07N/00005.|
    0020  37 39 57 2d 70 64 6e 2d  73 6f 75 6e 64 6d 6f 64  |79W-pdn-soundmod|
    0030  65 6d 20 64 65 6d 6f                              |em demo|
    text  !5132.07N/00005.79W-pdn-soundmodem demo

  1 distinct frame(s), 4 decode(s); 46 modes tried, 42 silent, 3.5 s.
```

`via <mode>` names the mode that read the frame most confidently. A verified CRC beats Reed-Solomon standing alone, and both beat a frame the receiver read and would not have handed to a host.

`also read by` lists every other mode that produced the identical bytes. Several modes reading one burst is normal, because `afsk1200`, its diversity bank and the FX.25 receiver all read plain AX.25.

The diagnostics in brackets are the receiver's account of how hard the frame was to read: `fcs ok`, `il2p+crc ok`, bytes repaired by FEC, bytes erased, bits chased, and how far off centre the winning branch sat.

`text` is the information field, printable characters only.

`--quiet` cuts each file down to its summary line. Exit status is 0 if anything decoded, 1 if nothing did, and 2 for a usage or input error.

## Trust the report

Running 46 receivers over a recording is 46 chances for one of them to find structure that is not there, and `--sweep` multiplies that by twelve. Two things in the output guard against believing one.

A frame read with no verified CRC behind it carries `MONITOR ONLY, a crc link would not deliver this`. That is the same distinction the modem makes on the air, where an IL2P+CRC link shows such a frame on the station page and does not pass it to your node or APRS software.

No AX.25 header line is printed at all when the address field does not validate as callsign characters with a proper termination. A false positive reads as a short frame with no header line under it, `reed-solomon only` among its diagnostics and the monitor-only badge after them: plausible-looking bytes with nothing saying to believe them.

## Decode one file with sm-decode

Use this when you know the mode. It builds and runs in one command.

```
$ dotnet run --project tools/Packet.SoundModem.Decode -c Release -- samples/demo/03-qpsk3600-ax25-ui.wav qpsk3600 --crc
[1] M0LTE>PDNODE:pdn-soundmodem QPSK3600 NinoTNC mode 5 data demo de M0LTE
1 frames decoded from 03-qpsk3600-ax25-ui.wav (qpsk3600 il2p)
```

The mode follows the file name and defaults to `afsk1200`. The list is shorter than the catalogue: `afsk1200`, `afsk1200-multi`, `bpsk300`, `bpsk1200`, `qpsk600`, `qpsk2400`, `qpsk3600`, `fsk9600`, `fsk9600-il2p`, `fsk4800`, `fsk4800-il2p` and `ardop`. Anything else exits 2.

The BPSK and QPSK modes imply IL2P framing. Add `--crc` for the NinoTNC IL2P+CRC variants, `--il2p` to read AFSK as IL2P, `--fx25` for FX.25, and `--quiet` for the count alone. `ardop` reports what the ARDOP demodulator recovered rather than AX.25 frames, needs 12 kHz audio, and is the only mode that takes `--centre HZ`.

It reads the file at its own rate and takes the first channel, so put the signal on the first channel of a stereo file, or use `pdn-decode --channel`. It exits 0 even when nothing decoded.

With the package installed and the mode known, `pdn-soundmodem --wav FILE` decodes a recording through the modems your `--modem` flags or config file name, with nothing to build. See [the one-shot flags](reference/command-line.md#one-shot-flags).

## Decode and build pages with sm-pocsag

POCSAG is the pager waveform, not one of the packet modes, so it has its own tool.

```
$ dotnet run --project tools/Packet.SoundModem.Pocsag -c Release -- decode samples/pocsag/pocsag1200-mixed-48k.wav
[1] RIC 133703  Function 3  Alpha: Hello DAPNET interop
[2] RIC 8  Function 0  Numeric: 0123456789-U.[]
[3] RIC 2007287  Function 2  Alpha: Frame seven, function two
[4] RIC 2097151  Function 0  Numeric: 999 111
[5] RIC 21  Function 1  (tone only)
5 page(s) decoded from pocsag1200-mixed-48k.wav (pocsag1200)
```

`--baud 512`, `1200` or `2400` picks the rate, and 1200 is DAPNET's. `--quiet` prints the count alone. The decoder finds inverted polarity by itself, and a page that needed bits corrected, arrived inverted or ran out mid-message says so in brackets.

Encoding writes a WAV you can play into a receiver:

```
dotnet run --project tools/Packet.SoundModem.Pocsag -c Release -- encode /tmp/page.wav "133703:3:a:Hello DAPNET interop" "8:0:n:0123456789"
```

A page is `<ric>:<function>:<type>[:<text>]`, where the type is `a` for alphanumeric, `n` for numeric or `t` for tone-only. `--rate` sets the sample rate (48000), `--invert` flips the polarity, and `--preamble` sets the preamble length in bits (576). It prints one line naming the file, the mode, the rate, the polarity and the duration. [samples/pocsag/README.md](../samples/pocsag/README.md) has the commands that regenerate the checked-in corpus and the multimon-ng cross-check.

## Check it worked

Run `scripts/pdn-decode samples/demo/01-afsk1200-aprs-position.wav`. You should see one frame from `M0LTE>APRS`, the summary line `1 distinct frame(s), 4 decode(s); 46 modes tried`, and exit status 0.

## If it did not

`pdn-decode: no files matched`, exit 2. The path or glob matched nothing. Check the path, or point at the directory.

A build error naming the SDK. The tools need the .NET 10 SDK, which the .deb does not install. Check `dotnet --list-sdks`.

`nothing decoded` on the summary line, exit status 1. Widen the sweep: drop `--packet` or `--fm`, then try `--sweep --packet` for a signal that may not be where its mode expects. On a stereo file try `--channel 0` and `--channel 1`. A recording that clipped or came in far too quiet may hold nothing readable, and [04-levels.md](04-levels.md) says what that looks like.

`--centre is only implemented for ardop` from sm-decode. Only `pdn-decode` retunes the packet modes; use it instead.

sm-pocsag decodes nothing from a file you believe holds pages. Try the other `--baud` values.

Decodes you do not believe, from a live station rather than a file, are in [12-troubleshooting.md](12-troubleshooting.md).

## Related

- [05-modes.md](05-modes.md) for every mode string the sweep can run.
- [11-logging-and-metrics.md](11-logging-and-metrics.md) for the survey captures and raw capture that produce most of the files you will point these tools at.
- [reference/command-line.md](reference/command-line.md) for the modem's own `--wav` and `--wav-loop`.
- [samples/demo/README.md](../samples/demo/README.md) for the nine demo recordings, with the payload each one carries.
