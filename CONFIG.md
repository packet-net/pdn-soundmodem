# Configuring pdn-soundmodem

The complete reference for `soundmodem.json`. For getting the package installed and running,
start with [INSTALL.md](INSTALL.md).

## The file

| | |
|---|---|
| Read from | `--config <path>` - the packaged systemd unit passes `/etc/pdn-soundmodem/soundmodem.json` |
| Format | JSON, with `//` and `/* */` **comments allowed** and trailing commas tolerated |
| Keys | case-insensitive (`kissPort`, `KissPort` and `kissport` are the same key) |
| Seeded from | `/usr/share/pdn-soundmodem/soundmodem.example.json` on first install |

Every key is optional. An empty `{}` is a valid config and gives you one `afsk1200` modem on
KISS sub-channel 0, ALSA device `default`, 48 kHz capture, KISS on port 8105.

Applying a change means restarting the service - the file is read once at start-up:

```sh
sudo systemctl restart pdn-soundmodem
```

(Commands here are written with `sudo`, as on Ubuntu and Raspberry Pi OS. Debian only
installs `sudo` when the root password is left blank at setup - without it, become root
with `su -` and drop the prefix.)

## Top-level keys

| Key | Type | Default | What it does |
|---|---|---|---|
| `device` | string | `"default"` | Sound device (or FlexRadio) - [below](#device) |
| `captureRate` | int | `48000` | ALSA capture/playback rate in Hz - [below](#capturerate) |
| `kissPort` | int | `8105` | Shared KISS-over-TCP port, all modems by nibble - [below](#kissport-and-kissbind) |
| `bind` | string | `"127.0.0.1"` | Address **every** listener binds to; `"*"` or `"0.0.0.0"` for all |
| `sideband` | string | `"usb"` | Which sideband the radio is on - [below](#band-plans-in-rf-terms) |
| `dialFrequency` | number | *(computed)* | Pin the dial instead of letting the daemon choose - [below](#band-plans-in-rf-terms) |
| `modems` | array | one `afsk1200` on sub-channel 0 | The modems sharing the channel - [below](#modems) |
| `modemPlugins` | array | *(none)* | Load modem assemblies from outside this package - [below](#modemplugins) |
| `ptt` | object | *(none - VOX)* | How the radio is keyed - [below](#ptt) |
| `alsa` | object | *(card left as it is)* | The sound card's mixer: capture gain, AGC, mic boost - [below](#alsa) |
| `waterfall` | object | *(disabled)* | Browser spectrum/waterfall page - [below](#waterfall) |
| `paging` | object | *(disabled)* | POCSAG paging endpoint - [below](#paging) |
| `ardop` | object | *(disabled)* | ARDOP virtual TNC - [below](#ardop) |
| `frameLog` | object | *(not kept)* | Record every frame heard and sent to SQLite - [below](#framelog) |
| `survey` | object | *(not surveying)* | Keep signals this station cannot read, for analysis - [below](#survey) |
| `metrics` | object | *(publishing nothing)* | Publish what this station hears, for a monitoring system to collect - [below](#metrics) |
| `rawCapture` | object | *(not recording)* | Record everything the channel hears, continuously - [below](#rawcapture) |
| `idBeacons` | bool | `true` | Listen for the NinoTNC idents sent alongside the PSK SSB modes - [below](#idbeacons) |
| `flex` | object | see below | FlexRadio slice params - [below](#flex) |
| `ubersdr` | object | see below | UberSDR stream params - [below](#ubersdr) |
| `deadFeed` | object | *(per-device defaults)* | Dead-feed protection: restart when the input dies silently - [below](#deadfeed) |
| `publish` | object | *(publishing nothing)* | Offer this station to a public monitor site over an outbound uplink - [below](#publish). Exclusive with `monitor` |
| `monitor` | object | *(not a monitor)* | Front many web receivers behind one page instead of running one station - [below](#monitor). Exclusive with `device` |

---

## `device`

The ALSA device used for both capture and playback. `arecord -l` and `aplay -l` list what
your machine has.

```json
"device": "default"
```

- **`"default"`** - the system default. Fine when there is exactly one USB sound card.
- **`"plughw:1,0"`** - a specific card and device. Prefer `plughw:` over `hw:` - it lets ALSA
  convert sample formats, which most USB interfaces need. Card numbers move around when
  devices are re-plugged; `plughw:CARD=Device,DEV=0` (see `aplay -L`) is stable.
- **`"null"`** - ALSA's null device: the modem runs and serves KISS but hears and transmits
  nothing. Useful for checking a config parses and the daemon starts.
- **`"pipe:<in>,<out>[,<rate>]"`** - a virtual audio link made of two named pipes, carrying raw
  32-bit float samples. Point two daemons at each other's pipes, reversed, and they are on the same
  air with no sound card, no kernel module and no radio:

  ```json
  "device": "pipe:/tmp/air-a-to-b,/tmp/air-b-to-a,48000"   // station A
  "device": "pipe:/tmp/air-b-to-a,/tmp/air-a-to-b,48000"   // station B
  ```

  The FIFOs are created if they do not exist, and the rate defaults to 48000 and must be a multiple
  of the channel's DSP rate. What one station transmits, the other hears - which is a different and
  stronger claim than a modem round-tripping its own buffer, because the transmit path and the
  receive path have to agree about something for it to work at all. It is what
  `scripts/two-station-pipe.py` uses.

  **It is not a radio.** No noise, no filtering, no deviation: samples arrive exactly as they were
  written. Right for "can these two talk", wrong for any performance question - those belong to the
  Watterson and FM channel models, which put a measured path in the way on purpose.
- **`"flex:<radio>[:slice][@station]"`** - a FlexRadio 6000-series over the LAN as both sound
  card *and* PTT.
- **`"ubersdr:<instance>"`** - a public UberSDR web receiver, **receive only** -
  [below](#listening-to-a-web-receiver).

For the Flex form, `<radio>` is `discover` (broadcast), an IP (`host[:port]`), a discovery
spec (`serial=…` / `name=…`), or `mock` (an in-process fake for offline testing). `<slice>` is
a letter `A`-`H`, default `A`.

With **no** `@station` the daemon owns the radio and brings it up headless, creating its own
slice from the [`flex`](#flex) section. A trailing `@station` attaches to a running SmartSDR's
existing slice instead, and the `flex` slice params are ignored because SmartSDR configures
it. Either way the radio keys itself, so **`ptt` must be omitted** - configuring both is
rejected at start-up. See [docs/flex-integration.md](docs/flex-integration.md).

### Listening to a web receiver

A station needs an antenna in a quiet place, and most of us do not have one. `ubersdr:` points
the whole modem at somebody who does - a public [UberSDR](https://github.com/madpsy/ka9q_ubersdr)
instance, of which there are many, on far better antennas than a suburban garden allows:

```json
{
  "device": "ubersdr:m9psy-1.instance.ubersdr.org",
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc",           "rfFrequency": 7050300, "port": 8101 },
    { "subChannel": 1, "mode": "ardop", "bandwidth": 500, "rfFrequency": 7050950, "port": 8200 },
    { "subChannel": 2, "mode": "bpsk300",                 "rfFrequency": 7051600, "port": 8102 }
  ],
  "waterfall": { "port": 8099 },
  "bind": "0.0.0.0"
}
```

That is an ordinary config with an ordinary band plan. Every mode works, the waterfall works,
the frame log works, KISS works - the device is the only thing that changed:

```
dial: 7.049450 MHz USB
  modem 0 afsk300-il2pc at 7.050300 MHz = 850 Hz audio
  modem 1 ardop at 7.050950 MHz = 1500 Hz audio
  modem 2 bpsk300 at 7.051600 MHz = 2150 Hz audio
audio: m9psy-1.instance.ubersdr.org iq48 IQ at 7.049450 MHz → USB 150-3450 Hz audio at 12000 Hz (RECEIVE ONLY)
ubersdr: M9PSY-1 · RX888 with 40m Full Wave Loop (GPSDO) · Dalgety Bay, Scotland, UK · reference offset 0 Hz
```

**Write the instance however you have it.** `ubersdr:m9psy-1.instance.ubersdr.org`,
`ubersdr:host:8443`, or the whole URL out of the browser's address bar -
`ubersdr:https://m9psy-1.instance.ubersdr.org/` - all name the same receiver. HTTPS on 443 is
assumed, because that is what public instances run.

**It receives and cannot transmit.** There is no transmitter at the far end of a WebSocket, so:

- `ptt` alongside it is rejected at start-up - there is nothing for a PTT line to key;
- anything sent to a KISS port is refused immediately, with that as the reason, rather than
  queued against a transmitter that will never appear;
- `ardop` still loads and still hears the channel, but no ARQ session can ever complete. The
  daemon says so at start-up rather than leaving you to work it out from a Winlink timeout.
  It is not pointless there: every frame ARDOP demodulates - other stations' sessions included -
  is still drawn on the waterfall and written to the frame log.

**The dial is not yours to set, so the daemon sets it.** Give the modems `rfFrequency` - or pin
`dialFrequency` - and the receiver is tuned there, exactly as a headless Flex is. Without either
it refuses to start: unlike a radio there is no dial already set to read a number off.

**It takes IQ, not the instance's demodulated audio.** UberSDR will demodulate SSB for you, but
then its filter, its AGC and its resampler are all in the path and none of them is yours. Taking
`iq48` puts the whole ±24 kHz of complex baseband here, so the receive filter is the one your
band plan asked for and there is no AGC anywhere - which is what makes an SNR figure off this
path mean the same thing as one off a sound card. `captureRate` does not apply; the stream
brings its own 48 kHz clock.

**Sessions end and are picked up again.** Public instances cap a session (3 hours on the ones
measured, reported at start-up), and a modem is expected to run for months. A closed stream is
treated as ordinary and reconnected, losing about a second each time to the receiver's
start-of-stream level ramp. Only an instance that stays unreachable for five minutes stops the
service - with exit 1, so systemd restarts it and tries afresh.

**Be a good guest.** These are somebody else's receivers, run at their expense, with a limited
number of listener slots. One long session is kinder than repeated reconnects, and a station you
intend to leave running for weeks is worth mentioning to the operator.

## `captureRate`

```json
"captureRate": 48000
```

The rate the sound card runs at. The daemon decimates internally to the DSP rate the modes
need, so the card's native rate is the right answer - 48000 for essentially all USB audio.

**It must be an integer multiple of the DSP rate**, or the daemon refuses to start with
`--capture-rate must be a multiple of N`. The DSP rate is decided by your modes:

| Modes in use | DSP rate | Valid `captureRate` |
|---|---|---|
| Any mode named `*9600*`, or starting `fsk`, `c4fsk`, `freedv-`, `ms110d-` | 48000 Hz | 48000, 96000, … |
| Everything else (`afsk*`, `bpsk*`, `qpsk*`) | 12000 Hz | 12000, 24000, 36000, 48000, … |

Mixing families is fine: if *any* configured mode needs 48 kHz, the whole channel runs at
48 kHz. Ignored entirely for `flex:` devices, which supply their own DAX sample clock.

## `kissPort` and `bind`

```json
"kissPort": 8105,
"bind": "127.0.0.1"
```

`kissPort` is the **shared** port: every modem is reachable on it, selected by the KISS port
nibble - the QtSoundModem multiplex model, and what Direwolf does on 8001.

`bind` is the address **every** listener uses - the shared KISS port, the per-modem ports, the
waterfall, paging and ARDOP alike. One setting rather than one per service: they are all on the
same machine facing the same network. `"*"` or `"0.0.0.0"` opens them to all interfaces.

> **KISS has no authentication of any kind.** Anything that can reach the port can key your
> transmitter. It stays on loopback unless you deliberately change it, and the daemon prints a
> warning at start-up when you do. If a host on another machine needs access, an SSH tunnel is
> a better answer than `"*"`.

### A port per modem

The nibble only helps if your host software lets you set it, and a good deal of it does not -
it assumes KISS channel 0 and offers nowhere to say otherwise. On the shared port such a host
can only ever reach `subChannel: 0`, however many modems you have configured.

Give a modem its own port and that stops mattering:

```json
"kissPort": 8105,
"modems": [
  { "subChannel": 0, "mode": "afsk1200", "port": 8110 },
  { "subChannel": 1, "mode": "bpsk300",  "port": 8111 }
]
```

A dedicated port carries **only** that modem, and presents it as **nibble 0**:

- frames received by that modem arrive on it labelled channel 0;
- other modems' traffic never appears on it;
- anything transmitted into it goes out on that modem, whatever nibble the client used.

So a host that only speaks channel 0 talks to port 8111 and works `bpsk300` without ever
knowing a multiplex exists. The shared port keeps working alongside, still reporting true
nibbles - you can run both at once.

Two services asking for the same TCP port is rejected at start-up, naming both, rather than
left to whichever listener happens to bind second.

### The KISS command surface

What a host can say on any KISS port (shared or dedicated), beyond ordinary data frames:

| Type | Name | Behaviour |
|---|---|---|
| 0 | data | queued for transmission on the addressed modem |
| 1-4 | TXDELAY / P / SLOTTIME / TXTAIL | update the channel's CSMA settings live, x10 ms (unlike QtSoundModem, which ignores them) |
| 5 | full duplex | accepted, no-op (the channel is half duplex) |
| 6 | SETHW | modem-specific hardware control, [below](#sethw-runtime-waveform-selection) |
| 12 | ACKMODE data | `id_lo, id_hi, data...` (the BPQ convention): the two-byte id is echoed back to the SENDING client once the frame's audio has fully left the device - true TX-complete, not a timer. An id with no data is acked immediately. A frame the channel refuses gets no ack; the host's retry timer is the error channel, as ACKMODE defines |
| 7 | RX quality | TNC-to-host only: opt-in per-frame decode diagnostics as JSON (`"qualityFrames"`) |

On a dedicated port every TNC-to-host frame - received data, acks, SETHW echoes, quality -
carries nibble 0, the same relabelling the data path always had.

### SETHW: runtime waveform selection

On a port whose modem is `ms110d-*`, SETHW (type 6) switches the TRANSMIT waveform without a
restart - the KISS face of `Ms110dModem.SetTxWaveform`, which in-process hosts call directly:

- `payload[0]`: the waveform number, plain: 0-8 or 13 (MIL-STD-188-110D Phase A). No
  NinoTNC-style +16 form - there is no flash here to suppress a write to, and the setting is
  RAM-only either way: the config file's `mode` is what survives a restart.
- `payload[1]` (optional): interleaver, 0 = short, 1 = long.

Receive needs nothing: it is autobaud, decoding every Phase A waveform regardless of the TX
setting. On success the daemon echoes the SETHW frame back to the sender - the confirmation
KISS itself never defined - and journals `modem 3: SETHW -> ms110d-wn2, short interleaver`.
An invalid payload is ignored and journalled, never answered (KISS has no error channel), and
the mode string on the waterfall's TX rows and in the frame log follows the change live. A
SETHW to any other mode's port is a journalled no-op.

## `modems`

The logical modems sharing the one audio channel, each addressed by its KISS port nibble.
This is the QtSoundModem multiplex model - your host software picks a modem by KISS port.

```json
"modems": [
  { "subChannel": 0, "mode": "afsk1200-multi", "frequency": 1700 },
  { "subChannel": 1, "mode": "bpsk300", "frequency": 1500, "offsetPairs": 4, "offsetStepHz": 7.5 }
]
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `subChannel` | int | `0` | KISS port nibble, 0-15. Must be unique - duplicates are rejected at start-up |
| `mode` | string | `"afsk1200"` | See [docs/modes.md](docs/modes.md) for all 38 modes, plus `ardop` - [below](#ardop) |
| `frequency` | number | mode default | Audio centre in Hz, TX **and** RX |
| `rfFrequency` | number | *(none)* | Where on the band it sits, in absolute Hz - [below](#band-plans-in-rf-terms) |
| `bandwidth` | number | measured | How much room to plan for; mainly for `ardop` - [below](#band-plans-in-rf-terms) |
| `offsetPairs` | int | `4` | Diversity-bank modes only |
| `offsetStepHz` | number | baud/40 | Diversity-bank modes only |
| `acceptPlainIl2p` | bool | `false` | IL2P+CRC modes only: pass IL2P **without** the trailing CRC to the host as well. Such frames are read and displayed either way - [below](#acceptplainil2p) |
| `port` | int | *(none)* | A TCP port carrying this modem alone - KISS, or the ardopcf host interface for `ardop` - [below](#a-port-per-modem) |

Omit `modems` entirely and you get one `afsk1200` on sub-channel 0.

### `frequency`

Moves a modem's audio centre, QtSoundModem-style - for meeting a peer who sits off the usual
centre, or (for the wide waveforms) placing them anywhere in a wide passband:

| Family | Default centre | Accepts `frequency`? |
|---|---|---|
| `afsk*` | 1700 Hz | yes |
| `bpsk*`, `qpsk*` | 1500 Hz (1650 for `qpsk3600`) | yes |
| `ardop` | 1500 Hz | yes - shifted outside the TNC, [see below](#ardop) |
| `ms110d-*` | 1800 Hz (MIL-STD-188-110D) | yes - shifted around its unchanged DSP |
| `freedv-*` | 1500 Hz (datac4/14 fractionally below) | yes - same mechanism |
| `fsk*`, `c4fsk*` | - occupies DC-to-Nyquist | **no** |

Setting one on a baseband mode is an error at start-up, not silently ignored.

**Moving a spec-fixed waveform does not make it non-standard on air.** The `ms110d-*` and
`freedv-*` waveforms keep their spec centres as defaults, and an override translates the audio
around the modem's unchanged DSP (the same analytic shift ARDOP uses). What a peer receives is
set by the RF centre alone, so a moved waveform interoperates exactly as an unmoved one; the
1800 Hz figure only matters to a peer whose modem cannot be told the dial (a MIL radio on a
voice plug), and staying on the default preserves that case. A centre that would fold the
waveform's skirts into DC or Nyquist is refused at start-up with the numbers.

### `offsetPairs` / `offsetStepHz`

The BPSK modes (`bpsk300`, `bpsk1200`, and their `-multi` aliases) run a frequency-diversity
bank by default: parallel decoder branches at stepped centres, which is how they tolerate an
off-frequency peer. `offsetPairs` is the number of branches *either side* of centre and
`offsetStepHz` the gap between them, so coverage spans ±`offsetPairs`×`offsetStepHz`.

More branches widen coverage at a linear CPU cost. `"offsetPairs": 0` gives a plain single
centred modem. Both are ignored by non-bank modes.

### `acceptPlainIl2p`

IL2P comes in two variants: with a trailing CRC (what a NinoTNC sends, what every `-il2pc` mode
here expects) and without. There is nothing in the signal to tell you which you are looking at -
the modulation, the baud and the centre are all identical - so a neighbour sending the CRC-less
variant used to be a strong, clean, perfectly demodulated burst that produced nothing at all.

That is not hypothetical. A station running `bpsk300` at 2150 Hz on 7.0516 MHz had a
[survey](#survey) full of `missed` captures; replayed offline through every 12 kHz mode, one of
them came out as

```
bpsk300-nocrc @ 2116 Hz -> 46 B  GB7BPQ>BEACON: =5828.54N/00612.69W- {BPQ32}
```

with the carrier measured at 2123 Hz - 27 Hz off the station's own centre, comfortably inside its
own diversity bank. Right frequency, right modulation, right baud, wrong IL2P variant: that BPQ32
node sends plain IL2P, and the frame was being demodulated perfectly and then thrown away at the
CRC check.

**Every IL2P+CRC modem now reads both variants, always.** You do not configure that and you cannot
turn it off. A station that cannot read a neighbour cannot tell you the neighbour is there, and
"nothing decoded" and "a CRC-less node you are structurally deaf to" look identical from the
outside. So a plain IL2P frame is decoded, and it appears:

- on the [waterfall](#waterfall) panel, **badged `RS ONLY`** in warning orange, with a tooltip
  saying whether it went to your host;
- in the [frame log](#framelog), with `crc_valid` null, `plain_il2p` 1 recording what stood behind
  it and `monitor_only` recording whether it reached the host - both replayed to the panel on the
  next page load, so a backlogged row carries the same badge it did live;
- on the journal line, as `plain il2p (rs only, not passed to host)`;
- as a decode for the [survey](#survey), so its burst stops being captured as `missed` - which is
  the point: a row in the panel naming GB7BPQ is worth more than another WAV of the same beacon
  every ten minutes, and it leaves the capture budget for bursts nobody has explained yet.

Where it does **not** appear is the page's **links pane** (2026-09-04, at the operator's request).
That pane says who is talking to whom and how the link is doing, which is a claim about the
channel, and the pair of callsigns in an RS-only reading is exactly as unverified as the rest of
the frame: a bit error in an address field would otherwise mint a station nobody heard and a link
nobody made. The same goes for the pane's start-up backfill out of the [frame log](#framelog) - a
`monitor_only` row is skipped there too, so a restart does not put back the cards the live path
declined. It is the same rule the [metrics](#only-frames-that-vouched-for-themselves) station list
has always applied, now applied to the pane beside it. Nothing else changes: the frame is still
listed, still logged, still in the journal, and `acceptPlainIl2p` still decides the host.

What it does **not** do by default is give the frame to your KISS host. That is the one thing
`acceptPlainIl2p` decides:

```json
{ "subChannel": 0, "mode": "bpsk300", "frequency": 2150, "acceptPlainIl2p": true }
```

With that set, plain IL2P frames on that modem are sent to the host as ordinary KISS data frames
like any other. It is per modem, so a station can be permissive on the BPSK slot and strict on the
AFSK one. It does not change what the modem *transmits*: you still send IL2P+CRC, exactly as
before, and nothing about an ordinary frame's handling changes in either direction.

**What accepting them costs, plainly.** A plain IL2P frame is checked by Reed-Solomon and nothing
else. There is no CRC behind it, which is the entire reason the +CRC variant exists: RS decoding
does not merely detect errors, it *invents* corrections, and given a run of noise it will
occasionally produce a frame that looks structurally valid and is not. On an IL2P+CRC link the
trailing CRC catches those; here nothing does. Expect the occasional frame of rubbish on a noisy
channel, and more of it the busier and worse the channel is. That is exactly why this is off by
default: seeing what is on the band is a different question from feeding it to a node.

Two consequences worth knowing before you turn it on:

- **Frames that arrive this way are logged with `crc_valid` null**, not `true` - "no CRC was
  checked" rather than "the CRC passed". If you [log frames](#framelog), that column is how you
  tell which frames came in on the relaxed path, and the panel badges them whether or not you
  turned this on.
- **An IL2P+CRC frame whose CRC fails is delivered too.** The receiver genuinely cannot tell it
  apart from a plain frame - both are a valid IL2P frame with four bytes after it that do not
  check out - so a corrupt frame that would have been dropped and counted is handed up instead.
  With this off, such a frame is shown and withheld, which is a good reason to look at the row
  and a poor reason to give it to a node.

**The exception, delivered whatever this option says: a frame whose trailer nearly checks
out.** The trailing CRC is a pure function of the frame's bytes, so the receiver can compute
the trailer a recovered payload *implies* and compare it with the 32 trailer bits that actually
followed. When they differ by at most 4 bits, the trailer corroborates the frame and it goes to
the host even with this option off - logged with `crc_valid` null plus the measured bit
distance in `trailer_near_bits`. This is not the RS-only gamble the paragraphs above warn about: a frame RS invented
from noise implies a trailer uncorrelated with the received bits, and the odds of an
uncorrelated trailer landing within 4 bits of the implied one are about 1 in 90,000 - the same
order as the CRC check itself. The common real cause of a grazed trailer is mundane and
measured: transmitters truncate the final pulse at the end of the burst, the last symbols
suffer, and the wire format parks its only FEC-unprotected bytes exactly there. On the GB7RDG
24 h miss corpus this one check moved host-delivered frames from 3 of 37 to 22 of 37.

Hosts with the KISS quality extension (`kiss.emitQualityFrames`) see nothing at all for a withheld
frame: no data frame, and no quality frame either, because a quality report for a frame the host
never received would be worse than silence.

Off by default, and only meaningful on a mode that runs IL2P+CRC: `afsk300-il2pc`,
`afsk1200-il2p`, `bpsk*`, `qpsk*`, `fsk9600-il2p`, `fsk4800-il2p`, `c4fsk*`, `freedv-*` and
`ms110d-*`. Setting it on any other mode - including the ones that already read plain IL2P, like
`bpsk300-nocrc`, where every frame is plain and every frame goes to the host - is an error at
start-up rather than silently ignored, because an ignored setting leaves you believing something
changed when nothing did. Note that `fsk9600-il2p` and `fsk4800-il2p` *do* run the CRC, despite
their names.

### `identify`

Send this modem's callsign in Morse, so anyone sharing the band can read it by ear:

```json
{ "subChannel": 3, "mode": "freedv-datac1", "rfFrequency": 7054000, "port": 8103,
  "identify": { "callsign": "M0LTE" } }
```

A data waveform carries nothing a listener can decode without our software. On a real antenna
that is a problem worth fixing: a station running `freedv-datac1` or `ms110d-wn6` is, to everyone
else on the channel, an unfamiliar noise that starts and stops. This puts a callsign on it.

| Field | Type | Default | Notes |
|---|---|---|---|
| `callsign` | string | *(required)* | No default; there is none for a licence condition |
| `intervalMinutes` | number | `10` | Counted from the last identification sent |
| `wpm` | number | `20` | PARIS timing |
| `toneHz` | number | *this modem's centre* | The audio tone to key |
| `rfFrequency` | number | *(none)* | Where to identify in absolute Hz, instead of `toneHz`. Band-planned stations only |
| `includeMode` | bool | `false` | Send the mode name after the callsign |
| `amplitude` | number | `0.8` | Key-down peak, matching the modulators' own |

**It is per modem, and that is the point.** The modems on one channel can sit kilohertz apart, so
a single station-wide ident would land on one audio frequency, say nothing about the others, and
usually sit on top of one of them. Leave `toneHz` unset and the ident goes out on **this modem's
own centre** - the signal it is identifying - and follows the band plan without being kept in step
by hand. That default is doing real work: on a band-planned station the dial is chosen to centre
the ensemble, so a conventional 700 Hz ident tone is wherever the planner happened to put it. On a
real 40 m layout it landed on top of a neighbouring modem's slot.

Omit the block and the modem never identifies, which is the right answer for a mode that already
identifies itself in-band: an AX.25 node sending its own `>ID` frames is legible to anything that
can hear it. Nothing here is switched on by upgrading.

**An identification is owed only after transmitting.** The clock starts at the first transmission,
and one falls due when the interval has elapsed *and* the modem has transmitted since it last
identified. A station that has sent nothing owes nothing: keying up on a timer to announce a
callsign nobody has heard transmit is QRM, not compliance. This is also what a NinoTNC does - its
beacon runs "while the station is transmitting" - and matching the established behaviour on this
network is worth more than a rule of our own. It is queued like any other transmission, so it
waits out a busy channel rather than transmitting over somebody, and the clock is only stamped
once the radio has actually sent it: an identification the transmitter refused was not made.

```
modem 3: identifying as M0LTE in CW @ 4250 Hz = 7.054000 MHz, 20 wpm, every 10 min while transmitting (3.1 s)
...
tx[3] freedv-datac1 M0LTE>TEST 30 bytes
id[3] M0LTE in CW
```

`includeMode` sends `M0LTE FREEDV-DATAC1` instead of `M0LTE`, which tells a listener who just
heard something they could not read what it actually was. It costs about two seconds.

Rejected at start-up rather than ignored: a missing `callsign`; both `toneHz` and `rfFrequency`
(they say the same thing twice); `rfFrequency` without a band plan, since the dial is what turns
one into a tone; a callsign with characters Morse has no code for; a tone above the channel's
Nyquist; `identify` on a [receive-only](#listening-to-a-web-receiver) station, which has no
transmitter to identify; and `identify` on `ardop`, whose ARQ bursts do not go out as addressed
frames, so there is nothing to count transmissions against. A tone outside the planned passband
warns and starts - you may have moved it deliberately - but on a Flex the transmit filter is set
from that passband, so it says so.

This is the transmit side. [`idBeacons`](#idbeacons) is the unrelated receive-side feature that
listens for *other* stations' identifications.

## `modemPlugins`

**This loads and runs code that is not part of this package.** Everything below follows from that.

Some modes cannot live in this repository. It is GPL-3.0-or-later and has to stay buildable and
distributable by anyone who clones it, so it cannot contain or build against anything we are not
free to ship - an experimental waveform whose specification is not ours to publish, say, or a
vendor's modem. `modemPlugins` is how a station runs one anyway: an assembly implementing this
package's `IModemPlugin`, loaded at start-up from a path you wrote down.

```json
"modemPlugins": [
  { "path": "/opt/pdn/plugins/Packet.SoundModem.SamplePlugin.dll" }
],
"modems": [
  { "subChannel": 0, "mode": "sample:loopback" }
]
```

`sample` is the plugin in this repository (`tests/Packet.SoundModem.SamplePlugin`), used here so
that the example is one you can build and run rather than one you have to take on trust. It is a
frame codec shaped like a modem: real enough to exercise the seam end to end, not a waveform
anybody transmits.

A plugin's modes are named `pluginId:mode`. That is not decoration: it means a plugin can never
shadow or redefine a built-in mode, a log line or a mode-validation entry always says plainly which
modes were not built here, and a plugin you have not installed yet gives you
`no modem plugin registered for 'sample'` rather than a mode that mysteriously does not exist.

**Discovery is explicit, never ambient.** There is no plugins directory that gets scanned, no
probing next to the executable, no environment variable, and no default location. The only thing
loaded is a path this file names. A daemon where a file appearing on disk changes what the station
transmits is not a daemon worth having, and an explicit list also makes the audit trivial: the
config says which non-package code runs, and start-up repeats it:

```
modem plugin: sample from /opt/pdn/plugins/Packet.SoundModem.SamplePlugin.dll [sample:loopback, sample:baseband, sample:explodes]
```

| Key | Type | Default | What it does |
|---|---|---|---|
| `path` | string | *(required)* | The assembly to load. Relative to the working directory; use an absolute path in anything systemd starts. |

A plugin that will not load is reported by name and start-up continues:

```
modem plugin: FAILED /opt/pdn/plugins/Packet.SoundModem.SamplePlugin.dll - no such file
```

That is deliberate - a station should not refuse to come up because an experimental modem is not
installed. But it is only half the story: a `modems` entry asking for one of that plugin's modes
*does* stop start-up, as an unknown mode, because that is the station being asked for something it
cannot do.

Three limits worth knowing before you build one:

- **A plugin gets `IModem` and nothing else.** Audio in, frames out. It cannot key the radio, open
  a port, or reach this config. It is not a place to extend the daemon.
- **A plugin mode must run at 12000 or 48000 Hz**, because that is what the shared audio channel
  runs at. A modem whose own DSP wants another rate resamples internally; declaring a third rate is
  refused at start-up rather than quietly given 12 kHz.
- **Plugin modes are config-file only.** `--modem N:MODE[:FREQ]` already uses `:` as its own
  separator, so `--modem 0:sample:loopback` reads as mode `sample` at frequency `loopback` and goes nowhere
  useful. Nothing is lost by that: an explicit config file is the audit trail this feature is built
  around, and a plugin loaded from a command line would be exactly the ambient arrangement the
  design refuses.

There is no version handshake yet. A plugin built against a different `pdn-soundmodem` may simply
fail to load, and it will say so rather than pretend. See `docs/modem-binding.md` for the design.

## Band plans in RF terms

Audio centres are an awkward way to describe a band plan: you think in "BPSK300 at 7051.6", not
"1600 Hz with the dial on 7050.0", and the moment the dial moves every number is wrong. Give the
daemon `rfFrequency` instead and it works the dial out for you:

```json
"sideband": "usb",
"modems": [
  { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 },
  { "subChannel": 1, "mode": "ardop",         "rfFrequency": 7050950, "bandwidth": 500 },
  { "subChannel": 2, "mode": "bpsk300",       "rfFrequency": 7051600 }
]
```

At start-up it tells you what to set:

```
dial: 7.049450 MHz USB - set your radio to this
  modem 0 afsk300-il2pc at 7.050300 MHz = 850 Hz audio
  modem 1 ardop at 7.050950 MHz = 1500 Hz audio
  modem 2 bpsk300 at 7.051600 MHz = 2150 Hz audio
```

**It picks a better dial than you would by hand.** The obvious round number for that plan is
7050.000, giving a tidy-looking 300/950/1600 Hz - but `afsk300` then occupies 150-450 Hz, half of
it below where an SSB filter starts. The daemon centres the whole ensemble in the passband
instead, which is how 7.049450 falls out.

`sideband` is `"usb"` (RF = dial + audio, the data-mode norm) or `"lsb"` (RF = dial − audio).

**Bandwidths are measured, not assumed.** Each modem is asked to modulate a probe frame and the
occupied width is metered off it - the same measurement the waterfall draws its overlays from, so
the two can never disagree. `ardop` is the exception: its bandwidth is negotiated per session, so
the planner assumes the widest (2000 Hz) unless `bandwidth` says otherwise. Setting it also caps
what ARDOP negotiates (200/500/1000/2000), which is worth doing - it reclaims the room the
planner would otherwise reserve.

### Rules

- **All or nothing.** Either every modem has an `rfFrequency` or none does. The dial is shared,
  so a modem pinned to an audio offset would sit at whatever RF the dial chosen for the others
  happened to put it.
- **One or the other.** A modem cannot set both `frequency` and `rfFrequency`; they say the same
  thing two ways.
- **No dial, no start.** If the modems are spread wider than one passband can carry, the daemon
  says so - naming the span and the modems - rather than starting something that cannot work. On a
  radio it cannot open up, that is a second-radio problem; on a headless Flex the window has
  already been widened as far as the radio goes before you see this.
- **A baseband mode cannot be band-planned.** `fsk9600` and the `c4fsk*` family occupy the audio
  band from DC upwards rather than sitting on a centre frequency, so "put it at 7.0516 MHz" has no
  meaning for them. Configure that channel by audio `frequency`.

### Wide plans and the spec-fixed modes

The passband is worked out, not configured. A plan that fits an ordinary 300-2700 Hz SSB window is
placed in one, exactly as before - but on a **headless Flex**, whose transmit and receive filters
the daemon sets itself, a plan that does not fit gets a wider window instead of a refusal, up to
the radio's 10 kHz ceiling. It grows only as far as the modems need, because every extra Hz is
noise bandwidth on receive and filter to open on transmit:

```
dial: 7.049800 MHz USB
  modem 0 ms110d-wn4 at 7.051600 MHz = 1800 Hz audio
  modem 1 bpsk300 at 7.054000 MHz = 4200 Hz audio
  passband: 300-4470 Hz - wider than an ordinary 300-2700 Hz SSB window, because these modems do
  not fit one; the radio's filters are set to suit
flex: setting the slice to 7.049800 MHz and the transmit filter high cut to 4600 Hz from the band plan
flex: setting the slice receive filter to 200-4600 Hz, to hear everything the modems are placed across
```

That is how `ms110d-*` becomes placeable in RF terms at all: a 3 kHz waveform does not fit inside
2400 Hz of room however the dial is chosen.

**Every mode is movable now, so no mode dictates the dial.** `ms110d-*` and `freedv-*` used to:
their audio centres were pinned by their standards, so only one dial could put them on the RF
frequency asked for, and on USB everything else then had to sit above them. Since the
frequency-shift decorator they are placed like any other modem - the planner chooses the dial
for the whole ensemble and each spec-fixed modem is shifted to wherever that puts it, with the
RF placement exact either way. An existing config with an `rfFrequency` on one of these modes
keeps its RF placement to the Hz but may see a **different dial** in the start-up report, since
the planner now centres the ensemble instead of obeying the pinned mode.

### Pinning the dial

Set `dialFrequency` for a net frequency, or to match another application, and it is used as-is:

```json
"dialFrequency": 7050000,
"sideband": "usb"
```

A pinned dial that puts a modem outside the nominal 300-2700 Hz passband **warns and starts**
rather than refusing - that figure is nominal, the daemon cannot see your rig's filter, and you
asked for this dial. Omit `dialFrequency` and it will be chosen to fit.

The waterfall inherits the computed dial and sideband, so its RF scale is right without being
told twice.

### On a FlexRadio, it just does it

For a headless `flex:` device the daemon owns the radio, so rather than telling you the dial it
**sets** it - the slice goes to the computed frequency, and the transmit filter's high cut is
opened to clear the highest modem:

```
dial: 7.049450 MHz USB
  modem 0 afsk300-il2pc at 7.050300 MHz = 850 Hz audio
  modem 1 ardop at 7.050950 MHz = 1500 Hz audio
  modem 2 bpsk300 at 7.051600 MHz = 2150 Hz audio
flex: setting the slice to 7.049450 MHz and the transmit filter high cut to 2550 Hz from the band plan
```

That matters because **the transmit filter is a global, persistent radio setting** - whatever
last touched the radio. A 300 Hz CW filter left over from another session would quietly truncate
the top of a band plan, and nothing would say so.

**Only the high cut can be set from here.** The transmit filter's *low* cut and the slice's
*receive* filter are not exposed by the station API, so they stay as the radio has them. The
daemon reads the transmit filter back at bring-up and warns, per modem, if the plan falls outside
what the radio will actually pass - so a modem sitting below the low cut is reported rather than
silently transmitting nothing. Widening that is a job for the radio.

In attach mode (`@station`) none of this happens: SmartSDR owns the slice, and the daemon would
only be fighting it.

A station placed by audio centre rather than by RF gets the same treatment, worked out from the
modems instead of from a plan - see [the transmit filter](#the-transmit-filter).

## `ptt`

How the radio is keyed. **Omit the whole section for VOX**, or when using a FlexRadio, which
keys itself.

```json
"ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" }
```
```json
"ptt": { "type": "cm108", "device": "/dev/hidraw0", "gpio": 3 }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `type` | string | `"serial"` | `"serial"` or `"cm108"` |
| `device` | string | `""` | `/dev/ttyUSB0` for serial, `/dev/hidraw0` for CM108 |
| `line` | string | `"rts"` | Serial only: `"rts"` or `"dtr"` |
| `gpio` | int | `3` | CM108 only: the GPIO pin driven |

**CM108 needs a udev rule.** `/dev/hidraw*` is root-only by default, so the service's
unprivileged user cannot open it - see [INSTALL.md § Permissions](INSTALL.md#permissions).
Serial PTT works out of the box because the unit already joins the `dialout` group.

Which `/dev/hidraw*` node is yours is worth confirming rather than assuming - the number
moves with what else is plugged in. `ls -l /sys/class/hidraw/*/device/` maps them to USB IDs.

## `alsa`

The sound card's own mixer: capture gain, the Auto Gain Control switch, Mic Boost, and the
transmit-side playback level. Between them these decide whether the receive audio is clean,
clipped or buried, and a reboot or a re-plug can silently reset them.

```json
"alsa": { "mixer": { "captureGainPercent": 60, "agc": false, "micBoost": false, "playbackPercent": 70 } }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `captureGainPercent` | int | *(left alone)* | 0-100 of the card's own capture range |
| `agc` | bool | *(left alone)* | Automatic gain control. **Recommended off for a data modem** |
| `micBoost` | bool | *(left alone)* | Microphone boost. **Recommended off** unless the radio's output is genuinely low |
| `playbackPercent` | int | *(left alone)* | 0-100, the level the radio is driven at |
| `card` | string | *(from `device`)* | The mixer card, if not the one `device` implies |
| `captureControls` | array | `["Mic", "Mic Capture", "Capture"]` | Names to look for the capture gain under, in order |
| `agcControls` | array | `["Auto Gain Control", "AGC", "Mic AGC"]` | Names to look for the AGC switch under |
| `micBoostControls` | array | `["Mic Boost", "Mic Boost (+20dB)", "Internal Mic Boost", "Mic Capture Boost"]` | Names to look for the boost under |
| `playbackControls` | array | `["Speaker", "PCM", "Master", "Headphone"]` | Names to look for the playback level under |

**Every key is optional and leaving one out leaves that control exactly as the card has it.**
That is the point of the section: it pins the settings that matter without taking over the ones
that do not, and a file with no `alsa` block behaves exactly as it did before this existed. There
are no hidden defaults. Only a sound card has a mixer, so an `alsa` section alongside a `flex:` or
`ubersdr:` device, or alongside `monitor`, is refused at start-up rather than silently ignored.

### Percentages, not dB

`captureGainPercent` and `playbackPercent` are percentages of the card's own range, which is what
`alsamixer` shows for the same control - so what you type here and what you see there agree.

dB was the alternative and was not chosen: ALSA can only set a level in dB on a card that
publishes a dB scale, and plenty do not, while every card with a volume at all takes a raw one. A
dB setting would therefore work on some cards and silently fail on others. The journal reports
the dB anyway, when the card knows one, because dB is what a level actually sits on:

```
alsa: mixer: Mic capture 60% / 9.00 dB (set 60%), Auto Gain Control off, Speaker playback 70% / -11.10 dB (set 70%)
```

Cards quantise, so **what is read back is the nearest step the card has, not the number you
typed**. A CM108's capture range is 36 steps: 60% lands exactly and comes back as 60%, while 45%
does not and comes back as 46%. Every line prints both the read-back and what was asked for, and
the read-back is the card's own answer rather than ours - as does the `percent` field of
[`/api/mixer`](#apimixer).

### Control names differ by card

There is no standard set of mixer control names. A CM108 calls its capture gain `Mic`; other
cards say `Mic Capture` or `Capture`. So each setting has a list of names and takes the first one
the card has, and the journal says which:

```
alsa: mixer: hw:3 has Mic, Auto Gain Control, Speaker
```

To see what yours is called, without stopping the station (reading a mixer does not touch the
PCM):

```
pdn-soundmodem --mixer-show hw:3
pdn-soundmodem --mixer-show plughw:CARD=Device,DEV=0
```

If your card names a control something none of the lists mention, put its name in the matching
`*Controls` list. The list replaces the built-in one, so include the fallbacks you still want.

**Many cards have no Mic Boost at all.** The CM108 revision on the bench folds its boost into the
top of the capture range (36 steps spanning -12 to +23 dB), so there is nothing separate to
switch. Asking for one on such a card is not an error; it is said once and skipped:

```
alsa: mixer: no control named "Mic Boost" on hw:3 (also tried "Mic Boost (+20dB)", "Internal Mic Boost", "Mic Capture Boost"), skipped
```

A control the file never mentioned is not reported missing - an absent key does nothing and says
nothing. **A card with no mixer at all does not stop the daemon**: it is said once and the station
carries on.

```
alsa: mixer: hw:3 has no mixer (snd_mixer_attach(hw:3): No such file or directory); capture gain, AGC and mic boost are left as the card has them
```

### Why AGC off

Automatic gain control fights the modem's own level tracking and turns the noise floor into a
moving target: it raises the gain in the gaps between frames and then pulls it down as a frame
arrives, which is exactly backwards for a demodulator that is measuring SNR and holding a
threshold. Mic Boost is +20 dB of gain ahead of everything, which is the wrong tool unless the
radio's output really is too low to reach the card's range - reach for the capture gain first and
watch the level meter on the waterfall page.

Both are recommendations, not forced defaults. The daemon writes neither unless the file says so.

### The mixer is read at every start-up

On a sound card the mixer is opened and read whether or not the file asks for anything, so the
journal records the level the station is actually listening at. Nothing is written to the card
unless a key said so.

### Setting it while watching the waterfall

With an [`api`](#api) key set, the operator page grows a **Mixer** group beside the display levels:
a capture-gain slider with the card's read-back beside it, and AGC and Mic Boost buttons. It is
never on the public page. See [`api`](#api) for `/api/mixer` and what it does and does not
persist.

## Channel access is the host's, not the config's

There is deliberately **no `csma` section**. TXDELAY, P (persistence), SLOTTIME and TXTAIL are
the host's to set, and it sets them at runtime with the standard KISS parameter commands -
`0x01`, `0x02`, `0x03` and `0x04`. The daemon honours all four the moment they arrive, which
QtSoundModem does not.

Until a host sends them, these are in force:

| Parameter | Default | Notes |
|---|---|---|
| TXDELAY | 300 ms | Key-up to first data. A *radio* allowance - the modems themselves acquire from 0-20 ms; 300 ms budgets a real transmitter's PTT-to-RF settling, which FM gear routinely needs |
| P (persistence) | 63 | ≈ 25 % chance of transmitting per slot |
| SLOTTIME | 100 ms | Gap between persistence rolls |
| TXTAIL | 20 ms | Carrier held after the last bit |

`--txdelay MS` overrides the TXDELAY default, for bench runs with no host attached.

**Scope is the radio, not the modem.** There is one PTT, so these settings apply to the whole
channel: a host on one modem's dedicated port that sends TXDELAY changes it for every modem,
and with several clients connected the last one to send a parameter frame wins.

> A `"csma"` block from an earlier version is not a setting any more; the daemon flags it as
> unknown at start-up rather than silently reverting a link you had tuned.

## `waterfall`

A live spectrum and waterfall page - 30 fps, every modem's measured band overlaid, each
decoded frame tagged on its burst with callsign, SNR and frequency offset. Genuinely useful
for confirming you are hearing the band at a sane level before trusting the decoder.

```json
"waterfall": { "port": 8107, "dialFrequencyHz": 14105000, "sideband": "usb" }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `port` | int | `8107` | HTTP listen port |
| `dialFrequencyHz` | number | `0` | Rig dial the RF scale derives from; 0 = audio frequencies only |
| `sideband` | string | `"usb"` | `"usb"` (RF = dial + audio) or `"lsb"` (RF = dial − audio) |
| `linesPerSecond` | int | `30` | Waterfall line / display frame rate |
| `fftSize` | int | `0` | 0 = rate default (2048 at 12 kHz, 8192 at 48 kHz) |
| `public` | bool | `false` | Dress the page for the public rather than the operator; see below |
| `title` | string | *(none)* | Public page title, in the tab and the top bar |
| `about` | string | *(none)* | One paragraph for the visitor, shown under the top bar |

**`public` is for a page on the open internet.** It takes the `title` and `about`, credits and
links the web receiver the station listens through (when the device is `ubersdr:`), and hides
the KISS host badges, which name ports on a box a visitor cannot reach, and the links pane's
*Mine* filter, which keeps only the links this station is one end of and means nothing on a page
whose receiver never transmits. Nothing else changes,
and nothing is removed from the operator's page; the waterfall, the links pane, the decoded
frames and *Listen* all stay. The natural pairing is `"ubersdr": { "onDemand": true }`, so the
receiver is only asked for while somebody is looking:

```json
"device": "ubersdr:m9psy-1.instance.ubersdr.org",
"ubersdr": { "onDemand": true, "lingerSeconds": 60 },
"waterfall": { "port": 8099, "public": true, "title": "40 m packet monitor",
               "about": "The 7050-7052 kHz packet window, receive only." }
```

**The page can play the received audio.** Press *Listen* in the top bar and the station's
receive audio streams to the browser, so you can hear the channel you are watching - an SSB
signal, a burst you are about to decode, or the noise floor you are arguing with. It is
per-viewer and **off until asked for**, so opening the page to look at a waterfall does not
quietly start pulling ~24 KB/s, and several viewers cost nothing unless they each ask.

Nothing is received while the station transmits, so the audio stops for the length of a keyup.
That is silence, not a dropout.

**Every page is asked whether it is still there.** The server sends each open page a keep-alive
every 20 seconds and stops counting one that has said nothing at all for 60 seconds, dropping its
socket without a close handshake and writing one line: `page: viewer dropped, no reply for 60 s,
0 viewers` (with the station's slug in front of it on a monitor). Nothing to configure, and
nothing to notice on a page that is open - the page answers from its message handler, so a tab in
the background answers too, where a tab that had to speak first would not: browsers throttle a
background tab's timers and do not throttle its messages. A tab the browser has frozen outright
runs no JavaScript at all, so it is let go, and it reconnects when it wakes, which is the right
answer. It matters because a socket whose browser has gone does not always close: a phone that
goes to sleep, a laptop lid, a browser killed outright behind a Cloudflare tunnel all leave a
connection that looks alive from here for ever, and before this that page went on being counted
as a viewer - which, with `"ubersdr": { "onDemand": true }`, held somebody's receiver open all
night for nobody (#411).

**A tab that was already open when this release was deployed will churn until it is reloaded.**
It is running the old page, which knows nothing about the keep-alive and cannot answer it, so it
is dropped once a minute, shows "reconnecting" for two seconds and comes back, over and over, for
as long as it stays open - three journal lines a minute on a receiver that has a session, and on
one that is retrying it also cancels the backoff and reopens, so the ladder's 300 s cap becomes an
attempt a minute. Nothing on this side can reach it: its JavaScript was fixed in the browser when
it loaded. Reload the tab, or close it, and it stops. From this release on the page fixes itself:
it compares the version the server announces against its own on every message the server sends it
its settings in, not only the first, so a tab left open across a future upgrade reloads itself two
seconds after the restart, before any deadline. A `page: viewer dropped` line arriving on a
metronome every 62 seconds from one station is a stale tab, not the keep-alive misbehaving.

**Your own transmissions are drawn.** Receive processing is gated off while transmitting - half
duplex - so the waterfall used to freeze for the length of every keyup, which quietly compressed
its time axis: a three-second transmission left no gap, and signals either side of it ended up
drawn adjacent. Transmitted audio is now fed to the display too, in its own colour ramp, so a
keyup occupies its real duration and reads as *yours* rather than as a strong station. The stats
line says `transmitting` from knowing rather than from guessing at silence.

It is drawn only. Transmitted audio is kept out of the SNR trackers - measuring your own
transmitter would report an enormous burst and attribute it to whatever decoded next - and is
not sent to the audio stream.

**The panel opens on what the station has already done**, when a [`frameLog`](#framelog) is
configured: the last 50 logged frames - heard and sent alike - dimmed apart from live traffic and
stamped with the time they happened rather than the time the page was opened. A panel that starts
empty says nothing about a channel that has been busy all morning, and on a quiet band it is
indistinguishable from a modem that is not working. Without a frame log the panel opens empty, as
before - there is nothing written down to show. A backlogged transmission keeps its **TX** badge
and its cyan edge as well as the dimming, so it reads as both: yours, and from before you opened
the page. The backlog is listed, never tagged onto the waterfall: those frames happened before
the scroll on screen began and belong to no burst on it. Reconnecting rebuilds the panel from the
log rather than stacking a second copy of the same frames.

**The transmit readout holds the last burst.** On a radio that reports its meters (a Flex), the
header carries forward power and SWR. While the transmitter is up the figures are live and the
readout is red; at key-up they are replaced by the **average over that transmission** and the
readout goes neutral, labelled `Last TX` with the time it was taken. Nothing clears it until the
next transmission. Packet bursts are a fraction of a second and the gaps between them are minutes,
so a readout that existed only during the keyup was one nobody ever managed to read - and holding
figures without saying they are held would be worse, which is why the state is said in words as
well as in colour. An SWR of 2.0 or more is flagged in either state.

**Each modem's label says whether a host is attached.** The chips under the header carry the KISS
attachment state - `1 host`, `2 hosts` or `no host` - covering both that modem's dedicated port
and the multiplexed one, since either can reach it; the tooltip breaks it down by port. A node
that quietly drops its TCP session stops passing traffic, and from the modem's side that is
indistinguishable from a band that went quiet: the journal says so once, at the moment it happens,
and then scrolls away. This follows clients in and out live.

**Your own frames are listed too**, in the decoded-frames panel, marked **TX** and styled apart
so a transmission can never be misread as a station heard. The panel was a record of half the
channel until then: everything heard, nothing sent, so an operator watching their own beacon go
out had only the burst to go on. A frame is listed once its audio has left, so what is shown
actually went on air. It carries no SNR, offset, FEC count or CRC verdict - those are receive
measurements, and we did not receive it - and it is not tagged onto the waterfall: the burst is
repainted from a queue in real time while the frame event fires as soon as the audio device took
the audio, so a tag would land somewhere up the burst rather than on it.

Omit the section to disable it. `dialFrequencyHz` is only the page's opening default - each
browser can retune its own copy, and it is inherited from a band plan when there is one. The
waterfall binds to the top-level [`bind`](#kissport-and-bind) like everything else; there is no
authentication, so opening it beyond loopback means a reverse proxy or VPN.

**With a [`monitor`](#monitor) section this is the whole site's port**, not one station's: the
picker is served at `/`, each receiver's page under `/r/<slug>/`, and `public` is forced true
because a picker is a page for strangers by definition. `title` and `about` dress the picker and
every receiver's page alike, so the two flavours are configured the same way.

## `idBeacons`

A NinoTNC running one of the PSK SSB modes cannot identify itself in that mode - nothing else on
the channel would be able to read it as speech or as anything a human recognises - so it idents
*alongside* it instead. Per the [NinoTNC operator's
manual](https://tarpn.net/t/nino-tnc/n9600a/n9600a_operation.html): in 300 BPSK, 600 QPSK, 1200
BPSK and 2400 QPSK, "a beacon in 300 AFSK AX.25 (1600/1800 Hz tones) is sent", from the host's
callsign to `IDENT`, every 9.5 minutes by default while the station is transmitting. (The modes
that are already self-identifying - 300 AFSK AX.25, 300 AFSK IL2Pc, 1200 AFSK AX.25 - send no
such beacon.)

The consequence for everyone else on the channel is a burst every few minutes that they can see
and cannot read: their data modem is a PSK demodulator and the ident is FSK. So for each PSK SSB
modem you configure, the daemon attaches a *ghost* - a second, receive-only 300 AFSK receiver
whose only job is that beacon:

```json
"idBeacons": true
```

Set it to `false` to turn them off. On by default; a station running none of those four modes is
unaffected either way.

**Where the ghost listens** follows the modem it accompanies. Nino's PSK modes phase-modulate a
1500 Hz tone and the beacon tones are 1600/1800, so the ident sits 200 Hz above the carrier -
*relative to the transmitting TNC's own audio layout*, which is the part that matters. Tune your
modem to 1200 Hz because your dial sits 300 Hz above your neighbour's, and their ident arrives at
your 1400 Hz, not at 1700. The daemon does that arithmetic; you do not place ghosts yourself. It
prints where each one landed at start-up:

```
modem 0: bpsk300 @ 1500 Hz
modem 0: id beacons - listening in afsk300-multi11 @ 1700 Hz
```

A ghost is whatever [`afsk300`](modes.md) currently means, which since 2026-08-02 is the
narrow-branch frequency-diversity bank rather than one wide demodulator - and that matters more
here than it does on a data slot. A ghost sits 200 Hz from a PSK carrier *by construction*, and a
quadrature discriminator follows the strongest thing in its passband, so tight branches are what
keep the neighbour it lives beside out of the ident. The bank's ±175 Hz of coverage also happens
to be the right shape for the only error this placement really has - a dial that differs from the
transmitting station's. (The offset from *their* carrier to *their* ident is fixed inside one TNC
and cannot drift.)

**What a ghost deliberately is not.** It takes no KISS sub-channel - beacons are not traffic, and
a host asking for packet data should not have to filter idents out of it. It does not contribute
to carrier sense, so channel access behaves exactly as it did without it. And it never transmits:
this is only a listener, for *other* stations' identifications. To identify your own station, see
[`identify`](#identify), which is a per-modem transmit setting and has nothing to do with this
one beyond the word.

Idents appear in the waterfall's decoded-frames panel with an **ID** badge, are tagged onto their
burst on the waterfall like any other frame - reading `KK4HEJ · ID` - and land in the
[`frameLog`](#framelog) as `ID beacon (AFSK300)`. What a ghost does *not* get is a **band** of its
own: it has no slot to shade, riding as it does on a modem that is already drawn, so its tag lands
against that modem's band, which is where the ident sits - a couple of hundred Hz above it. That
is also why an ident's tag says `ID`: without the word it would read as a station sitting *on* the
slot rather than identifying beside it.

An ident carries no SNR figure - the waterfall's per-burst SNR comes from a band tracker, the
trackers are keyed by modem, and a ghost shares its base modem's rather than having one. It does
carry a **frequency offset**, which is a real measurement of the identifying station's carrier
against the ghost's centre, so it tells you how far their dial sits from yours.

## `api`

Change this station's configuration at runtime, over HTTP:

```json
"api": { "key": "a-long-random-string" },
"waterfall": { "port": 8099 }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `key` | string | *(none)* | The shared secret every request must present. No key, no API |

Served on the [`waterfall`](#waterfall)'s listener under `/api/`, so a station that already
publishes a waterfall gains this on the same port rather than a second one to open. An `api`
section without a `waterfall` section is refused at start-up: there would be no listener to hang
it on, and a setting that silently does nothing is worse than one that says so.

**Present the key** as `Authorization: Bearer KEY` or `X-API-Key: KEY`. Compared in fixed time,
because the socket may be reachable from the LAN and a byte-at-a-time comparison leaks a secret a
byte at a time. There is no unauthenticated mode and no default key.

> **This can change frequency and transmit power**, which makes it a bigger gun than the
> waterfall it shares a socket with. The same warning that applies to KISS applies here and more
> so: anything that can reach the port *and holds the key* can retune and key your transmitter.
> Bind the waterfall to loopback and reach it over SSH if the station is anywhere exposed.

```
GET  /api/config                     what this process is running
POST /api/config                     replace it for one run
POST /api/config?persist=true        replace it and write the config file
GET  /api/mixer                      the sound card's mixer, as it reads back now
POST /api/mixer                      set it, live, for one run
POST /api/mixer?persist=true         set it and write the config file
```

`GET` reports the running configuration, the config file's path, and whether the station is
running the file or a one-run change. The `api.key` is blanked in the reply - the caller already
knows it, and the point is to keep it out of the scrollback, the pasted diagnostic and the
screenshot.

`POST` takes a **complete** configuration document, the same shape as `soundmodem.json`. It is
not a patch: send what you want the station to be.

**Validate and decline is the whole point.** The proposal is parsed and band-planned *before*
anything is written, through the code the start-up path uses, and a bad one comes back as `400`
with the message the journal would have carried - while the running station carries on untouched:

```
$ curl -sX POST -H "X-API-Key: $KEY" --data @new.json http://radio:8099/api/config
modem 3: unknown mode 'freedv-datac3 '. Check the spelling against docs/modes.md.
```

That is a real example. Editing the file by hand, the same stray space produced a correct refusal
with a much worse consequence: exit 2, which `RestartPreventExitStatus=2` deliberately does not
retry, and a production node down until somebody noticed. Validation is not exhaustive - a sound
card that has been unplugged is discovered only by trying - which is what the default below
insures against.

**Applying is a full rebuild.** Nothing is mutated in place. Adding a 48 kHz mode to a 12 kHz
station rebuilds every modem anyway (the channel's DSP rate is chosen once, from the modem set),
so the daemon writes the new configuration and restarts onto it. Expect a few seconds of
interruption, and note that KISS clients are disconnected and reconnect.

**A change is one-run unless you say otherwise.** By default it lives in the state directory and
is consumed by the next start-up - read once, deleted immediately - so it is in force for exactly
one run. Any later restart (a crash, a reboot, a `systemctl restart`) returns the station to its
config file. **An experiment that goes wrong therefore self-heals**, and the config file stays the
description of the intended station. While one is in force the daemon says so on every start-up:

```
api: this station is running a ONE-RUN configuration applied over the API, not
     /etc/pdn-soundmodem/soundmodem.json. Any restart from here returns it to the file.
```

`?persist=true` writes `soundmodem.json` instead, for a change meant to outlive the session. The
shipped unit allows this with `ReadWritePaths=/etc/pdn-soundmodem`; on a hand-written unit with
`ProtectSystem=full` and no such line the write is refused and the reply says so.

**Run without systemd and there is nothing to restart the daemon**, so an applied change stops it
rather than reloading it. The daemon warns at start-up when it cannot see systemd around it.

### `/api/mixer`

The sound card's [mixer](#alsa), for a script or for the operator page's Mixer group. Same key,
same one-run default, and **no restart**: a mixer setting lands on the card as the request is
served, the PCM stream is not touched, and restarting a station to trim its own capture gain
would drop the very waterfall the operator is trimming it against.

```
$ curl -s -H "X-API-Key: $KEY" http://radio:8107/api/mixer
{
  "available": true,
  "card": "hw:3",
  "controls": [ "Mic", "Auto Gain Control", "Speaker" ],
  "capture": { "control": "Mic", "percent": 57, "decibels": 7.95 },
  "playback": { "control": "Speaker", "percent": 46, "decibels": -19.98 },
  "agc": { "control": "Auto Gain Control", "on": true },
  "micBoost": null,
  "summary": "alsa: mixer: Mic capture 57% / 7.95 dB, Auto Gain Control on, Speaker playback 46% / -19.98 dB"
}
$ curl -sX POST -H "X-API-Key: $KEY" -d '{"captureGainPercent": 70, "agc": false}' http://radio:8107/api/mixer
```

`POST` takes only the settings you want changed, unlike `/api/config`, which takes a whole
document. A control the body does not mention is left exactly as the card has it. Percentages
outside 0-100 come back as `400` with the same sentence the config file's refusal carries.

A control the card has not got reads back as `null` - `micBoost` above is a CM108 that folds its
boost into the capture range - and a station with no sound card at all answers
`{"available": false, "why": "..."}` rather than a 404, so a caller can tell "no mixer here" from
"no such endpoint". `POST` to one of those is a `409`.

`?persist=true` writes the config file, but **only when writing it back would lose nothing**. A
config file here is JSONC and most are full of comments, and this daemon does not serialise a
parsed document over the top of somebody's notes. So a commented file is left exactly as it is,
the card is still set for this run, and the reply says what to paste in to keep it:

```
"note": "/etc/pdn-soundmodem/soundmodem.json has comments or trailing commas in it, and this
         daemon never writes a config file back from a parsed document - it would delete them,
         so it was NOT written. To keep this, add {\"alsa\":{\"mixer\":{\"captureGainPercent\":45}}}
         to it by hand. in force until the next restart, then the config file applies again"
```

The operator page therefore never persists: a trim you want to keep is worth a deliberate line in
the file.

## `frameLog`

Everything the station hears **and everything it sends**, written to a SQLite file:

```json
"frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" }
```

Omit the section and frames come and go without being written down. One row per frame:

| Column | What it holds |
|---|---|
| `heard_at` | UTC, ISO 8601 |
| `direction` | `rx` for a frame the station heard, `tx` for one it sent |
| `sub_channel`, `mode`, `mode_name` | which modem carried it, and what it is - `bpsk300-il2pc` and `BPSK300 IL2Pc` |
| `source`, `destination` | AX.25 callsigns where the frame carries them; null where it does not |
| `length`, `corrected`, `crc_valid` | size, FEC corrections applied, whether the CRC checked - null on a received frame means there was no CRC to check, which on an IL2P+CRC modem means it was read as [plain IL2P](#acceptplainil2p); the next three columns say what it was read as and what became of it |
| `plain_il2p` | 1 for a frame read as [plain IL2P](#acceptplainil2p), with no trailing CRC behind it; 0 for one that was not, which is every other receive there is - an IL2P+CRC frame, and equally an HDLC, an FX.25 or an ARDOP one. So `plain_il2p = 0` means "not read RS-only" and **not** "verified by a CRC": on those last three there was no trailing CRC to check either, and `crc_valid` is null beside the 0. A fact about the decode rather than about what became of the frame, which is why it is not the same column as `monitor_only` - and its own column rather than a null `crc_valid`, which is also null on HDLC, on FX.25, on ARDOP and on transmitted rows. What the waterfall panel draws its **`RS ONLY`** badge from, live and on the opening backlog alike. Null on transmitted rows and on rows logged before the column existed |
| `trailer_near_bits` | on a frame delivered by [trailer corroboration](#acceptplainil2p): the measured distance, in wire bits, between the 32 trailer bits received and the trailer the payload implies (0-4). Null on every other row, so a `crc_valid`-null frame with this set is one the host received on the trailer's evidence |
| `monitor_only` | 1 for a frame the station read, showed and withheld from the host; 0 for one the host received. Null on transmitted rows - withholding is a receive event - and on rows logged before the column existed, where it was not written down |
| `erased_bytes` | bytes the decode erased on the receiver's own confidence flags before Reed-Solomon repaired the frame - how a frame beyond the errors-only budget was still read. Null when no erasures were needed, or before the column existed |
| `chased_bits` | wire bits chase decoding flipped outright - the receiver's least-confident bits, tried in combination after errors-only decoding and the erasure ladder both failed, each accepted attempt still leaving Reed-Solomon parity in reserve. The only rescue the 2-parity IL2P header has. Null when no chase was needed, or before the column existed |
| `snr_db` | strength of the burst the frame arrived on: mean in-band power over the burst against a rolling minimum noise floor, in dB. **The band-tracker convention, not the 3 kHz-referenced SNR the simulation ladders quote** - the two differ by a bandwidth ratio and must not be compared without converting. Null when the band was quiet at decode time, and on rows from before the column existed |
| `offset_hz` | how far off centre the sender actually was - measured, not the diversity branch that copied it; null where the decoder could not measure it |
| `audio_hz`, `rf_hz` | where that modem sits - `rf_hz` filled in when you have given it an `rfFrequency` |
| `payload` | the frame itself, as a blob |

**On a transmitted row, `heard_at` is when it went out.** The column keeps its name because
renaming it would silently break every query, dashboard and example already written against this
log - an ugly name is the smaller cost, and this is the note that stops it being a surprise. A
transmitted row also leaves `corrected`, `crc_valid` and `offset_hz` **null**: those are receive
measurements, and filling them in for our own transmission would be inventing a measurement of
ourselves. Everything else - who to who, mode, length, where the modem sits, the payload - is
recorded exactly as for a frame heard. A row is written once the audio has gone to the device, so
a logged transmission is one that actually went on air.

So "who have I heard on 40m today" is a query - and it now has to say that it means *heard*,
since your own frames are in the table too:

```sql
SELECT source, COUNT(*), MAX(heard_at)
FROM frames WHERE direction = 'rx' AND rf_hz > 7000000 AND rf_hz < 7300000
GROUP BY source ORDER BY 2 DESC;
```

Drop the `direction` clause and you are asking who has been on the channel, yourself included,
which is a fair question too - just not the same one. A log written by an earlier version is
migrated in place on first open: the column is added with every existing row set to `rx`, which
is what they all were.

**It is written on a background thread** - the receive path queues and returns, so logging never
delays a decode. If the disk fills or goes away the modem keeps decoding and drops log rows
rather than stopping. **The file is WAL**, so you can read it with `sqlite3` or point a dashboard
at it while the modem is still running and writing.

**The [`waterfall`](#waterfall) reads it too**: with a frame log configured, the decoded-frames
panel opens on the last 50 rows instead of on nothing. That read takes its own short-lived
connection, so a browser arriving never touches the writer, and a log that cannot be read costs
that browser its backlog and nothing else. The backlog carries both directions: a transmitted row
comes back marked **TX** and styled apart exactly as a live transmission is, so a reload shows
what the station has been doing rather than only what it has been hearing.

The packaged service runs unprivileged and the unit declares `StateDirectory=pdn-soundmodem`, so
systemd creates `/var/lib/pdn-soundmodem/` owned by the service user and the default path just
works. If you move it, the service user has to be able to write to wherever you move it to - the
daemon says so plainly at start-up rather than running without a log you asked for.

## `survey`

Watch the whole passband for transmissions this station cannot read, and keep the ones worth
looking at later:

```json
"survey": { "path": "/var/lib/pdn-soundmodem/survey" }
```

Omit the section and signals you are not configured to decode go unrecorded, which is what
happens today: they paint on the waterfall and are lost. This turns "something went past on
7.050594 and I will never know what it was" into a WAV and a JSON sidecar.

**Why not simply run more modems.** The obvious answer is a comb of decoders across the passband,
and it fails for a reason that is not CPU: the mode is unknown too, so brute force is centres ×
modes - afsk300 in three framings, bpsk300, 850 Hz FSK, 150 Hz-shift FSK, non-NinoTNC soundmodems -
at every centre you might try, and it is still silent when it guesses wrong. Energy, meanwhile,
is already being computed for the display.

**What it keeps.** A burst is packet-shaped if it started, stopped, and was neither too narrow
(a carrier or a het) nor too long (see `maxSeconds` below). Three kinds are written out:

| Verdict | What it means |
|---|---|
| `unclaimed` | Outside every configured modem's band. Nobody was listening there. |
| `missed` | Inside one, and nothing decoded. **The most useful of the three** - the station was listening and could not read it, which is a receiver problem rather than a coverage one, and is invisible today unless you happen to be recording. |
| `unattributed` | A frame decoded carrying no readable AX.25 addresses. The sidecar carries its bytes, the IL2P encapsulation it arrived in (Type 1 translated or Type 0 transparent - they put the address field in different places) and a line saying exactly what would not read, so the payload, the reason and the modulation can be examined together. |

Nothing is captured while the station transmits, and normal traffic on your own slots is not
captured at all.

| Field | Type | Default | Notes |
|---|---|---|---|
| `path` | string | `/var/lib/pdn-soundmodem/survey` | Where captures are written |
| `maxBytes` | number | `536870912` (512 MB) | Byte budget for that directory |
| `maxPerHour` | int | `30` | Captures in any rolling hour |
| `cooldownSeconds` | number | `120` | How long the same part of the spectrum is left alone after a capture |
| `marginSeconds` | number | `1.0` | Audio kept either side of the burst |
| `decodeClaimSeconds` | number | `5.0` | How long a Missed capture waits for a decode to claim it as a fade-split fragment of its own transmission before it is written. A frame's decode lands at the END of the transmission, seconds after a fragment closes; 0 writes at once and files every such fragment as a miss |
| `maxSeconds` | number | `20` | Longest burst still plausibly a packet |
| `minPeakSnrDb` | number | `6` | Weakest burst worth keeping, over the noise floor |
| `capture` | array | all three | Which verdicts to write - `unclaimed`, `missed`, `unattributed` |
| `propose` | bool | `false` | Read each capture back and propose the modems that would have read it - [below](#proposing-modems) |
| `proposeMinCaptures` | int | `3` | Separate occasions a proposal needs behind it |

All three reach the **waterfall's decoded-frames panel** too, which is where an operator sees the
word "unattributed" in the first place: the row carries the IL2P type, the reason, and the frame's
bytes laid out to be selected and pasted.

The same explanation reaches the journal whether or not a survey is running - the `rx` line for
such a frame carries `il2p Type1` and the reason in brackets, because the survey is optional and
budgeted and may drop that particular burst.

## `metrics`

What this station hears, published for a monitoring system to come and collect.

```json
"metrics": { "enabled": true }
```

Served on the [waterfall](#waterfall)'s listener, so a station that already publishes a waterfall
gains this on the same port rather than another one to open. **There is no authentication**: what
is served is callsigns and signal reports that were transmitted in the clear on a shared channel,
which is what the waterfall page already shows anyone who opens it. It is off unless configured
all the same, because publishing is the operator's decision.

**Pull, and deliberately ignorant of your stack.** Nothing here knows the address, protocol or
credentials of any monitoring system. The station serves what it knows; whoever is interested
comes and reads it. That is what makes this generic rather than fitted to one operator's setup.

### Two endpoints, because one format cannot do both

| | | |
|---|---|---|
| `/metrics` | Prometheus text exposition | totals and sums, for rates, dashboards and alerting |
| `/metrics/frames` | InfluxDB line protocol | **one point per frame**, carrying the moment it was heard |

A scrape-and-aggregate format has one sample per series per scrape, so it cannot express two
frames a second apart. If you want to plot individual frames as points - a scatter of every frame
received, coloured by station - that is what the second endpoint is for, and it needs a collector
that can pull line protocol. Everything else is on the first.

### Metrics

Every per-station series carries `station` and `mode`.

| Metric | Type | |
|---|---|---|
| `pdn_station_info{station,mode,sub_channel}` | gauge | 1 per station and mode, with the sub-channel it was last heard on |
| `pdn_station_frames_total` | counter | Frames whose own check sequence verified |
| `pdn_station_bytes_total` | counter | Bytes in those frames |
| `pdn_station_snr_db_sum` | counter | Sum of per-frame SNR |
| `pdn_station_frames_with_snr_total` | counter | The divisor for it |
| `pdn_station_frequency_offset_above_hz_sum` | counter | Sum of how far each station sat **above** our centre |
| `pdn_station_frequency_offset_below_hz_sum` | counter | The same **below** it, as a positive number |
| `pdn_station_frames_with_offset_total` | counter | The divisor for the two of them |
| `pdn_station_corrected_bytes_total` | counter | Bytes Reed-Solomon repaired |
| `pdn_station_snr_db_last` | gauge | The most recent frame's SNR. A point reading |
| `pdn_station_frequency_offset_hz_last` | gauge | The most recent frame's offset, positive above centre. A point reading |
| `pdn_frames_uncounted_total` | counter | Decodes attributed to nobody |
| `pdn_stations` | gauge | Station-and-mode series currently held |

### Mode is on the series, not something to join to

The tidy Prometheus idiom is an info metric carrying the detail and a `group_left` join to bring
it in. That was the wrong choice here, twice over.

**A join is PromQL, and InfluxQL has none.** In a setup where Telegraf scrapes the same endpoint
into InfluxDB alongside Prometheus - which is a common shape, and the one this was built against
- the mode would be unreachable for every aggregate panel on the InfluxDB side.

**And it is not really a join.** One callsign heard on two modes is two links: different
frequency, different modem, different path budget. On the 40 m station `GB7BPQ` reads **15.2 dB
on `afsk300-il2pc` and 12.7 dB on `bpsk300-il2pc`**, and a single series spanning both was the
average of two unrelated measurements, describing neither.

### Sums and counts, not gauges, and why

The obvious design is a gauge holding each station's latest SNR, and it is a trap. A station
transmitting every few minutes against a fifteen-second scrape holds its reading across every
scrape in between, so a chart draws a flat line and a long-retention store keeps it for ever. One
sample smeared across an hour reads exactly like a continuous measurement of a quiet channel.

A sum and a count divide into the mean over whatever window you ask for, and produce **nothing at
all** when nothing was heard, which is the truth:

```promql
rate(pdn_station_snr_db_sum[$__rate_interval]) / rate(pdn_station_frames_with_snr_total[$__rate_interval])
```

`pdn_station_snr_db_last` is published for a "right now" readout and is named so that anyone
building a time series on it can see what they are doing.

### Frequency offset takes two sums, because it has a sign

How far a station sat from our centre is the measurement that shows a reference drifting over
days, and the one that says which way to nudge the dial. It is charted the same way as SNR, with
one difference forced on it: a station below our centre reads negative, so a single running sum
of those readings goes **down** - and a counter that goes down is a counter that reset. Prometheus
reads the drop as a restart and `rate()` returns a large positive number from the value after it.
That is the worst way for a metric to be wrong, because it looks right: on the 40 m station seven
of twelve station-and-mode pairs sat low, so the mean-offset chart was believable for the stations
above centre and nonsense for the ones below.

So the sums are split by sign - each one only ever climbs - and the mean is the difference over
the count:

```promql
(rate(pdn_station_frequency_offset_above_hz_sum[$__rate_interval])
 - rate(pdn_station_frequency_offset_below_hz_sum[$__rate_interval]))
/ rate(pdn_station_frames_with_offset_total[$__rate_interval])
```

Positive is above your centre, so the sign reads the way an operator expects: `+18 Hz` means they
are high and you would tune up to meet them.

`pdn_station_frequency_offset_hz_last` is the point reading, beside `pdn_station_snr_db_last`.

Only a modem that measures a carrier offset reports one, so a station heard on a single decoder
has no offset series at all - `pdn_station_frames_with_offset_total` stays at zero and the mean
divides to no point. That is deliberate: "not measured" must not chart as "dead on frequency".

### SSIDs are combined

`GB7IOW-1`, `GB7IOW-2` and `GB7IOW-9` are one transmitter, one antenna and one path, so the
`station` label is the base callsign and a chart does not draw one signal three times. The SSID
travels as a field on the individual frame, where the question "which one answered" is still
answerable.

Note the asymmetry with mode, and that it is deliberate: SSIDs combine because they are one link,
and modes separate because they are not.

### Only frames that vouched for themselves

A frame counts towards a station when its own FCS or IL2P CRC verified. This is not tidiness. Of
the 77 distinct callsigns the 40 m station had ever decoded, **45 were heard exactly once and not
one of those 45 ever had a valid check sequence** - they are corruptions of the regulars
(`EI0RSI-9`, `EI0RSA-12` and `EI0RSE-1` are all `EI0RSI-1` with a bit wrong; `7B7BPQ` is
`GB7BPQ`). All 21 stations heard twenty times or more had one. Without the gate, every bit error
the receiver ever makes mints a series that exists for ever and appears on a dashboard as a
station.

What was declined is counted in `pdn_frames_uncounted_total`, because a large number beside a
small station list is a receiver working at its limit and worth knowing about.

A station drops out of the exposition once it has not been heard for `stationIdleHours`, rather
than holding its last reading indefinitely.

### Options

| Field | Type | Default | Notes |
|---|---|---|---|
| `enabled` | bool | `true` | Serve the endpoints |
| `maxStations` | int | `256` | Cap; the least recently heard is dropped on reaching it |
| `frameWindowSeconds` | number | `300` | How long a frame stays in `/metrics/frames` |
| `stationIdleHours` | number | `6` | How long a station keeps its series after its last frame |

`frameWindowSeconds` must comfortably exceed your scrape interval. Scraping more slowly loses
frames; scraping faster sees some twice, which is harmless - a window is served rather than a
queue, so nothing is consumed by being read, and InfluxDB identifies a point by measurement, tags
and timestamp, so a repeated write of an identical point replaces it.

### Collecting it

Prometheus, or anything that scrapes Prometheus text (VictoriaMetrics, Grafana Alloy, the
OpenTelemetry collector, Telegraf's `inputs.prometheus`):

```yaml
  - job_name: pdn-soundmodem
    static_configs:
      - targets: ['station.example:8099']
```

The per-frame feed needs a collector that pulls line protocol. With Telegraf:

```toml
[[inputs.http]]
  urls = ["http://station.example:8099/metrics/frames"]
  interval = "10s"
  data_format = "influx"
```

A ready-made Grafana dashboard is in [`docs/grafana/pdn-soundmodem.json`](grafana/pdn-soundmodem.json),
including the per-frame scatter. It picks its datasources through variables rather than hardcoding
them, so it imports against whatever yours are called.

### Proposing modems

A survey answers "something went past that I could not read" and stops there, which is a
diagnosis with no prescription. The 40 m station produced **14,267 captures in three weeks**, and
two of them opened by hand on 2026-08-24 turned out to be the same station beaconing every twenty
minutes in a mode the station could read and simply was not configured for. Nobody is going to
open fourteen thousand WAVs.

```json
"survey": { "path": "/var/lib/pdn-soundmodem/survey", "propose": true }
```

With this on, each capture is read back with every mode that could have carried it, pointed at
the centre the survey already measured. What decodes is clustered by mode and frequency, and once
a cluster has enough separate occasions behind it the station says what it would take to read the
traffic:

```
propose: add afsk300 at 7.050570 MHz - 34 frame(s) in 34 capture(s), PD4R-12, 19 dB,
         2026-08-06 to 2026-08-24
```

**Two shapes of answer**, and the second is the one worth having:

| Kind | Means |
|---|---|
| `NewModem` | Nobody is listening on that frequency. Add a modem. |
| `FramingChange` | A modem already covers it and cannot read the framing - it runs IL2P+CRC and the station sends plain AX.25, or the reverse. **The frequency is not the problem** and moving anything would make it worse. |

**What counts as evidence.** Separate captures - separate transmissions, on separate occasions -
each carrying a frame whose own FCS or CRC verified. Not distinct frames: the traffic this finds
is largely beacons, and a beacon is the same bytes every twenty minutes for ever. And not
Reed-Solomon-only readings: running thirty receivers over every capture is thirty chances to find
structure that is not there, so a reading with no verified check sequence is recorded and cannot
be what commits a modem slot.

**What it costs.** One capture at a time, on one thread below normal priority, with a sleep after
each one of nineteen times what that capture took to sweep. That bounds it at a twentieth of one
core whatever the station is hearing, and a slower box makes it slower rather than busier. A
backlog is dropped rather than queued, and counted. The receive path is never waited on.

**Acting on one.** With an [`api`](#api) key set, `GET /api/proposals` returns each proposal with
its evidence *and the complete configuration that would apply it* - the modem entry already
spelled, on the lowest free sub-channel:

```
curl -s -H "X-API-Key: $KEY" http://station:8099/api/proposals | jq '.proposals[0].summary'
```

To act on one, POST the `config` it carries back to `/api/config`, which is the ordinary
configuration path: validated before anything is written, refused with the same wording an
operator's own edit would get, and **ephemeral by default** so a proposal that turns out to be
wrong self-heals at the next restart. There is deliberately no apply endpoint here - a second way
to change a station is a second set of rules to keep in step, and the first set is the one
carrying the safety property.

Proposals also reach the journal as they are made, so a station with no API key still tells you.

**On reaching the byte budget the oldest captures are deleted** to make room. That is a real
choice, and the alternative is worse: stopping instead would mean a station left collecting for a
week quietly stops on day one and you find an empty tail. A flight recorder keeps the recent past;
so does this. Raise `maxBytes` if you would rather keep more of it.

**Duration, not width, is what separates a voice contact from a wideband data burst** - both
occupy much the same 2.4 kHz. An over runs for tens of seconds and the longest frame these modes
can carry does not, so `maxSeconds` is the knob that matters if voice is getting through.

**It is audio, not IQ.** On a Flex the daemon receives demodulated SSB over DAX; the complex
baseband is gone before the modem sees it. That is fine and is the right artefact - the channel
audio *is* the whole configured passband, it is exactly what every modem sees, and it
re-demodulates offline at any centre inside it.

**With a [`waterfall`](#waterfall) configured, captures appear on the page.** Each one is bracketed
on the scroll at the frequencies and for the duration it actually occupied - a capture has a
frequency and a time, which is exactly what that display's two axes are, so "something we could not
read went past *there*" is a statement the waterfall can make and a list of filenames cannot - and
listed in the panel with links to its audio and its sidecar. The panel header also carries a running
`survey N · M skipped · X MB`, which is the only place the **skipped** count appears: a station left
collecting for a week silently becomes a sample rather than the set when the channel is busier than
`maxPerHour`, and counting files by hand was the alternative.

Serving those files is the one route on the waterfall that reads from disk. Only the exact
`YYYYMMDD-HHMMSS-NNNNhz-verdict.wav|json` shape the writer produces is served, and only out of the
survey directory - a name is not a path. There is still no authentication on the waterfall, so the
reverse-proxy or VPN advice in that section is load-bearing rather than tidy-minded now that
recorded audio is reachable over it.

Each capture is a `.wav` and a `.json` of the same name, stamped with the time, the measured
centre and the verdict:

```
20260804-151909-862hz-unclaimed.wav
20260804-151909-862hz-unclaimed.json
```

The sidecar records the measured centre, edges, width, duration and SNR, the RF frequency where
the dial is known, the sample rate, and which modems the station was running at the time - so a
capture read months later still says what "unclaimed" meant. Point `sm-decode` at the WAV, or any
mode's demodulator at the sidecar's centre, to find out what it was.

## `rawCapture`

Continuous recording of the channel's receive audio - everything, not just detected bursts -
as chunked 16-bit mono WAVs at the channel DSP rate:

```json
"rawCapture": { "path": "/var/lib/pdn-soundmodem/raw", "maxBytes": 4294967296, "chunkMinutes": 15 }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `path` | string | `/var/lib/pdn-soundmodem/raw` | Where chunks land, named `raw-<UTC>.wav` |
| `maxBytes` | number | 4 GiB | Directory budget; the oldest chunks are pruned to fit |
| `chunkMinutes` | int | `15` | Audio minutes per chunk |

The [survey](#survey) and this answer different questions. The survey curates: bursts the
station could not read, rate-limited and cooled down, cheap enough to leave on for months. Raw
capture keeps the unedited stream so the **whole run can be re-scored offline against a later
receiver** - including everything a burst detector would have missed, which is precisely what a
receiver-improvement campaign needs a corpus of. It costs disk by design: about 2 GB per day at
a 12 kHz DSP rate, 8.6 GB at 48 kHz. Size `maxBytes` to the disk and the campaign.

Chunks are always readable WAVs, even after a crash or power cut, to within a few seconds of
the end. Recording pauses while the station transmits (its own transmissions are not receive
evidence), and a disk failure disables recording with one journal line rather than stopping
the modems.

## `paging`

A POCSAG paging endpoint (DAPNET-compatible waveform). Pages are not AX.25 frames, so they
get a line-based TCP service of their own rather than a KISS port.

```json
"paging": { "port": 8106, "baud": 1200, "invertPolarity": false }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `port` | int | `8106` | Paging TCP listen port |
| `baud` | int | `1200` | `512`, `1200` (DAPNET) or `2400` |
| `invertPolarity` | bool | `false` | For radios whose data path inverts |

The grammar is one UTF-8 command per line - `PAGE <ric> <function> ALPHA|NUMERIC|TONE [text]`,
replying `OK <id>` or `ERR <reason>`. Every page heard on channel is broadcast to all
connected clients as a `HEARD …` line. Transmissions share the CSMA/PTT path with the packet
modems.

## `ardop`

An ARDOP virtual TNC with an ardopcf-compatible host interface, so Pat, Winlink Express,
ARIM/gARIM and hamChat connect unmodified. **It is a modem entry like any other** - it shares
the passband with the packet modes rather than excluding them:

```json
"captureRate": 12000,
"modems": [
  { "subChannel": 0, "mode": "afsk300-il2pc", "frequency": 300,  "port": 8100 },
  { "subChannel": 1, "mode": "ardop",         "frequency": 950,  "port": 8101 },
  { "subChannel": 2, "mode": "bpsk300",       "frequency": 1600, "port": 8103 }
]
```

Three modes in one 3 kHz passband on one radio, each on its own centre and its own host port.

**`port` here speaks ardopcf, not KISS.** ARDOP is a connected-mode ARQ protocol with session
semantics; there is no way to express a session over KISS, which carries AX.25 frames and knows
nothing of connections. So the entry gets the ardopcf host interface: **command on `port`, data
always on `port + 1`.** That reservation is real and the daemon enforces it - `8103` above is
not a typo, because `8102` belongs to ARDOP's data port.

**`frequency` moves it off 1500 Hz.** ARDOP's waveforms are pinned to a 1500 Hz centre and the
underlying library exposes no way to move them, so the daemon shifts the audio outside the TNC:
transmitted audio is mixed from 1500 Hz to your centre, and received audio mixed back before the
TNC sees it. The TNC never knows. Choose the centre with ARDOP's *negotiated* bandwidth in mind -
up to 2000 Hz - and the daemon warns at start-up when a centre cannot fit the widest session
inside a nominal 300-2700 Hz SSB passband.

**Sharing the radio with an ARQ session.** ARDOP owns the channel's timing while a session is
up: an AX.25 frame landing mid-turnaround breaks it. So while the ARQ engine is connected or
connecting, packet transmissions are **held** - queued, not discarded. A frame held longer than
30 seconds is then rejected rather than escaping minutes late as a duplicate, since an AX.25
host will have retried long before a Winlink session ends. Receive is unaffected throughout:
every modem and ARDOP hear the channel simultaneously.

**ARDOP shares any channel.** Its engine is native 12 kHz, and on a 12 kHz channel (only
`afsk*`/`bpsk*`/`qpsk*` alongside it) that is the end of it. When a 48 kHz mode (`fsk9600`,
`c4fsk*`, `freedv-*`, `ms110d-*`) puts the whole channel at 48 kHz, the daemon bridges: ARDOP's
receive audio is decimated down to 12 kHz and its bursts upsampled back, outside the TNC, which
never knows. The start-up line says when this is happening
(`engine 12000 Hz bridged to the 48000 Hz channel`).

One ARDOP TNC per channel - it is a whole virtual TNC, not a demodulator you can run twice.

> The older top-level form still works and is folded into a modem entry at start-up:
> ```json
> "ardop": { "port": 8515 }
> ```
> It has no `frequency` and no `subChannel`, so prefer the modem entry. Configuring both at
> once is rejected.

See [docs/ardop-design.md](docs/ardop-design.md).

## `flex`

Slice parameters used **only** when `device` is a headless `flex:` string - that is, `flex:`
with no `@station`. Ignored for ALSA devices and for attach-mode Flex, where SmartSDR owns
the slice.

```json
"flex": { "frequency": "14.100000", "antenna": "ANT1", "mode": "DIGU", "daxChannel": "1" }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `frequency` | string | `"14.100000"` | MHz, six-decimal Flex form - a **string**, not a number. Superseded by a band plan |
| `antenna` | string | `"ANT1"` | RX/TX antenna |
| `mode` | string | `"DIGU"` | Slice demod mode |
| `daxChannel` | string | `"2"` headless, `"1"` attach | DAX channel to claim - [below](#coexisting-with-smartsdr) |
| `txPowerWatts` | number | unset | Transmit power in watts - [below](#transmit-power) |
| `transmitFilterHighHz` | int | *derived* | TX filter high cut in Hz - [below](#the-transmit-filter). `0` leaves the radio's own |
| `stationName` | string | `"pdn-soundmodem"` | The station name registered with the radio (best-effort), so per-station state and other operators' diagnostics name this daemon rather than a generic "Flex" |
| `arbitration` | bool | `false` | Key through the arbitrated PTT - [below](#sharing-the-radio-with-a-second-transmitting-client) |

The headless path disables band persistence and explicitly tunes the slice, so it lands on the
requested frequency regardless of the radio's last-used band.

### Sharing the radio with a second transmitting client

Two pdn processes can want one Flex: the production daemon plus a test instance, or the
sm-ota harness. Without arbitration the second client silently steals the TX slice - the
first then keys the wrong slice forever while logging success - and the radio-global transmit
filter is last-writer-wins. `"arbitration": true` keys through a sequence that emits no
radio-global write until the radio is quiet: wait for the interlock to clear, re-assert this
station's transmit filter, claim the TX slice, key, and only believe a keyup the radio
confirms. A keyup that cannot get the radio inside 10 s fails loudly (queued frames are
answered, the journal says another station holds the PA) instead of transmitting over
someone. Ordinary queued frames also defer before rendering while another station transmits,
the same polite hold ARDOP sessions get.

**Off by default** until the multi-client radio semantics are probed on real hardware
(docs/flex-integration.md § Sharing the PA names the nine probes); the default path is
bit-for-bit what it always was. Turning it on today is safe in the sense that it never
disturbs a busy radio - the unprobed risks are around losing races, not corrupting anyone.

**The slice mode states the sideband**, so do not also set `sideband`: `DIGU` is USB, `DIGL` is
LSB, and the daemon takes it from the slice. Setting a `sideband` that contradicts the slice mode
is rejected - silently accepting it would mirror every modem about the dial.

**A band plan supersedes `frequency`.** With `rfFrequency` modems the dial is computed, so a slice
frequency here would be saying two different things; the daemon warns and uses the plan.

### Transmit power

```json
"flex": { "antenna": "ANT1", "txPowerWatts": 30 }
```

Watts, not the radio's 0-100 number, because watts is what your licence is written in. Every
6000-series model has a 100 W PA, so on that family the two happen to coincide - the daemon
converts using the PA size regardless, so the config stays honest about its units.

**Unset, the radio's own setting is left alone** - the station transmits at whatever the rig is
on, exactly as it did before this setting existed. Either way the daemon prints what is in force
at startup, because an inherited power shapes every transmission just as much as a configured one:

```
flex: transmit power 30 W, limit 50 W
flex: transmit power 9 W, limit 50 W (radio's own setting)
```

**Above your Max Power Level, the radio refuses rather than reduces.** Asking for 30 W against a
15 W ceiling is an error - not 15 W, and not some fraction of it. The daemon reads the ceiling
first and fails at startup naming both numbers, so raise the limit at the rig (Settings →
Transmit → Max Power Level) or ask for less.

**Why this is here at all, rather than left to the rig.** RF power is held *per station*, and
only the client that owns the transmit slice can set it. In a headless station that client is
pdn-soundmodem. Anything else - SmartSDR on another machine, a command-line tool - has its
request answered `err=0` and silently discarded, and the value never moves. Measured on a
FLEX-6500 (fw 4.2.20.41343, 2026-08-02). So while the daemon holds the slice, the daemon is the
only thing that *can* set the power, which is why it has to be configurable here.

The setting persists on the radio after the daemon exits, like the transmit filter.

### The transmit filter

The radio's transmit filter decides how much of your audio reaches the air, and on a Flex it is a
**global, persistent** setting rather than a slice one: it is whatever last touched the radio, it
outlives the daemon, and anything wider than it goes out truncated with nothing said. So the
daemon states it rather than inheriting it - measuring what the configured modems actually occupy
and setting the high cut to clear the highest of them:

```
flex: setting the transmit filter high cut to 3400 Hz - modem 0 (ms110d-wn4) reaches 3199 Hz
flex: transmit filter 0..3400 Hz (radio global - limits TX audio bandwidth)
```

**This matters most for the wide modes.** `ms110d-*` is a 3 kHz waveform at a fixed 1800 Hz
centre, so it reaches past 3.1 kHz - a radio left on the usual 3000 Hz cut clips the top of every
burst. The audio-band packet modes all fit inside 3000 Hz and end up *narrowing* the filter
instead, which keeps transmitted noise off your neighbours' frequencies.

The widths are measured off the modems themselves (the same probe the waterfall draws and the
band planner fits with), so nothing has to be kept in step by hand.

| `transmitFilterHighHz` | What happens |
|---|---|
| unset | Derived from the modems, as above - or from the [band plan](#band-plans-in-rf-terms) when there is one |
| a number (500-10000) | Used as it stands; the band plan does not override it |
| `0` | The radio's own filter is left alone |

**The slice's receive filter is set too**, from the same measurement - the transmit filter decides
what leaves the radio, but what reaches the modems is capped separately, per slice, so widening only
the transmit side would give a wide signal out and an ordinary ~3 kHz window back in:

```
flex: setting the slice receive filter to 200-3400 Hz, to hear everything the modems are placed across
flex: slice receive filter 200..3400 Hz (what the modems can hear)
```

Unlike the transmit filter this is slice state rather than a global radio setting, so it goes away
with the slice. The radio's ceiling on receive width is **not measured** - the 10 kHz figure is the
transmit filter's - so the daemon asks, reads back, and warns if the radio would not go that wide
rather than leaving a modem quietly deaf.

**Only the transmit high cut is settable** through the station API - the transmit *low* cut is not.
A modem outside either filter is reported at start-up, so the one thing you may still have to fix at
the rig is named rather than left to be discovered on the air:

```
flex: WARNING - modem 0 (ms110d-wn4) occupies 410-3199 Hz, outside the radio's 0..3000 Hz
transmit filter - it will be clipped. Widen the high cut on the radio - this mode's centre is
fixed by its spec.
```

**Headless only.** In attach mode SmartSDR owns the slice and the daemon does not touch the
filter - but it still measures the modems and warns when the filter you are on would clip one.

### Coexisting with SmartSDR

A running SmartSDR grabs **DAX channel 1**, and a headless client on the same channel contends
with it (live finding, 2026-07-17). So an unset `daxChannel` puts a *headless* client on **2** -
which means it does not matter whether SmartSDR was started before or after the modem. Attach
mode keeps 1, because there it is SmartSDR's slice by definition.

Set `daxChannel` explicitly if you have other DAX users to work around.

> **There is no IQ / raw-waveform option here, by design.** The daemon reaches a Flex over
> **DAX audio** only: it hands real audio to the radio's own DIGU SSB modulator, so the signal
> goes through the same TX chain a user's would. The software-IQ route - single-sideband IQ
> through a headless waveform, bypassing the radio's SSB modulator, ALC and TX DSP - exists
> only in the OTA bench harness (`sm-ota ladder --route iq`), and exists precisely *because*
> it bypasses that chain, which makes it a measurement instrument rather than a deployment
> path. See [docs/flex-integration.md](docs/flex-integration.md) § 2.3.

## `ubersdr`

Stream parameters used **only** when `device` is `ubersdr:<instance>` - see
[Listening to a web receiver](#listening-to-a-web-receiver) for what that is and how it behaves.
Ignored for every other device. Where to tune is *not* here: that comes from the band plan, as
it does on a Flex.

```json
"ubersdr": { "mode": "iq48", "gain": 1.0 }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `mode` | string | `"iq48"` | The receiver's IQ mode. `iq48` is 48 kHz complex, ±24 kHz, and is what every public instance offers; `iq96` where one allows it |
| `password` | string | *(none)* | For a protected instance |
| `ssbLowHz` | number | `150` | Lower edge of the SSB filter to synthesise, Hz above the dial |
| `ssbHighHz` | number | `3450` | Upper edge, likewise |
| `startupGuardMs` | int | `1000` | Audio discarded after each connect |
| `gain` | number | `1.0` | Linear gain on the demodulated audio |
| `onDemand` | bool | `false` | Hold a session only while the waterfall page has a viewer |
| `lingerSeconds` | int | `60` | With `onDemand`, how long the session is kept after the last viewer leaves |

**`ssbLowHz`/`ssbHighHz` are the receive filter**, and unlike a rig's they are actually yours to
choose - holding the complex baseband is what makes that possible. The default clears the whole
300-2700 Hz band a plan can place modems in, with room either side, so nothing legal gets
clipped on the way in. Narrowing them emulates a tighter rig for comparison.

**`startupGuardMs` covers a real transient.** Instances ramp their level over the first
0.7-1.0 s of a stream (measured 2026-07-24), which would otherwise put a fade at the head of
every session and a stripe on the waterfall. It costs about a second per reconnect.

**`gain` is for the display, not the decoders.** Everything downstream is floating point and
level-independent; measured off `m9psy-1`, the demodulated audio lands around −26 dBFS RMS,
which is soundcard-like already. Raise it if a quiet instance makes the waterfall hard to read.

**`onDemand` is for a public monitor, not a node.** A station feeding a node needs the receiver
all day; a page for the public has nothing to show while nobody is looking, and the receiver's
slot and this address's daily listening allowance are better left alone until somebody is. With
`onDemand` the daemon still runs the receiver's pre-flight at start-up (so a wrong host or a
refused IQ mode is still an error) and then sits idle: the first browser to open the waterfall
opens the session, every further browser shares it, and the last one leaving starts the
`lingerSeconds` clock, after which the session is closed. The linger is what stops a page
refresh, a tab switch or a flaky connection from costing the receiver a tear-down and rebuild
each time; 0 closes at once. It needs a `waterfall` section, because the page's viewers are
what asks for the receiver.

A receiver that cannot be reached is not fatal in this mode. The page stays up and says what
is wrong in the status chip at the top (unreachable, refused for the day, gave up), and the
daemon keeps trying on the same backoff it uses for a lost session, for as long as anyone is
waiting; with nobody waiting it goes back to idle. The dead-feed watches stand down while idle,
so an empty page does not restart the service every half minute. Every line the receiver's own
machinery writes to the journal carries the viewer count, not only the changes of state: the
phase lines (`ubersdr: live, 2 viewers: M9PSY-1, ...`) and the session's own lines about the
stream ending, reconnecting and backing off (`ubersdr: live, 2 viewers: stream from ... ended
(...)`). A wait before another attempt at the receiver with nobody watching says so in words,
because that is the one worth grepping for: `ubersdr: lingering, 0 viewers: the session ended
after 41 ms with only 0 ms of audio; backing off 300s before reconnecting to ...,
retrying for nobody`. The always-on device writes the same sentences without a count, because
it has no viewers to count.

The daemon writes a few `ubersdr:` lines of its own at start-up which carry no count, because
they are the daemon's rather than the receiver's and none of them is part of a churn: the
receiver description, the session limit, and the "refusing this address for now" line.

The count itself is pages, and a page only counts while it is answering: see [the keep-alive
above](#waterfall). Before it existed a browser that vanished without closing its socket was
counted for ever, so the linger never started and the receiver was retried all night with nobody
watching.

**With a [`monitor`](#monitor) section this section still applies**, to every receiver the monitor
fronts: `mode`, `password`, `ssbLowHz`, `ssbHighHz`, `startupGuardMs` and `gain` are honoured as
they are here. `onDemand` is implied - a monitor is on demand by definition - and the linger comes
from `monitor.lingerSeconds` rather than from here, because it is a property of the site rather
than of one receiver.

---

## `publish`

A station with a real radio can **offer itself to a public monitor site**: one block here, a token
from the site owner, and it appears alongside the web receivers at
https://monitor.ukpacketradio.network with its own page - the waterfall, the AX.25 links panel,
the decoded frames and a Listen button. The station dials out, so nothing has to be opened, held
or forwarded on a home connection. Absent (the default), nothing about this daemon changes at all.

```json
{
  "device": "default",
  "modems": [ { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 } ],
  "waterfall": { "port": 8107 },
  "publish": {
    "url": "wss://monitor.ukpacketradio.network/uplink",
    "token": "pdnsm_...",
    "callsign": "GB7RDG-2",
    "operator": "Tom M0LTE",
    "location": "Reading, England",
    "radio": "IC-7300 into a doublet at 10 m",
    "site": "https://gb7rdg.example/",
    "audioRate": 12000,
    "frames": "always"
  }
}
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `url` | string | *(required)* | The site's uplink endpoint, an absolute `ws` or `wss` URL. Given to you with the token |
| `token` | string | *(required)* | The credential the site issued this station. **Issued once, pasted in once, not edited by hand** - the same rule `api.key` has |
| `callsign` | string | *(required)* | Whose station this is. The site checks it against the token it issued, and refuses the connection if the two disagree |
| `operator` | string | *(none)* | Whose radio it is, for the credit line and the picker row |
| `location` | string | *(none)* | Where it is |
| `radio` | string | *(none)* | What it is listening with |
| `site` | string | *(none)* | Your own page, linked from the picker. An absolute `http` or `https` URL |
| `audioRate` | int | `12000` (or the channel rate, if lower) | The rate the audio is relayed at, in Hz. An integer divisor of the channel's DSP rate, 6000 to 48000 |
| `frames` | string | `"always"` | `"always"` sends decoded frames whether or not anybody is watching; `"watched"` holds them back |

**The site issues the token; nothing here mints one.** There is no sign-up and no discovery: a
station is on the site because its owner asked for a token and was given one. The site holds only
the SHA-256 of it, so a leaked config file on the site's side hands out nothing. This is UberSDR's
own pattern - one opaque string in one config key, generated once, not edited by hand - with the
single difference that the site issues it rather than the station minting it, because a station
behind NAT has no public URL for the site to call back on and a self-minted identifier with no
callback is not a credential at all.

**Nothing flows until somebody is watching.** The socket sits idle. When a visitor opens this
station's page the site says so and audio starts; when the last one leaves it stops, after the
same sixty-second linger the web receivers get, so a page refresh does not stop and restart the
stream. Decoded frames are the exception and go up all the time by default, because they are well
under a kilobit a second and they are what makes a quiet band look alive to somebody arriving an
hour later.

**Only the uplink is on demand, not the radio.** This is a transceiver: its receiver is receiving
and its modems are decoding whether or not anybody anywhere is watching, and the uplink does not
change that by one sample. Nothing a site visitor does reaches this station's radio.

**The audio is 16-bit PCM, uncompressed** - exactly the samples this station's own modems are
reading, sample for sample. There is no codec and there will not be one: what crosses the wire is
modem audio rather than a rendering of it, so it is usable by somebody working on a decoder and
not only by somebody looking at a picture. The site runs the FFT and paints the waterfall; the
decodes stay yours, made by your modems with your diversity settings on your antenna.

**Your own transmissions are part of what a visitor hears**, flagged so the site paints them as
yours. They arrive at the rate they actually leave the radio, and at the display's own -35 dB, so
a keyup does not deafen anybody listening.

**What it costs a home connection.** At the default 12000, **194 kbit/s upstream while somebody is
watching**, and under 1 kbit/s while nobody is - and it is one stream however many people are
watching, because the site fans it out. On FTTC or FTTP that is one to two per cent of the upload
and nobody will notice. **ADSL is different**: a typical ADSL upload is 800 kbit/s to 1 Mbit/s, so
a watched station is using a fifth to a quarter of it for as long as somebody has the page open,
which a video call sharing the line will feel. There being no codec, the two levers are
`"audioRate": 6000` - 98 kbit/s, at the price of a 0 to 3 kHz picture and of any modem above 3
kHz - or not opting in. A 48 kHz station costs 770 kbit/s and cannot sensibly publish from ADSL at
all.

**`audioRate` decides how much of the band the site sees.** The relayed waterfall spans 0 to
`audioRate/2`, so a 48 kHz station that leaves the default gets a 0 to 6 kHz picture and start-up
names any modem whose band falls outside it. The audio is decimated rather than resampled, which
is why the rate has to be an integer divisor of the channel's.

**Nothing can come back down.** The only message the site sends that this station acts on carries
one integer: how many people are watching. There is no transmit, no configuration, no KISS and no
restart, and that is structural rather than a matter of the page hiding buttons - the uplink
client holds no channel, no PTT, no KISS server and no config API, and a test asserts it. See
[`monitor`](#monitor) for the other end of the same rule.

**The uplink never faults this station.** It is a courtesy. A site that is down, a token that is
wrong, a network that has gone: each writes a line and retries, and none of them touches the exit
code, the dead-feed watches or anything a node is doing. A permanently misconfigured `publish`
block is a journal line every quarter of an hour and nothing louder.

**If the site will not have this station, the journal says why, on the first attempt.** A
`callsign` that does not match the token it was issued, or a protocol version the site does not
speak, is answered with the site's own sentence and a closed connection, and that sentence is this
station's first line about it rather than a generic one a quarter of an hour later:

```
publish: monitor.ukpacketradio.network refused this station: says it is GB7RDG-2 and this token was issued to GB7RDG. Retrying in 1 min and saying no more about it for an hour; the station is unaffected
```

It is retried a minute later, doubling to a quarter of an hour, and said once an hour until it is
fixed, which is one field in this block or one line of the site's own config. A site that is
merely down or falling over is a different thing and keeps the shorter ladder and the
quarter-hourly line.

**Leaving is deleting the block**, and it takes effect at the next restart; the socket goes and
the station leaves the picker within seconds. The site keeps a `deny` of its own for the other
direction.

**What a site visitor can see is a public record.** Every frame this station decodes is listed on
its page and written into a log the site keeps, and that log outlives the station going off air.
That is no more than any other monitor site publishes, and it is worth knowing before opting in.

**`publish.token` is redacted by the [config API](#api)**, which reads back `"(set, not shown)"`
for it as it does for `api.key`: a station running the API on its LAN must not hand its uplink
token to anyone holding the API key.

**Validation**, all exit 2 with the reason: `publish` alongside [`monitor`](#monitor), because one
process is not both; `publish` on a `device` that starts `ubersdr:`, because a public web receiver
is already on the site in its own right and relaying it a second time would show one operator's
antenna twice under two names and spend that receiver's daily allowance on the site's behalf
without the site knowing; no [`waterfall`](#waterfall) section, since the uplink publishes what the
waterfall server computes and without one there is nothing to publish; a `url` that is not an
absolute `ws` or `wss` URL; a `token` shorter than 32 characters, or missing, there being no
default; a `callsign` that is not a callsign with an optional SSID; a `site` that is not an
absolute http or https URL; a `callsign` over 16, `operator` over 40, `location` over 60 or
`radio` over 60 characters, said here rather than cut in half on somebody else's website; a
`frames` that is not `"always"` or `"watched"`; and an `audioRate` outside 6000 to 48000 or that
does not divide the channel's DSP rate, which is answered with the list of rates that do.

---

## `monitor`

Everything so far describes **one station**: one radio or one web receiver, one KISS port, one
page. A `monitor` section turns the same daemon into something else - **one site fronting many
UberSDR web receivers**, with a front page that lists them and a visitor picking one. Same binary,
same package, same tests; the configuration is the switch.

```json
{
  "monitor": {
    "publicUrl": "https://monitor.ukpacketradio.network",
    "directory": "https://instances.ubersdr.org/api/instances",
    "refreshMinutes": 5,
    "lingerSeconds": 60,
    "allow": [],
    "deny": [],
    "modems": [
      { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 },
      { "subChannel": 2, "mode": "bpsk300",       "rfFrequency": 7051600 }
    ]
  },
  "frameLog": { "path": "/var/lib/pdn-soundmodem" },
  "waterfall": {
    "port": 8099,
    "title": "UK packet monitor",
    "about": "The 7050-7052 kHz packet window on 40 m, as heard by public web receivers. Pick a receiver to watch. Receive only: this site decodes what it hears and shows the AX.25 links and frames; nothing is transmitted."
  },
  "bind": "127.0.0.1"
}
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `publicUrl` | string | *(worked out per request)* | This site's own address as the world reaches it, e.g. `"https://monitor.ukpacketradio.network"`. Scheme, host and optional port only; a trailing slash is accepted and ignored - [below](#monitoruplinks) |
| `directory` | string | `"https://instances.ubersdr.org/api/instances"` | Where the list of receivers comes from. An absolute http or https URL |
| `refreshMinutes` | int | `5` | How often it is fetched again. 0 fetches once, at start-up, and never again |
| `lingerSeconds` | int | `60` | How long each receiver's session is held after the last viewer of *that receiver* leaves |
| `allow` | array | *(everything)* | When non-empty, the only hosts listed. Matched on the directory's `host`, case insensitively |
| `deny` | array | *(nothing)* | Hosts never listed, whatever else says otherwise. **`deny` beats `allow`** |
| `modems` | array | *(required)* | The modems every receiver is given, same schema as the top-level [`modems`](#modems) |
| `uplinks` | array | *(none)* | Private stations this site accepts an uplink from - [below](#monitoruplinks) |

**`monitor` and [`device`](#device) are mutually exclusive.** They say incompatible things about
what the process is: `device` is one radio or one receiver with a KISS port and a transmitter,
`monitor` is many web receivers behind one page with neither. Both set is an exit 2 naming both.

**What is served, and what is not.** One port, `waterfall.port`, and on it:

| Path | What |
|---|---|
| `/` and `/index.html` | The picker: one row per receiver, sorted so that the ones people are already watching are at the top |
| `/api/instances` | What the picker polls, every 10 s: the list, each receiver's state and viewer count, and how old the list is |
| `/r/<slug>/` | That receiver's page - the waterfall, the AX.25 links pane, the decoded frames, the browser audio |
| `/r/<slug>/ws` and `/r/<slug>/links` | That page's own socket and its torn-off links window |
| `/uplink` | Where a private station's own daemon connects, with the token this site issued it - [below](#monitoruplinks). Absent, and a 404, on a site with no `uplinks` configured |
| `/robots.txt` | Asks crawlers to leave `/r/` and `/api/` alone; see below |
| anything else | 404 |

There is **no KISS, no PTT, no transmitter, no [`api`](#api), no [`survey`](#survey), no
[`paging`](#paging) and no ARDOP host** in this flavour, and none of them is reachable on that
port. A monitor is a display. `waterfall.public` is forced true, because a picker that lists other
people's receivers and invites anyone to watch one is a page for strangers by definition.

**Everything the directory says is treated as somebody else's writing.** A `host` that is not a
hostname is ignored, with one journal line, rather than carried into a station that then cannot
open; and a `public_url` that is not an absolute `http` or `https` URL is dropped in favour of the
receiver's own endpoint, because that string goes into a link on the picker and on the receiver's
page, and a `javascript:` URL there would run in every visitor's session on this site.

**The slug is derived from the receiver's host**, which is the only field the directory guarantees
unique: lower-case it, strip a trailing `.tunnel.ubersdr.org` or `.instance.ubersdr.org`, replace
every run of anything outside `a-z`, `0-9` and `-` with one hyphen, trim the ends. So
`m9psy-1.instance.ubersdr.org` is `/r/m9psy-1/` and `reading-ubersdr.m0lte.uk` is
`/r/reading-ubersdr-m0lte-uk/`. It is ugly for a receiver on its own domain; the picker shows the
callsign and the location, so the slug is only ever seen in the address bar - and it does not
change when an unrelated receiver appears, which is what makes a bookmark keep working.

**Which receivers are listed.** In order: `deny`, then `allow` when it is non-empty, then whether
the directory says the receiver is online, then whether it offers the IQ mode
[`ubersdr.mode`](#ubersdr) asks for (a receiver that lists no IQ modes at all is not offering
ours), then whether it has an antenna connected, then - only where
the receiver actually *reported* a tuning range - whether that range covers the RF window the
configured modems occupy. A receiver that fails any of those is not listed at all. A receiver with
**no free listener slot is listed and shown as full**, because that is one a visitor may well come
back to; a picker that silently dropped it would read as this site being broken.

**`deny` is how an operator's wishes are honoured.** If the operator of a receiver would rather
not be listed here, their host goes in `deny` and they are gone from the picker within
`refreshMinutes`. That is the answer to give when asked, and it should be given before anyone has
to ask. `deny` beats `allow`, so it cannot be defeated by editing the other list.

**`allow` is also how you run a smoke test**: two hosts in `allow` and the site fronts two
receivers rather than fifty.

**Stations are built lazily and kept.** Nothing exists for a receiver nobody has picked, and
picking one costs the *receiver* nothing: the first request for `/r/<slug>/` builds that
receiver's channel, modems, frame log and page, and the receiver itself is not contacted until a
browser actually attaches. So a crawler cannot cost anybody a session. Once built, a station is
kept for the life of the process, so the links pane and the frame log survive a visitor leaving
and coming back - which is the whole reason a quiet band looks alive.

**An `ardop` entry is accepted, draws its band and decodes nothing**, because ARDOP is not a
demodulator but a whole virtual TNC with its own host interface and a monitor has none - so leave
it out, as the example above does. (The gap at sub-channel 1 is deliberate: these are GB7RDG-2's
standard slots and slot 1 is its ARDOP one, so 0 and 2 here are 0 and 2 on the node. Leaving the
entry out does not move anything: with it and without it the plan for the two modems above is dial
7049450 with audio centres 850 and 2150.)

**One session per receiver, however many people are watching it.** The fan-out is in this daemon:
ten visitors on one receiver are ten browsers on one page, which is one viewer count, which is one
session. Each receiver's session is dropped `lingerSeconds` after the last browser watching *that
receiver* leaves. This is the promise the design makes to the people whose antennas these are.

**The per-address daily allowance is per receiver, and this site has one egress address.** Fifty
receivers means fifty independent allowances, each of which this site can exhaust on its own. When
one is spent the picker says so in that receiver's own row, in words - "this receiver's daily
allowance for this monitor is used up, back tomorrow" - and the receiver's page says so in its
status chip. Nobody should have to guess why a receiver went quiet.

**[`frameLog`](#framelog)`.path` is a directory here, not a file.** A monitor keeps one log per
receiver, `frames-<slug>.db` inside it. The directory is created if it is missing; a path that is
a file, or that names one (anything ending `.db`), is an exit 2 with the reason rather than a
SQLite error from inside whichever receiver a visitor happened to pick first. Omit the section and
nothing is written down.

**When the directory is down**, the last good list is kept and the picker says how old it is: "the
receiver directory is unreachable, this list is from 19:42". Nothing watching a receiver is
affected - a station outlives whatever the directory last said about its receiver, and a receiver
that leaves the directory keeps its page for as long as anybody is on it, and simply stops being
offered. A cold start that has never reached the directory shows an empty picker and says why. One
journal line per outage, not one per refresh.

**A receiver whose stream breaks** - a dead feed, or a stream that stops delivering while claiming
to be live - takes down that receiver and no other. Its page and its row say what happened, and it
is built again a minute later if somebody is still watching. That is what a single station answers
with a restart, applied to one receiver instead of to the process.

**A crawler can build every station without touching a single receiver.** Following each row's
link is what builds that receiver's station, and stations are kept, so anything that walks the
picker's links takes the process to its maximum memory in one pass. It costs the receivers
nothing - none of them is contacted - but it costs this container everything it was going to cost
eventually, all at once. Size for every listed receiver, not for the ones you expect people to
watch.

`/robots.txt` asks crawlers to leave `/r/` and `/api/` alone and leaves the picker itself
indexable, which is the page somebody searching for this would want to find. **It is a courtesy,
not a control**: a crawler that ignores it will do exactly what the paragraph above describes, and
what actually bounds the damage is the rate limit in front of the site and having sized the
container for every listed receiver. The picker's own poll of `/api/instances` is unaffected -
`robots.txt` governs crawlers, not the page's own fetches.

**Memory is the sizing question**, and it is the modems rather than the plumbing (measured
2026-09-03, x86-64, .NET 10): about **31 MB per station** for the three-modem 40 m band plan above,
against 86 MB for the process with no receiver picked. Almost all of that is the
frequency-diversity banks - `afsk300-il2pc` runs 11 decoder branches by default and `bpsk300` runs
9 - so `"offsetPairs": 0` on both brings it to about **3.5 MB per station** at the cost of the
off-frequency coverage those banks buy. Fifty receivers all picked is therefore about 1.6 GB as
configured above, or 260 MB with the banks off. Nothing is freed by a visitor leaving, because the
station is kept.

**Validation**, all exit 2 with the reason: `monitor` alongside `device`; an empty `monitor.modems`;
a modem in it that will not build (they are built once at start-up, against a throwaway channel,
so that a mode this configuration cannot make is one message here rather than a 404 on every
request for every receiver); no `waterfall` section, or one with no `port` written down, since a
site meant to be reached from outside should not come up on a number nobody chose; a negative `refreshMinutes` or `lingerSeconds`; a `directory` that is not
an absolute http or https URL; a `publicUrl` that is not an absolute http or https URL, or that
carries a path, a query or a fragment, this site being served from the root of its port, or that
carries credentials, which is the one refusal here that does not quote the value back; and an
`allow` or `deny` entry that is not a hostname - that last
one because an entry with a scheme or a port in it would silently match nothing and leave an
operator believing they had been taken off a list they are still on. The `uplinks` entries have
their own conditions, [below](#monitoruplinks).

### `monitor.uplinks`

The other end of [`publish`](#publish): the private stations this site will accept a connection
from, and how each of them appears. Empty (the default) accepts none, and `/uplink` is a 404 like
any other path this site does not serve. A station in this list is somebody's actual transceiver -
their radio, their antenna and their electricity - and it appears on the picker in the same list
as the web receivers, tagged `station`.

```json
{
  "monitor": {
    "directory": "https://instances.ubersdr.org/api/instances",
    "modems": [ { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 } ],
    "uplinks": [
      {
        "callsign": "GB7RDG-2",
        "slug": "gb7rdg-2",
        "tokenSha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
      }
    ]
  },
  "frameLog": { "path": "/var/lib/pdn-soundmodem" },
  "waterfall": { "port": 8099, "title": "UK packet monitor" },
  "bind": "127.0.0.1"
}
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `callsign` | string | *(required)* | The callsign this station must say it is. A connection whose `publish.callsign` does not match is closed with the reason |
| `slug` | string | *(required)* | The path its page is served under, `/r/<slug>/`. Lower-case letters, digits and hyphens; normally the callsign lower-cased |
| `tokenSha256` | string | *(required)* | The SHA-256 of the token issued to that station, as 64 hex characters. `pdn-soundmodem --uplink-token GB7RDG-2` prints a token and this hash together |

**Issuing a token.** Run `pdn-soundmodem --uplink-token GB7RDG-2` on the monitor, naming the
station the token is for. It prints a fresh token and the whole entry to paste into this list,
and writes neither anywhere: keep the hash here, give the token to the station's operator once,
and they paste it into their own `publish.token`. The callsign is an argument rather than an
example in the printed entry, so that what comes out is that station's entry and not one with
somebody else's callsign in it waiting to be edited. The token is 256 bits from the system's
cryptographic random source, so it is worth possessing and worth not inventing by hand.

**The hash, and not the token.** This site only ever compares, so it never needs the plaintext,
and a config file that leaks does not hand out working uplinks. Plain SHA-256 with no salt and no
work factor is deliberate rather than an oversight: at 256 random bits there is no dictionary to
defend against, and a work factor would only cost this process time on every connection. The
comparison is constant-time, over the raw bytes, as the [config API](#api)'s key check is.

**The callsign is bound to the token**, which is what stops one station claiming another's page,
and **the slug never comes off the wire at all**, so a station has no way to ask for one. Both are
decisions somebody took and wrote down here, which is why the slug is written out rather than
derived: the URL a visitor bookmarks should not be a function that might change.

**A station's slug beats a receiver's.** Each configured slug is reserved at start-up, so a web
receiver whose hostname would give the same one is served under its full sanitised host instead,
with a line saying so. A station wins because its slug is a callsign somebody was issued and a
receiver's is derived from a hostname.

**A station is told the address of its own page here**, in the `welcome` it gets when it
connects, so its own journal reads `publish: live at
https://monitor.ukpacketradio.network/r/gb7rdg-2/` rather than `publish: live as gb7rdg-2`. That
address is [`monitor.publicUrl`](#monitor) where it is set, and otherwise the `Host` header of the
station's own connection. **Set it on any site behind a tunnel**: `cloudflared` and its like
rewrite `Host` on the way in - CT 146's ingress leaves `127.0.0.1:8099` - and a loopback name or a
bare address is not one worth repeating back, so a site that has not written its address down
tells the station nothing and the station names its slug instead. A guessed URL in somebody else's
journal is worse than none. Written with or without a trailing slash it means the same thing, and
`/r/<slug>/` is appended to it.

**One connection per token.** A second connection authenticating the same token closes the first,
with a close reason saying so, because a station whose socket has half-closed must not be locked
out by its own ghost.

**A station is listed while its socket is up.** Its page, its frame log and its links panel are
built on its first connection and kept for the life of the process, exactly as a receiver's
station is, so a station that goes off air keeps its history and its page and simply stops being
offered on the picker - `"offered": false`, `"why": "not connected just now"`.

**Removing a station needs a restart.** Take its entry out and restart; there is no live reload.
That is accepted rather than overlooked: this is a list of people the site owner invited, and it
changes about as often as the container does.

**Everything a station sends is treated as somebody else's writing**, exactly as the receiver
directory's strings are. Lengths are capped at the boundary - callsign 16, operator 40, location
60, radio 60, mode 24 - and over a cap is a refused connection or a dropped message rather than a
truncation, so nothing arrives half-said. A `site` URL goes through the same absolute-http-or-https
check the directory's `public_url` does. Anything reaching the journal is flattened to ASCII. A
message over its size cap, an audio block that is not the length the station's own hello declared,
or a sustained rate over twice what it declared: the connection is closed, one line is journalled
naming the station, and that token is not accepted again for a minute. A token this site has not
issued is held for a second and counted, at most one journal line a minute.

**What a station cannot do.** The only message this site sends down an uplink is how many people
are watching. There is no path by which a connection can transmit, retune, reconfigure or restart
anything, and none by which a station's own bytes reach a browser unexamined: every message is
parsed into typed fields here and this site re-serialises its own. What a station *can* do is say
it heard a callsign it did not, which is the ordinary exposure any publisher has - the token was
issued to a person, the credit line names them, and taking their entry out removes them.

**`/api/instances` says which is which.** Every row gains `kind`, `receiver` or `station`. A
station's row carries `callsign`, `operator`, `location`, `radio`, `modes`, `publicUrl`, `offered`,
`why`, `state`, `status` and `viewers`; `host`, `snrDb`, `loadStatus`, `availableClients` and
`maxClients` are null, because they are facts about a web receiver and inventing them for
somebody's transceiver would be inventing them.

**Validation**, all exit 2 with the reason: a `callsign` that is not a callsign with an optional
SSID; a `slug` that is not a usable path segment, which is answered with the one the callsign
would have given; a `tokenSha256` that is not 64 hex characters, which is answered with
`--uplink-token CALLSIGN`; two entries with the same `slug`, because one page cannot be two stations; the
same `callsign` twice; and the same `tokenSha256` twice, because a token names one station and the
same one on two entries cannot say which of them is connecting.

## `deadFeed`

An input device can die without saying so, and a modem that keeps "running" on a dead feed is
worse than one that stops: no decodes, no captures, and nothing anywhere says a word. The real
incident behind this: a Flex whose VITA stream died kept delivering full-rate buffers of exact
zeros, and the daemon spent 6.8 hours recording them. Two watches cover the two ways a feed
dies, and either one firing takes the proven recovery - an orderly shutdown with exit 1, so
systemd restarts the service and rebuilds the device session from scratch.

```json
"deadFeed": { "silenceSeconds": 30, "starvationSeconds": 30 }
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `silenceSeconds` | number | per device | Unbroken **digital silence** (every sample exactly zero) that declares the feed dead. 0 = off |
| `starvationSeconds` | number | per device | Wall-clock seconds with **no samples delivered at all** that declare the feed starved. 0 = off |

Omit the section (the normal case) and each device gets the defaults its failure modes ask for:

| Device | `silenceSeconds` | `starvationSeconds` | Why |
|---|---|---|---|
| `flex:` | 30 | 30 | A dead VITA stream pads exact zeros at full rate (silence); a broken DAX UDP path delivers nothing while the session looks alive (starvation) |
| `ubersdr:` | 30 | 30 | An instance whose SDR feed dies streams zero IQ (silence); a hung WebSocket delivers nothing (starvation) |
| ALSA | off | 30 | A stalled or unplugged card stops returning samples (starvation). Silence is off **deliberately**: genuinely-silent wired inputs exist, and a disconnected cable must not restart-loop the service |
| `--wav-loop`, `flex:mock` | off | off | Bench inputs with no radio behind them: a recording paces itself and cannot starve (and looping a silent one is legitimate), and the mock's DAX-RX path deliberately delivers nothing between injected frames |

The silence watch is safe where it is on by default because a real receive path always carries
noise-floor energy (the healthy Flex capture measures RMS ~0.02 against the dead feed's exact
0.0) - half a minute of unbroken zeros is a dead feed with certainty, not a quiet band. The one
false-positive family is a **deliberately muted** stream: a muted DAX channel restart-loops the
service every `silenceSeconds`, loudly, and the journal line says where to look. Set
`"silenceSeconds": 0` if muting the feed is something you do on purpose.

Turning `silenceSeconds` on for an ALSA card is legitimate where the input always carries
audible noise floor (a receiver wired straight in) - that buys the same protection the Flex
gets, at the price above if the cable comes out.

One ALSA consequence worth knowing: an `snd-aloop` capture side only clocks while some
application holds its playback side, so a virtual-card station started before its peer sits in
a blocked read, gets declared starved after `starvationSeconds`, and restart-loops (loudly)
until the peer appears - at which point it comes up by itself, which is the point of the
watch. If waiting indefinitely for the peer is your normal case, set `"starvationSeconds": 0`.

The watches do not double-report deaths another path already owns: a Flex whose TCP session
drops is reported by the session handler, and an UberSDR receiver that stays unreachable is
reported once by its own five-minute give-up clock - reconnect backoff and quota refusals are
deliberate quiet, not starvation.

In the journal, each detector names its family and device, e.g.:

```
receive feed dead: 30 s of unbroken digital silence from the radio - restarting to rebuild the session (recurring? check DAX/slice config - a deliberately muted DAX stream restart-loops this way)
receive feed starved: the sound device returned no samples for 30 s - a stalled or unplugged card - restarting to reopen it
```

---

## Watching a station work

`journalctl -u pdn-soundmodem -f` is the view most operators actually use - the waterfall needs a
browser and the frame log needs SQL. Every frame the station hears or sends is one line:

```
rx[0] afsk300-il2pc M0LTE>GB7IOW-1 15 bytes  crc ok  fec 0  -5 Hz
tx[0] afsk1200 M0LTE>GB7RDG-2 28 bytes
tx[0] DROPPED M0LTE>GB7RDG-2 28 bytes: this station receives only
```

| Field | Means |
|---|---|
| `rx[N]` / `tx[N]` | Direction and KISS sub-channel |
| mode | The mode the frame was heard on - the bare catalogue name, the same string however many branches the receiving bank was built with. The bank's own construction (`afsk300-il2pc-multi11`) is how the modem describes itself, shown where the receiver is the subject - the waterfall's modem chips and the id-beacons line above - not on every frame |
| `SOURCE>DEST` | AX.25 addresses; `(no ax25 header)` where the payload is not AX.25, rather than a mangled callsign |
| `crc ok` / `CRC BAD` | Only for modes that carry a CRC; a mode with none claims neither |
| `fec N` | Bytes the FEC corrected. Rising counts mean the link is being carried by the FEC and is closer to the edge than a clean decode suggests |
| `±N Hz` | Measured carrier offset - what to retune by |
| `emph ±N dB` | Diversity banks only, and only when non-zero: the far station's TX audio is twisted |

Host sessions are logged too, because a host that quietly drops its TCP connection stops passing
traffic and - from the modem's side - looks exactly like a band that went quiet:

```
kiss[8105] 192.168.1.50:54312 connected - 2 clients (all modems)
kiss[8101] 127.0.0.1:40000 disconnected - 0 clients (modem 3 only)
kiss[8105] 192.168.1.50:54312 disconnected: Connection reset by peer. - 1 client (all modems)
```

Each line says which port, which host, how many sessions remain, and **which modems that port
reaches** - the thing host software gets wrong. A clean close carries no reason; a host that
vanished carries the transport's, because "the host closed" and "the host died" are different
problems.

When the sound device loses audio, that is reported too:

```
audio: 3 capture overruns, 1 playback underrun (4 since start) - each one is lost audio: a dropped
frame on receive, a discontinuity in what we transmitted. This is the machine not scheduling the
modem within the ~120 ms of buffer it has, not a radio problem; give it more CPU share, or fewer
neighbours.
```

ALSA recovers from an overrun or underrun by restarting the stream, silently - so without this a
station being starved of CPU is indistinguishable from a station on a quiet band. Reported as
what was lost **since the last look**, every ten seconds at most, with the advice given once.

Worth knowing if you run under LXC or Docker: a `Nice=` value inside a container does **not** rank
you against other containers - that is the container's CPU weight, set from the host. If these
lines appear on a shared box, that is the knob, or dedicated cores.

**A transmission is logged when it goes out, not when it is queued.** A frame can wait behind CSMA
or an ARQ session for seconds. Frames that never made it appear once as `DROPPED` with the reason,
and never as `tx`.

## What is rejected at start-up

The daemon validates before it opens anything, and refuses to start rather than run in a state
you did not ask for. Every configuration problem is reported the same way - the file, what is
wrong in plain words, and what to do - and exits with status **2**:

```
configuration error in /etc/pdn-soundmodem/soundmodem.json
  not valid JSON - line 7, position 3: ',' is an invalid start of a value.

  The service will not start until this is fixed. As root, to start
  from a known-good file:
    cp /usr/share/pdn-soundmodem/soundmodem.example.json /etc/pdn-soundmodem/soundmodem.json
  Then edit it for your sound device and PTT, and:
    systemctl restart pdn-soundmodem
  Every setting is documented at https://github.com/packet-net/pdn-soundmodem/blob/main/CONFIG.md
```

Read it with:

```sh
systemctl status pdn-soundmodem          # the last few lines, usually enough
journalctl -u pdn-soundmodem -n 30       # the whole message
```

**A rejected config does not crash-loop.** The unit sets `RestartPreventExitStatus=2`, so
systemd leaves the service stopped after a configuration error and the journal holds one
readable explanation rather than a copy every five seconds. Fix the file and
`systemctl restart pdn-soundmodem`. Any *other* failure - a USB sound card that has not
enumerated yet at boot, for instance - still restarts on its own as usual.

| Condition | What you get |
|---|---|
| File missing, unreadable, or in a missing directory | `no such file` / `no such directory` / `permission denied reading the file` |
| File empty | `the file is empty` |
| File contains the literal `null` | `the file contains only \`null\`` - with a minimal working config to copy |
| Malformed JSON | `not valid JSON - line L, position P: …` (counted from 1, as your editor does) |
| Two modems on the same `subChannel` | `two modems share "subChannel": N … renumber one of them` |
| `ardop` alongside `modems` or `paging` | `"ardop" cannot be combined with … keep "ardop" and delete the others, or delete "ardop"` |
| `mode` not a known mode | `unknown mode 'X'` - with a **did you mean** for near misses, and a link to the mode table |
| `frequency` on a fixed-centre mode | `mode 'X' has a fixed centre frequency - drop the frequency override …` |
| `acceptPlainIl2p` on a mode that does not run IL2P+CRC | `mode 'X' does not run IL2P+CRC, so it has no separate plain-IL2P reading to release - drop "acceptPlainIl2p"` - with the modes it does apply to |
| `captureRate` not a multiple of the DSP rate | `--capture-rate must be a multiple of N` |
| `flex.transmitFilterHighHz` outside 500-10000 (and not `0`) | `That is an audio cut-off in Hz … use 500-10000, 0 to leave the radio's own filter alone …` |
| `ptt` alongside a `flex:` device | `--device flex: keys the radio itself; remove the conflicting --ptt …` |
| `ptt` alongside a `ubersdr:` device | `--device ubersdr: is a receive-only station … Remove "ptt".` |
| `alsa.mixer` percentage outside 0-100 | `"alsa"."mixer"."captureGainPercent" is 150 … use 0-100, or remove it to leave the control exactly as the card has it.` |
| `alsa.mixer` alongside a `flex:`/`ubersdr:` device | `… which is not a sound card … Remove the "alsa" section, or point "device" at the card.` |
| `alsa.mixer` alongside `monitor` | `A monitor fronts web receivers and has no sound card of its own …` |
| `ubersdr:` with no `rfFrequency` and no `dialFrequency` | `the UberSDR instance … has to be told where to listen` |
| `ptt.type` not `serial` or `cm108` | `unknown ptt type 'X'` |
| [`monitor`](#monitor) alongside `device` | `this file sets both "device" (…) and "monitor" … Remove whichever one you did not mean.` |
| `monitor.modems` empty | `"monitor"."modems" is empty … a monitor with none would connect to receivers and decode nothing` - with an entry to copy |
| `monitor` with no `waterfall` | `"monitor" needs a "waterfall" section: the picker and every receiver's page are served on its "port"` |
| `monitor` with a `waterfall` that has no `port` | `"waterfall" has no "port" … not a decision anybody made` |
| `monitor.modems` naming a modem that will not build | the same refusal a station gets, e.g. `mode 'X' does not run IL2P+CRC, so it has no separate plain-IL2P reading to release` |
| `monitor.refreshMinutes` or `monitor.lingerSeconds` negative | `That is a number of minutes/seconds to wait, so it cannot be negative` |
| `monitor.directory` not an absolute http or https URL | `which is not an absolute http or https URL` - with the public one to copy |
| `monitor.allow` or `monitor.deny` entry that is not a hostname | `which is not a hostname … no scheme, no port, no path` |
| `monitor.publicUrl` with a path, a query or a fragment, or not http/https | `a scheme, a host and an optional port and nothing after them` - the site is served from the root of its port |
| `monitor.publicUrl` with credentials in it | `carries credentials, and this message does not repeat it back` - nothing signs in to a public page, and the journal is no place for a password |
| `frameLog.path` a file, or naming one, under `monitor` | `A monitor keeps one log per receiver, so this is a DIRECTORY here …` |
| [`publish`](#publish) alongside `monitor` | `this file sets both "publish" and "monitor" … one process is not both` |
| `publish` on a `ubersdr:` device | `A receiver like that is already on the monitor site in its own right …` |
| `publish` with no `waterfall` | `"publish" needs a "waterfall" section: the uplink publishes what the waterfall server already computes` |
| `publish.url` not an absolute ws or wss URL | `which is not an absolute ws or wss URL` - with the endpoint's shape to copy |
| `publish.token` missing or under 32 characters | `it is the credential the site issued this station … do not edit it by hand` |
| `publish.callsign` not a callsign | `A station on a public page has to say whose it is` |
| `publish.site` not an absolute http or https URL | `It is linked from a public page in your name` |
| `publish` callsign/operator/location/radio over its limit | `Said here rather than cut in half on somebody else's website` |
| `publish.audioRate` outside 6000-48000, or not dividing the channel rate | `the audio is decimated rather than resampled, so it has to be an integer divisor` - with the rates that are |
| [`monitor.uplinks`](#monitoruplinks) entry with no callsign, an unusable slug or a bad hash | `has no business being there` / `cannot be a path segment` / `not 64 hex characters` - each with what to write instead |
| Two `monitor.uplinks` entries sharing a slug, a callsign or a token hash | `One page cannot be two stations` / `lists it twice` / `a token names one station` |

The mode suggestion is worth knowing about, because a hyphen is easy to lose among 38 names:

```
modem 0: unknown mode 'fsk9600il2p'
  did you mean: fsk9600-il2p, fsk4800-il2p
  the 38 valid mode names are listed at …/docs/modes.md
```

### Hardware the config names but the machine does not have

Different case, deliberately handled differently. A config that is *structurally* fine but
points at a sound card or PTT line that will not open - the usual first-install experience,
since the seeded config names a CM108 on `/dev/hidraw0` - exits **1**, not 2, so the service
**keeps restarting**. That is on purpose: a USB interface may simply not have enumerated yet
at boot, and the service should come up by itself when it does rather than stay down waiting
for a human.

You still get a message rather than a stack trace:

```
cannot open the cm108 PTT device "/dev/hidraw0"
  Could not find file '/dev/hidraw0'.

  Set by "ptt" in /etc/pdn-soundmodem/soundmodem.json
  This selects how the radio is keyed; omit it entirely for VOX, or for a
  FlexRadio, which keys itself.
  List what this machine actually has:
    ls -l /dev/hidraw*
  /dev/hidraw* is root-only by default, so the unprivileged service user cannot
  open it without a udev rule granting the audio group access - see the
  Permissions section of INSTALL.md. …
```

So: **"will not start until this is fixed" means the service is stopped and waiting for you;
"will keep retrying" means it is still trying and may fix itself.**

## Config file vs command line

Everything here has a command-line equivalent, and the daemon can run entirely without a
config file:

```sh
pdn-soundmodem --device plughw:1,0 --modem 0:afsk1200 --kiss 8105 --ptt serial:/dev/ttyUSB0:rts
```

**When `--config` is given, the file wins for most settings** - it overwrites `device`,
`captureRate`, `kissPort`, `bind`, `ptt`, `paging`, `flex` and `waterfall`, so a
`--device` passed alongside `--config` is silently discarded (`--txdelay` still applies -
it has no config equivalent). The exceptions:

| Flag | Behaviour with `--config` |
|---|---|
| `--modem N:MODE[:FREQ]` | **Appends** to the file's `modems` list rather than replacing it |
| `--waterfall PORT`, `--dial HZ` | Override the file's `waterfall` section (and enable it if absent) |
| `--flex-freq`, `--flex-ant`, `--flex-mode`, `--flex-daxch` | Override the file's `flex` section |
| `--paging PORT[:BAUD]` | Replaces the file's `paging` section |
| `--ardop PORT` | Used only if the file has no `ardop` section |

Some options are command-line only and have no config equivalent - `--wav FILE` and
`--wav-loop FILE` (decode a recording instead of live audio), `--quality-frames`, and
`--psk-detector coherent|differential`.

## Worked examples

**VHF packet, one AFSK modem, serial PTT** - the common node case:

```json
{
  "device": "plughw:CARD=Device,DEV=0",
  "captureRate": 48000,
  "kissPort": 8105,
  "modems": [ { "subChannel": 0, "mode": "afsk1200-multi" } ],
  "ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" }
}
```

**HF, two modems on one channel, CM108 PTT, waterfall on the LAN:**

```json
{
  "device": "plughw:1,0",
  "modems": [
    { "subChannel": 0, "mode": "bpsk300", "frequency": 1500 },
    { "subChannel": 1, "mode": "qpsk2400", "frequency": 1500 }
  ],
  "ptt": { "type": "cm108", "device": "/dev/hidraw0", "gpio": 3 },
  "waterfall": { "port": 8107, "bind": "*", "dialFrequencyHz": 14105000, "sideband": "usb" }
}
```

**9600 baud** - note `captureRate` must be a multiple of 48000 here:

```json
{
  "device": "plughw:1,0",
  "captureRate": 48000,
  "modems": [ { "subChannel": 0, "mode": "fsk9600" } ],
  "ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" }
}
```

**Winlink over HF via ARDOP, sharing 40m with two packet modes** - one radio, one passband:

```json
{
  "device": "plughw:1,0",
  "captureRate": 12000,
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc", "frequency": 300,  "port": 8100 },
    { "subChannel": 1, "mode": "ardop",         "frequency": 950,  "port": 8101 },
    { "subChannel": 2, "mode": "bpsk300",       "frequency": 1600, "port": 8103 }
  ],
  "ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" }
}
```

Point Pat at 8101 (data 8102), and your packet host at 8100 and 8103.

**A monitoring station on somebody else's antenna** - no radio, no sound card, no PTT; it hears
40 m from Scotland and writes down everything it decodes:

```json
{
  "device": "ubersdr:m9psy-1.instance.ubersdr.org",
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300 },
    { "subChannel": 1, "mode": "bpsk300",       "rfFrequency": 7051600 }
  ],
  "waterfall": { "port": 8107 },
  "frameLog": { "path": "/var/lib/pdn-soundmodem/frames.db" }
}
```

**A FlexRadio over the LAN, no sound card at all** - no `ptt`, the radio keys itself:

```json
{
  "device": "flex:10.45.0.76",
  "flex": { "frequency": "14.105000", "antenna": "ANT1", "mode": "DIGU", "daxChannel": "1" },
  "modems": [ { "subChannel": 0, "mode": "bpsk300" } ]
}
```

## See also

- [INSTALL.md](INSTALL.md) - installing the package and first-run setup
- [docs/modes.md](docs/modes.md) - every mode, its capabilities and verification level
- [docs/flex-integration.md](docs/flex-integration.md) - FlexRadio headless and attach modes
- [docs/ardop-design.md](docs/ardop-design.md) - the ARDOP implementation
