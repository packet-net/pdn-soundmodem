# ARDOP on-air acceptance bench (GB7RDG, 40 m slot 2)

The procedure for the one rung of the ARDOP acceptance ladder that has never been climbed: a real ARDOP transmission from a real transmitter on a shared band segment. Written 2026-09-12, before the session, because [docs/roadmap.md](../roadmap.md) #6 and its "Needs Tom + a radio" item 3 both say to write it first.

Everything below is grounded in this repository, or in a read-only probe of the live station taken on 2026-09-12 and quoted as such. Where a number or a behaviour is not grounded it says **unverified**, and is not to be quoted as measured.

This is an operator's document. It assumes a human at the radio for every transmission on it, and section 4 explains why that is not a stylistic preference.

## 1. What is being validated, and the exit criterion

**Validated:** our ARDOP transmit chain and our ARQ engine, over a real HF path, against a peer we do not control and did not configure.

**Exit criterion**, stated identically in two places and not to be widened here: **one real ARQ connection with a deployed peer, logged** ([docs/ardop/plan.md](../archive/ardop/plan.md), "Open legs" leg 2; [docs/roadmap.md](../roadmap.md) #6). A ping answered, an ID heard by somebody, or an FEC frame decoded elsewhere are all useful and none of them is the exit. The exit is `CONNECTED <call> <bw>` followed by data moving and an orderly `DISCONNECTED`, with a transcript.

A Winlink gateway session is explicitly optional gravy (roadmap #6). Pat already works through the host interface and has the Rung 4 evidence to prove it; a gateway adds no information this rung is short of.

## 2. What is already proven, and where

The point of this section is so that a failure on air can be attributed. If a rung below fails, the question is always "which of these did the air just disprove", and that is only answerable if the prior evidence is written down with its gate and its date.

| Claim | Evidence | Gate | Date |
|---|---|---|---|
| Frame codec, component vectors, WAV cross-decode both directions, FEC exchange (ladder Rungs 0-2) | checked-in fixtures + tests in the main suite | **in CI**, every push | continuous |
| Host protocol byte-identical to ardopcf | 107-command transcript diff against a live ardopcf 1.0.4.1.3, identical bar VERSION | see below | 2026-07-17 |
| Full ARQ sessions ours to ardopcf, both roles, over `snd-aloop` (Rung 3) | `tests/Packet.SoundModem.Tests/Ardop/ArdopHostLiveTests.cs` | **skipped unless `ARDOPCF` and `ARDOP_ALOOP_CARD` are set** (`:442`, `:513`, `:569`, `:803`) | 2026-07-17 |
| Real Pat, real B2F message, end to end through our modem and ardopcf (Rung 4) | `ArdopHostLiveTests.cs:619` `Pat_Exchanges_A_Message_Through_Our_Modem_And_Ardopcf` | **additionally skipped unless `PAT` is set** (`:624`) | 2026-07-17 |
| Receive chain against wild, uncontrolled 40 m ARDOP traffic | [docs/ardop/plan.md](../archive/ardop/plan.md) A0-A2; raw evidence at `/home/tf/ardop-campaign-evidence/` | reproducible from the commands in that README | 2026-08-08 |
| Per-mode decode floors against the Watterson rig | `WattersonMaskTests`, 8 smoke rows + 23 full-tier rows | smoke rows **in CI**; full tier under `SM_MASK_GATE=1` | 2026-08-08 |
| A pdn-to-pdn ARQ session at ARDOP's native 1500 Hz centre, sharing a channel with a packet modem | `ArdopSharedChannelSessionTests`, the `InlineData(null, SampleRate)` case, which the test itself labels the control | **skipped unless `ARDOP_SESSION_BENCH=1`** | 2026-08-02 |

**Read the gate column before the claim column.** The three rows that carry every interop assertion this project makes about ARDOP (Rung 3, Rung 4, and the host-protocol diff) all live in `ArdopHostLiveTests`, which is **skip-by-default and has never run in CI**: `.github/workflows/ci.yml` sets no `ARDOPCF`, no `ARDOP_ALOOP_CARD` and no `PAT`, so on every automated run those tests skip silently. Their last substantive commit is `200de2c`, 2026-07-17. So the interop evidence is a **hand-run bench result from July 2026**, on an `snd-aloop` cable, since re-verified by nobody and by nothing. It is good evidence of what it covers. It is two months old, it covers one peer implementation, and it has no continuous guard.

It also cannot currently be re-run on the dev box: **`snd-aloop` is not available inside this LXC**, so the `ARDOP_ALOOP_CARD` legs have no rig to run on here. That is a limitation to record, not a task to schedule before the session.

What the wild-corpus campaign adds is receive only, and says so: 749 frames acquired and 605 bodies ok over ~45 h of capture at the corrected 1500 Hz centre, and genuine per-frame disputes against ardopcf running 9-4 in our favour ([docs/ardop/plan.md](../archive/ardop/plan.md), A1 finding 5 and the A1 addendum). **Nothing in that campaign is on-air evidence and nothing in it transmitted a single sample.**

### 2.1 What has never happened at all

- **pdn-soundmodem has never keyed any transmitter with an ARDOP waveform**, not on air and not into a load. The dummy-load procedure exists and is unexecuted: [docs/flex-integration.md](../archive/flex-integration.md) §8 item 2, and roadmap #11's remaining item is exactly "a FreeDV-datac / ARDOP frame into the dummy load".
- **No live ARQ session has ever exercised the ConAck acceptance shipped in M0LTE.Ardop 0.4.0.** It has bench and wild-replay evidence only ([docs/ardop/plan.md](../archive/ardop/plan.md), leg 2). That change is on the connection path, so rung 4 of this document is the first thing that will ever exercise it for real.
- **No ARDOP session of any kind over RF, at any centre.**

## 3. The station, as measured

Read-only from the live box on 2026-09-12. The daemon at the time was started 2026-09-07 17:45:11, PID 511554.

### 3.1 Callsign: M0LTE

Settled by Tom: the transmissions are made under **M0LTE**, not GB7RDG. The reason is worth stating because it governs how the whole session is filed: this is a hand-run experiment by a licensed operator sitting at a radio, not a service the node offers. Keeping the experiment's callsign distinct from the node's keeps the two separable in anybody's log, including ours, and means nothing heard as GB7RDG was ever a hand-driven test. The roadmap says the same thing in one line: "Operate as **M0LTE**."

### 3.2 The band plan, verbatim from the journal

```
dial: 7.049450 MHz USB
  modem 0 afsk300-il2pc at 7.050300 MHz = 850 Hz audio
  modem 3 afsk300 at 7.050570 MHz = 1120 Hz audio
  modem 1 ardop at 7.050950 MHz = 1500 Hz audio
  modem 2 bpsk300 at 7.051600 MHz = 2150 Hz audio
flex: setting the slice to 7.049450 MHz and the transmit filter high cut to 2550 Hz from the band plan
flex: setting the slice receive filter to 450-2550 Hz, to hear everything the modems are placed across
ardop host tcp: 0.0.0.0:8200 (data 8201, ardopcf-compatible virtual TNC, modem 1)
audio: flex:discover DAX 24000 Hz -> 12000 Hz (slice A, dax 2, headless 7.049450 MHz ANT1 DIGU)
```

| | |
|---|---|
| Node | `root@pdn-soundmodem`, 10.45.0.37 on the LAN; config `/etc/pdn-soundmodem/soundmodem.json` ([docs/uplink-plan.md](../archive/uplink-plan.md):401) |
| Radio | FlexRadio 6500, slice A, DAX channel 2, headless, ANT1, DIGU, 50 W (the power setting and what ANT1 terminates into on the day are the operator's to confirm) |
| ARDOP host interface | `pdn-soundmodem:8200` command, **8201 data** (data port is always command + 1, `ArdopHostServer.cs:57`) |

**Two happy consequences of the measured plan.** ARDOP sits at **1500 Hz audio, its own native centre**, so `ArdopChannelShift` is a pass-through on this station and none of the shifted-centre machinery is in the path. The channel's DSP rate is **12000 Hz**, which is the ARDOP engine's own rate, so `ArdopChannelBridge` does no resampling either. On GB7RDG the bridge is a no-op in both of its dimensions, and that is the simplest wiring ARDOP can have here.

Beware two slot numberings. The [UK 40 m band plan](https://ukpacketradio.network/info:40m) counts from 1, so **ARDOP is band-plan slot 2**, quoted verbatim: "Slot 2 is reserved for ARDOP (not AX.25) in coordination with existing users." The daemon's `subChannel` counts from 0, so the same slot is **sub-channel 1** (it appears as `modem 1` in the journal above). The band plan also states the guard: "With 500Hz occupied bandwidth, there is 150Hz guard between slots."

"In coordination with existing users" is the operative phrase for this whole document. The existing users on this slot are known by name from the off-air campaign: **GB7BPQ, GB7BWR-2 and DC7DE** ([docs/ardop/plan.md](../archive/ardop/plan.md), A0 scoreboard). They were there before us.

### 3.3 What state the TNC is in right now

Read-only probe of the running host interface, 2026-09-12:

```
VERSION pdn-soundmodem_0.4.0
STATE DISC
MYCALL            (empty)
GRIDSQUARE        (empty)
ARQBW 2000MAX
LISTEN TRUE
PROTOCOLMODE ARQ
ARQTIMEOUT 120
AUTOBREAK TRUE
```

Three things in that block matter more than the rest.

**`MYCALL` is empty, and that is the only thing making the station inert.** With no callsign it cannot answer an inbound call and it faults on every transmit command. `LISTEN` is already `TRUE` and `PROTOCOLMODE` is already `ARQ`, so the moment a callsign is set this station will answer anybody who calls it. Setting `MYCALL` is therefore the act that makes the station live, and it appears in the ladder below as a deliberate, named rung of its own rather than as setup. Undoing it is in section 8.

**`ARQBW` reads `2000MAX` although the config says `"bandwidth": 500`.** The config key never reaches the TNC. It is consumed only by the survey's band accounting (`Program.cs:1378`) and by the transmit-filter plan; nothing in this repository sets `ArqBandwidth` or `CallBandwidth`, so the library default at `ArdopArqConfig.cs:24` stands, and that default governs both what we accept as IRS and what we request as ISS. Two doc comments claim otherwise and are wrong: `DaemonConfig.cs:84` ("Setting it also caps what ARDOP will negotiate") and `RfPlan.cs:409-410` ("plans for less and caps what it negotiates"). Filed separately as `/home/tf/ardopcall/docs/issue-arqbw-not-capped.md`; read it once, and do not restate it on the day.

The operational consequence is a hard pre-flight requirement: **set `ARQBW` explicitly on every run and read it back before anything transmits** (pre-flight item 6, and the `--bw` flag on every transmitting rung). Nothing in the station's configuration will do it for you.

**`ARQTIMEOUT` is 120 s.** That is the idle-session timeout the ladder's rungs 4 and 5 inherit unless `--timeout` says otherwise.

### 3.4 What the transmit filter will and will not save you from

The slice's transmit filter high cut is **2550 Hz**, set from the band plan. At a 1500 Hz audio centre:

| Session bandwidth | Audio span | RF span | Inside the filter? | Inside slot 2? |
|---|---|---|---|---|
| 500 Hz | 1250 to 1750 Hz | 7050.70 to 7051.20 kHz | yes | **yes** |
| 2000 Hz | 500 to 2500 Hz | 7049.95 to 7051.95 kHz | **yes** | **no** |

So a negotiated 2000 Hz session fits inside the transmit filter and would go out essentially intact, spanning modem 0 (7050.30), modem 3 (7050.57) and modem 2 (7051.60), and well outside what slot 2 is coordinated for. **The radio will not stop it.** The only thing that stops it is `ARQBW`, which 3.3 has just established does not come from the config. This is why `--bw` is on every transmitting command line below and why pre-flight item 6 exists.

### 3.5 The free calibration signal

GB7BPQ runs a half-hourly beacon and ID pair, measured over the corpus: a 4FSK.500.100 FEC beacon at about **:00:37 and :30:37** past the hour, and a two-frame ID sequence about twenty seconds later at **:00:57 and :30:57** ([docs/ardop/plan.md](../archive/ardop/plan.md), A0 scoreboard and A1 finding 2). Two things follow.

1. It is a recurring, free, known-good receive check: if the station does not read that ID pair, the receive chain or the radio is wrong and nothing further down this ladder means anything.
2. It sits **at the decode threshold for both implementations**: ardopcf fails the beacon 31 times out of 34 over the corpus (A1 finding 2), so failing to decode the beacon is not evidence of a fault. The **ID pair** is the check, not the beacon.

Plan the ladder around those marks. Do not transmit across them.

### 3.6 The station is busy, and it is busy talking to our intended peer

Measured on 2026-09-12: the station is **actively forwarding mail to GB7BPQ over bpsk300 on modem 2**, one frame every 20 to 25 seconds in bursts, with GB7BPQ answering at 6 to 9 dB SNR. PD4R-12 and EI0RSI-7 were also heard within the hour.

Two consequences, both of which shape the timing of the session.

- **One radio serves all four slots.** ARDOP bypasses channel access (4.2), so an ARDOP transmission of ours will key over a packet exchange of ours that is mid-flight. Check that the **packet** slots are idle, not just the ARDOP slot.
- **GB7BPQ is simultaneously our intended ARDOP peer and our current mail-forwarding correspondent.** An ARQ session with it lands on a station we are already working on another slot. That is not a reason to pick a different peer, it is a reason to pick a quiet moment and to expect the mail forwarding to stall for the length of the session (4.3).

## 4. The two facts that govern everything below

### 4.1 There is no busy detector

Our TNC never sends `BUSY TRUE` or `BUSY FALSE`, and `BUSYDET` and `BUSYBLOCK` are accepted and inert (`M0LTE.Ardop` `Host/ArdopHostTnc.cs:44-56`, `:481-489`; README.md:96). A real ardopcf does send them. The ARDOP spec assumes them: §2.7 lists "listen before transmit and busy detectors" as how the protocol minimises interference, and rule 1.5 makes `ConRejBusy` the mechanism by which a busy channel refuses a connection ([docs/refs/ardop-spec-rev2.md](../refs/ardop-spec-rev2.md):44, :449).

So: **no software in this path will decline to transmit because somebody else is using the channel.** Not the TNC, not the daemon, not the tool.

### 4.2 ARDOP transmissions deliberately bypass channel access

An ARDOP burst is queued with `ownsChannelTiming: true` (`src/Packet.SoundModem.Daemon/Program.cs:1994`), and that flag **skips both the transmit inhibit and the p-persistence roll** (`src/Packet.SoundModem/Channel/SoundModemChannel.cs:446-452`, `:507-509`, `:993-999`). It was the right fix for a real problem, and the problem is stated in the code: at a shifted centre ARDOP's own signal sits inside a packet modem's passband and asserts that modem's busy detector, so deferring would mean partly deferring to itself, and an ARQ turnaround has a budget a p-persistence roll does not respect. It was measured costing about 20 % of session time at the shipped p=63 before the fix ([docs/mode-validation.md](../mode-validation.md), 2026-08-02). Shipped in `3c7fdd5` (#171), 2026-08-02.

The consequence is not a defect and is not up for debate on the day: **the operator is the only channel-access mechanism this station has for ARDOP.** Every rung below is written on that basis.

Two corollaries worth knowing before you key.

- **The station is deaf while it transmits.** Receive processing is gated off for the length of every keyup (half duplex, `SoundModemChannel.cs`, the `TransmittedAudio` remarks). It cannot hear a collision it is causing, and it cannot tell you afterwards that it caused one. Only a second receiver can (section 11).
- **Two different two-tone tests exist and they behave oppositely.** The daemon's operator-page test transmission and `--two-tone` are queued like a frame and **do** defer to carrier sense and to an ARQ hold (PR #415, roadmap #18). The ARDOP host command `TWOTONETEST` goes out through the TNC's transmitter, which is the bypassing path, and **does not defer** (`ArdopHostTnc.cs:984-993`). If a level check is wanted on this slot, prefer the daemon's.

### 4.3 What an ARQ session of ours does to the node

GB7RDG is a live node carrying traffic ([docs/uplink-plan.md](../archive/uplink-plan.md):401, :575), and 3.6 has it forwarding mail right now. While our ARQ engine is connected or pending, `channel.TransmitInhibit` holds every packet transmission on the other three sub-channels off the air (`Program.cs:2053-2057`). Frames are queued rather than discarded, but only until `TransmitInhibitTimeout`, whose default is **30 seconds** (`SoundModemChannel.cs:633`), after which they are dropped.

So an ARQ session longer than half a minute silently costs the node its packet transmissions, including the GB7BPQ mail forwarding. Keep rung 5's payload short, and put the session in a window when the node is quiet.

## 5. The tools

### 5.1 ardopcall

`ardopcall` drives the session: one verb per run, stdin as the data path, stderr as the operator's view, a transcript file when asked for. Its design and the exact host-protocol behaviour it relies on are at `/home/tf/ardopcall/docs/design.md` and `/home/tf/ardopcall/docs/host-protocol.md`.

**Status, stated plainly: `ardopcall` is specified and not yet built.** As of 2026-09-12 that repository holds documentation, a licence and build props: no source, no commits. **Every command line in section 7 is therefore unverified**, written against the tool's design document, and must be checked against the tool's own `--help` on the day. If the tool does not exist by the session, the fallback is the raw host protocol over `nc` or a small script, using the command sequences in `host-protocol.md` §"The commands ardopcall uses"; the ladder's shape and its decisions do not change.

Two properties of the tool this procedure depends on:

- **It speaks the host protocol directly and depends on no ARDOP library**, so the same binary drives our TNC and a real ardopcf. That is what makes a disagreement between them a measurement rather than a guess.
- **A flag left unset is not sent to the TNC at all**, so the TNC's own default governs and the transcript never silently restates a value the tool invented. Note what 3.3 makes of that: an unset `--bw` leaves `ARQBW 2000MAX` in place. On this slot, always set it.

### 5.2 The reference TNC, for the differential check

ardopcf 1.0.4.1.3 at the pinned commit `a7c9228` is built at `/home/tf/ardopcf/ardopcf` and runs with no sound card at all, on ALSA's `null` PCM:

```sh
/home/tf/ardopcf/ardopcf 18501 null null -m
```

Its host interface answers identically to ours on the commands checked so far (`VERSION ardopcf_1.0.4.1.3`, `MYCALL now M0LTE`, `STATE DISC`). That gives a free pre-flight: drive the same script at both TNCs and compare transcripts, which is exactly the seam `ardopcall` exists for.

**Its limit, recorded so nobody mistakes it for the Rung 3 rig:** this is a **host-protocol** comparison only. `snd-aloop` is not available in this LXC, so no audio flows between two instances here and the over-audio interop legs cannot be re-run on this box (2). A disagreement found this way is a host-interface finding; agreement here says nothing about the waveform.

## 6. Pre-flight, in order

Do not skip an item because the previous session did it. Every one of these has a failure mode that is silent from the operator's chair.

1. **Confirm the callsign to be used is M0LTE** and understand why (3.1). `-s M0LTE` on every transmitting subcommand; the tool has no default callsign and never will (`design.md`, non-goals).
2. **Confirm the licence conditions** for the transmissions about to be made, and the operator's own identification obligations. An ARDOP `IDFrame` carries the callsign in the data waveform; **CWID is accepted and inert in our implementation, so no CW identification is sent** (`ArdopHostTnc.cs:108`, README.md:97). If a CW ident is wanted, it is not coming from this TNC.
3. **Read the node's journal and confirm the station is healthy on receive**, all four sub-channels: `journalctl -u pdn-soundmodem -n 100`. Compare the band-plan block against 3.2 line for line. A different dial or a different centre means the station is not the station this document was written for, and the frequencies in section 7 are then wrong.
4. **Confirm the radio**: dial, sideband, ANT1, power, and that nothing else on the LAN owns the slice. A second headless instance on the same Flex steals the slice unless it has its own DAX channel.
5. **Probe the TNC read-only and compare against 3.3.** `VERSION`, `STATE`, `MYCALL`, `ARQBW`, `LISTEN`, `PROTOCOLMODE`, `ARQTIMEOUT`. Query form is the bare command with no parameter. Expect `STATE DISC` and an empty `MYCALL`; anything else means somebody is already using it.
6. **Set `ARQBW` explicitly, and read it back.** The config does not do this (3.3) and the transmit filter will not catch it (3.4). `ARQBW 500MAX` for this slot. Verify with a bare `ARQBW` and see `ARQBW 500MAX` come back before any transmit command is sent. If `ardopcall`'s `--bw` is doing it per run, still read it back once by hand the first time and confirm the tool sends what it claims.
7. **Run the differential check against ardopcf** (5.2): the same setup script at both TNCs, transcripts diffed. Costs nothing, needs no radio, and is the cheapest way to find out that the tool is wrong before the tool is pointed at an antenna.
8. **Listen. On the ARDOP slot, for at least one full half-hour mark.** This is the step the software cannot do for you (4.1). Watch the waterfall and the frame log, and confirm you have heard GB7BPQ's :00:57 or :30:57 ID pair decode (3.5). If the ID pair does not decode, stop: the receive chain is wrong and the ladder is meaningless.
9. **Confirm the ARDOP slot is clear right now.** A session in progress is two stations, and the one you cannot hear is the one you will land on. Watch for a full turnaround cycle (a `ConReq`/`ConAck` pair, or `DataACK`/`IDLE` traffic) before deciding the slot is idle. Prefer a window that is not a half-hour mark.
10. **Confirm the packet slots are idle too** (3.6). One radio serves all four, and the mail forwarding to GB7BPQ runs in bursts every 20 to 25 seconds. Wait for a gap between bursts, and prefer a window where the node has nothing queued.
11. **Decide the window and write it down.** Rungs 1 to 3 take seconds each; rungs 4 and 5 take minutes. Start the session log before the first transmission and keep one clock (UTC) for everything.
12. **Run the dummy-load rung first if it has not been run** (rung 0b). Nothing here has ever keyed a transmitter with ARDOP audio (2.1).

## 7. The transmit ladder

Increasing risk and increasing channel occupancy. Each rung has an explicit decision, and the decision is a gate: **do not climb past a rung that did not pass.** Record the result of every rung whether it passed or not.

Throughout, `EV` is the evidence directory for the session (section 11):

```sh
EV=/home/tf/ardop-campaign-evidence/on-air-2026-MM-DD
mkdir -p "$EV"
```

### Rung 0: receive-only monitor. No transmission.

```sh
ardopcall -t pdn-soundmodem:8200 monitor --log "$EV/r0-monitor.log"
```

`monitor` attaches, sets `PROTOCOLMODE RXO` and prints every line and every data block. RXO is receive-only by construction and the TNC refuses `ARQCALL` and `PING` from it, so this rung cannot transmit even by mistake.

- **Success:** the transcript fills with the band's own traffic, and a `PTT TRUE` never appears. Ideally it catches a GB7BPQ ID pair on a half-hour mark, with `IDF`-tagged blocks arriving on the data socket.
- **Failure:** nothing at all over a half-hour mark that the waterfall shows carrying signal. That is a receive or attach fault, not an ARDOP one.
- **Decision:** if the tool cannot attach, or sees nothing where the frame log sees frames, **stop**. The host interface or the bridge is wrong and no transmission will teach you anything.

Two notes. Attaching displaces whatever host was on those sockets: the TNC accepts one host at a time per socket and a new connection silently replaces the previous one (`ArdopHostServer.cs:193-210`). And `monitor` sets `PROTOCOLMODE RXO`, which takes the station **out** of the ARQ mode 3.3 found it in; rung 4 puts it back.

### Rung 0b: one ID frame into a dummy load. First key ever.

Owed by roadmap #11 regardless of this campaign, and the only rung on this ladder with no channel risk at all. Terminate the transmit path in a load, then run rung 1's two commands unchanged. This is the first time any ARDOP audio from this codebase reaches a PA.

- **Success:** `PTT TRUE` then `PTT FALSE` in the transcript, the Flex reporting `interlock=TRANSMITTING`, RF into the load, no setup errors. The existing procedure and its success criteria are at [docs/flex-integration.md](../archive/flex-integration.md) §8 item 2.
- **Failure:** no keying, keying with no RF, a `FAULT`, or audio that is obviously wrong on the panadapter.
- **Decision:** a failure here is a transmit-path fault and **stops the ladder**. A pass closes roadmap #11's last item and should be recorded as such.

### Rung 1: give the station a callsign, then send one ID frame

**This rung has two halves and the first one is not paperwork.** Setting `MYCALL` is what takes the station from inert to live: `LISTEN` is already `TRUE` and `PROTOCOLMODE` is already `ARQ` (3.3), so from the moment a callsign exists, this station will answer anybody who calls it, whether or not you are at the keyboard. Do it deliberately, at the start of the session, and undo it at the end (section 8).

```sh
ardopcall -t pdn-soundmodem:8200 -s M0LTE --bw 500 --grid IO91lk id --log "$EV/r1-id.log"
```

`-s` sends `MYCALL M0LTE`; `--bw 500` sends `ARQBW 500MAX` and must be on every transmitting run (3.3, 3.4); `--grid` is optional and sets `GRIDSQUARE`. The locator is `IO91lk`, taken from the station's own `publish.location` in `/etc/pdn-soundmodem/soundmodem.json`, so it is the one this station already declares to the monitor site rather than one this document invented. The action itself is one `SENDID`: a single short 4FSK frame carrying the callsign (`host-protocol.md`, "Things that key the transmitter").

- **Success:** `PTT TRUE` ... `PTT FALSE` in the transcript, one keyup of well under a second, the waterfall showing the burst in the right slot at the right width, and a second receiver decoding it as an `IDFrame` carrying M0LTE (section 11).
- **Failure:** no keying; `FAULT MYCALL not set` (the callsign did not take); a burst on the wrong frequency or visibly wider than the slot; or nothing decodable at the second receiver.
- **Decision:** wrong frequency or excessive width **stops the ladder** and is a bug report, not a retry. Keying with nothing decoded anywhere is ambiguous on one receiver and worth one repeat with a second witness before stopping.

This is also the rung to judge the transmit level on. Do it by looking at ALC and at the second receiver's picture, not by guessing: the house receive-level guidance and its derivation are in [docs/receive-levels.md](../receive-levels.md), and **there is no measured transmit-level figure for ARDOP anywhere in this repository**, so any drive level arrived at on the day is a new measurement and belongs in the evidence.

### Rung 2: a ping to a known station

```sh
ardopcall -t pdn-soundmodem:8200 -s M0LTE --bw 500 --attempts 2 ping GB7BPQ --log "$EV/r2-ping-gb7bpq.log"
```

A ping is two short frames and a reply, and it is the first rung that asks another station's software to answer. `--attempts 2` rather than a long repeat train: this is a courtesy probe, not a beacon.

- **Success:** `PINGACK <snDb> <quality>` on the command socket (`ArdopArqEngine.cs:558`), carrying the far station's reported signal-to-noise in dB and quality 0-100. **Write both numbers down.** They are the first end-to-end path measurement this project has ever had for ARDOP.
- **Partial:** our transmission goes out, a second receiver hears it, and no `PINGACK` returns. That is either a path that does not work in the return direction or a peer with `ENABLEPINGACK` off, and the two are not distinguishable from here.
- **Failure:** a `FAULT` (wrong mode, callsign not set), or no keying.
- **Decision:** a `PINGACK` is a green light for rung 3. A silent ping is not a stop: try the other known stations (GB7BWR-2, DC7DE) once each, at intervals, and if none answers, continue to rung 3 knowing the return path is unproven. Do **not** repeat-hammer a station that does not answer; that is exactly the behaviour the missing busy detector makes antisocial.

### Rung 3: a connectionless FEC transmission

```sh
printf 'M0LTE ARDOP TEST %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  | ardopcall -t pdn-soundmodem:8200 -s M0LTE --fecmode 4FSK.500.100 fec - --log "$EV/r3-fec.log"
```

FEC is connectionless: it transmits and nothing answers. It is the last rung that occupies the channel only for as long as you told it to. `4FSK.500.100` is the slot's workhorse rung and the type the corpus knows best; it is also the type whose wild body-decode rate is 60 %, with failures that are a known threshold emitter rather than a defect ([docs/ardop/plan.md](../archive/ardop/plan.md), A1 finding 2). **`--fecmode` is implied by the design's `fec` row ("at a chosen frame type") but is not named in its global-options list, so check the real flag against `--help`.**

- **Success:** one bounded keyup of a few seconds, the burst in the right place, and a second receiver decoding the text byte-exact. An `FEC`-tagged block coming back on our own data socket would only mean we decoded our own audio, which a half-duplex station cannot do; the witness is the second receiver.
- **Failure:** a keyup that does not end when it should (`FECSEND TRUE` with nothing stopping it is the one command on this ladder that can hold the PA), or an undecodable burst at a receiver that hears the level fine.
- **Decision:** if the keyup does not end on its own, execute section 8 and **stop the ladder**. Anything else, continue.

### Rung 4: a short ARQ connect at 200 Hz

The first rung that asks for a session, and the first thing that will ever exercise the ConAck acceptance shipped in M0LTE.Ardop 0.4.0 on a live path (2.1). **200 Hz first**, deliberately: it is the narrowest class, it is the class the wild corpus shows nearly clean (76 of 83 bodies ok, the campaign's own guard rail), and it occupies the least channel.

```sh
ardopcall -t pdn-soundmodem:8200 -s M0LTE --bw 200 --attempts 3 --timeout 120 \
  connect GB7BPQ --log "$EV/r4-arq200-gb7bpq.log"
```

Run it from an interactive terminal so stdin stays open; an EOF on stdin is an end-of-data condition and will take the session down before you have looked at it. `--attempts 3`: three `ConReq` attempts, then it gives up. Do not raise it.

`--bw 200` without `--forced` sends `ARQBW 200MAX`, which lets the far end negotiate at or below 200 Hz. `--forced` pins it and refuses anything else; use it only if a negotiation goes somewhere you did not intend. **Under no circumstances leave `--bw` off**, which would leave the station at its measured `2000MAX` default and permit a session four times the coordinated width (3.4).

**An alternative worth preferring if a peer's operator can be asked:** have them call us instead, and run

```sh
ardopcall -t pdn-soundmodem:8200 -s M0LTE --bw 200 listen --log "$EV/r4-arq200-inbound.log"
```

which sets `LISTEN TRUE`, never initiates, and transmits only on answer. It exercises the IRS role rather than the ISS role, and it is the politest possible first session.

- **Success:** `PENDING`, `TARGET M0LTE`, then `CONNECTED <call> 200`, data relayed, then a clean `DISCONNECTED` after a `DISCONNECT`. **That is the exit criterion met** (section 1), at 200 Hz. Stop and record it before doing anything else.
- **Partial:** `REJECTEDBW <call>` (the far end refuses on bandwidth) or `REJECTEDBUSY <call>` (the far end's busy detector refused us, which is the deployed peers doing the job ours cannot). Both are informative, both are clean, and neither is a fault of ours.
- **Failure:** `ConReq` sent repeatedly with nothing back; a connect that establishes and then drops without carrying data; or the `CONNECT TO ... FAILED` shape discussed in 9.1.
- **Decision:** a session that establishes, carries data and tears down cleanly is a pass. A connect that fails silently gets **one** retry at most, then stop for the day and take the transcripts away to read. Repeated failed `ConReq` trains on a shared slot with no busy detector is precisely the behaviour to avoid.

### Rung 5: 500 Hz with real data

Only after rung 4 has passed. This is the slot's nominal class (the band plan says "ARDOP 500Hz") and the class our masks and the corpus both know best. It is also the only class that fits slot 2's coordinated width (3.4).

```sh
ardopcall -t pdn-soundmodem:8200 -s M0LTE --bw 500 --attempts 3 --timeout 300 \
  connect GB7BPQ --log "$EV/r5-arq500-gb7bpq.log" < "$EV/payload.txt"
```

Keep `payload.txt` short: a few hundred bytes of known, checkable text. Remember 4.3, and remember 3.6: the node's mail forwarding to this same station is held off the air for the session, and its frames start being dropped after 30 seconds.

- **Success:** `CONNECTED <call> 500`, the payload delivered intact, `BUFFER` counting down to zero, an orderly `DISCONNECTED`. Compare bytes at both ends if the far operator can say what arrived.
- **Failure:** a session that gears down and stalls, a session that drops mid-transfer, or a payload that arrives corrupt.
- **Decision:** whatever happens, this is the end of the ladder for the session. Tear down, stop transmitting, return the station to inert (section 8), and write it up.

### Out of scope for this bench

A Winlink gateway connection (optional gravy, section 1); anything above 500 Hz on this slot (3.4); and any unattended or automatic operation whatsoever (section 10).

## 8. Abort, and returning the station to inert

**The immediate stop is the operator's hand on the radio.** Everything else below is a request to software that is already transmitting.

**Killing `ardopcall` stops most things, but be precise about which.** Dropping either host socket triggers the TNC's host-link failsafe (`ArdopHostServer.cs:325-338`, ardopcf `ARDOPCommon.c:424`), which calls `HostLinkLost` (`ArdopHostTnc.cs:278-290`). That does two things: it sets the FEC abort flag, ending a `FECSEND` transmission, and it calls `Engine.Disconnect`, which is the **orderly** ARQ teardown, not the dirty one. An orderly teardown transmits: `CheckForDisconnect` (`ArdopArqEngine.cs:1279-1300`) sends a `DISC` frame and repeats it on a 2000 ms interval until the far end ends the session or the timeout expires.

Stated honestly, and this sharpens the shorter form of the claim in `ardopcall`'s own design document:

- **With a FEC transmission in progress**, killing the tool stops it.
- **With an ARQ session up**, killing the tool starts a disconnect, and the disconnect itself keys the radio a few more times over a few seconds. That is correct protocol behaviour, and it is also not silence.
- **Audio already handed to the sound device plays out.** Nothing at the host interface reaches into the transmit buffer.

The ladder, in order of preference:

1. **`DISCONNECT`** (orderly): let the session end properly. This is what `ardopcall`'s own quit path should do.
2. **`ABORT`** (dirty): drop everything and return to the `DISC` state (`ArdopArqEngine.cs:411-419`, ardopcf `ARQ.c:2544`). Use when the session is misbehaving rather than merely unwanted.
3. **Kill `ardopcall`** (the failsafe): as above, with the DISC caveat.
4. **Stop the daemon** on the node: `systemctl stop pdn-soundmodem`. This takes the node's packet service down with it, so it is not free.
5. **Unkey at the radio.** Take the PA out of circuit, or power the radio down. This is the only step that is instantaneous and unconditional, and it is the one to take if the transmitter is stuck.

Know which of these you will use, and have the node's shell already open, **before** the first transmission of rung 1.

### 8.1 Ending the session: put `MYCALL` back

The station arrived at this bench inert only because `MYCALL` was empty, with `LISTEN TRUE` and `PROTOCOLMODE ARQ` already set (3.3). Rung 1 removed that protection deliberately. **It has to be put back, or the station is left able to answer ARDOP calls unattended**, which section 10 says it is not authorised to do.

Restart the daemon (`systemctl restart pdn-soundmodem`), which returns the TNC to its configured state, then **verify** by probing it exactly as pre-flight item 5 did: `MYCALL` must come back empty and `STATE` must read `DISC`. Read the restart's journal against 3.2 line for line and confirm nothing else moved: this is a live node, and the discipline for touching it is the one [docs/uplink-plan.md](../archive/uplink-plan.md):575 sets, that every line that was there before is still there and in the same order.

## 9. Known risks, stated honestly

### 9.1 The flaky pdn-to-pdn session bench, and the open question inside it

`tests/Packet.SoundModem.Tests/Ardop/ArdopSharedChannelSessionTests.cs:420-434` carries a comment that is the most relevant unresolved thing in this repository to the rung-4 and rung-5 outcomes. It is quoted rather than paraphrased because its own conclusion is that it does not know:

> The suspected cause is a real behaviour change, not a bad test: the two stations here share one simulated channel, and until ARDOP was given ownsChannelTiming (#171) CSMA kept them from transmitting over each other. Real ardopcf stations have no CSMA - ARQ owns its own turnaround timing - so the bench is now exposing collision behaviour it was previously shielded from. Whether that is the bench's cross-connect being unrealistic or a genuine timing margin problem is unresolved, and is the open question, not the flake itself.

Measured: 2 of 4 runs failing on 2026-08-02, against 6 of 7 green when the bench was written. **The failures are sessions that do not establish ("CONNECT TO ... FAILED"), never wrong data.** It was gated behind `ARDOP_SESSION_BENCH=1` rather than loosened, deliberately, because widening a wait would hide the thing worth knowing (`fcdf584`, #175).

**Two thirds of that test's cases do not describe this station.** The bench runs three: the native 1500 Hz centre, which the test's own comment labels the control ("ARDOP's own centre, nothing in the path but a pass-through"), and two shifted-centre cases at 950 Hz. GB7RDG runs ARDOP at **1500 Hz native** (3.2), so the shift is not in the path here at all, and the shifted cases carry no warning for this session. The control case is the one that matches this station, and it is the one that was written as the thing that should always pass.

The residue worth keeping is narrower than the comment's own framing suggests: the collision mechanism it describes is centre-independent, because it comes from two stations sharing one channel with CSMA bypassed. On air, the two stations do not share a transmitter, they share a path, and they are not cross-connected sample-for-sample. So the bench's geometry is less like the air than it first reads.

What that means for the day: **if rung 4 produces a connect that does not establish, that is the signature the bench produces, and it is a data point on the open question rather than a mystery.** Capture the transcript and the station's journal in full before retrying. A rung-4 failure that looks like the bench's failure makes the "genuine timing margin problem" branch considerably more likely; a rung-4 success makes the "unrealistic cross-connect" branch more likely. Either way it is the first evidence anyone will have had.

### 9.2 We can transmit over a session we cannot hear

Sections 4.1 and 4.2 together. Mitigations are all procedural: rung 0 first, the listening window in pre-flight items 8 to 10, bounded attempt counts, and a human present. There is no software mitigation available and none is claimed. The busy-detector port is drafted as an issue (`/home/tf/ardopcall/docs/issue-busy-detector.md`), and the roadmap already flags it: "add the busy-detector port if channel-sharing needs it on air" (roadmap #6).

### 9.3 `ARQBW` is not what the config says it is

3.3 and 3.4. The risk is a 2000 Hz session that fits the transmit filter, sits across three of our own slots and outside slot 2's coordination. It is fully mitigated by setting `ARQBW` on every run and reading it back (pre-flight item 6), and not mitigated at all by anything else. Cross-reference `/home/tf/ardopcall/docs/issue-arqbw-not-capped.md`.

### 9.4 The interop evidence is old and narrow

One peer implementation (ardopcf 1.0.4.1.3), one rig (`snd-aloop`), one date (2026-07-17), skip-by-default, never in CI, and not currently re-runnable on the dev box (2). The wild corpus shows the slot carries traffic from implementations we cannot name, and the design doc records ARDOP_Win behavioural differences as an explicit Rung 5 finding to record ([docs/ardop-design.md](../ardop-design.md) §9.2). **If a live peer behaves differently from ardopcf, that is a finding to write down, not a bug to fix on the day.**

### 9.5 The top rungs will not fly, and that is expected

8PSK.500.100 and 16QAM.500.100 decode essentially nothing under CCIR Moderate or Poor at any plausible NVIS SNR, measured on the sim ladder, and all 17 wild 16QAM bodies in the corpus fail for ardopcf too ([docs/ardop/plan.md](../archive/ardop/plan.md), A1 finding 1 and A3). If a session gearshifts up and stalls, that is the waveform meeting the channel, not our decoder. Do not chase it.

### 9.6 The ~2 % single-shot acquisition ceiling

A small fraction of noise realisations false-trigger the leader detector during the lead-in and the capture swallows the real frame; measured as a ~98 % single-shot ceiling on the sim ladder, absorbed in deployment by ARQ retries ([docs/ardop/plan.md](../archive/ardop/plan.md), A3). Expect occasional retried frames in a healthy session. It is a named future receiver leg ("leader re-arm", leg 1), not a session fault.

### 9.7 The node is live and currently busy

3.6 and 4.3. An ARQ session costs the node its packet transmissions for the session's length and drops frames after 30 seconds of holding, and the traffic it is holding up right now is mail forwarding to the very station we intend to call. Pick the window accordingly.

## 10. What this bench does not authorise

It authorises **attended, single-session, operator-initiated transmissions** on 7050.95 kHz, made under M0LTE, on the rungs above, on the day.

It does not authorise leaving the station with a callsign set and `LISTEN TRUE` after the session (8.1), pointing a node or a Winlink client at port 8200 to run by itself, or any automatic retry loop. `ardopcall` has no automatic retry loop by design, and its design document says why: "a tool that retries unattended is the thing we are specifically trying not to build yet: the TNC has no busy detector, so a human stays in the loop." Unattended or node-backed operation on this slot is gated on the busy-detector port, and that is separate work with its own issue.

## 11. Evidence: what to capture and where it goes

The house pattern is set by `/home/tf/ardop-campaign-evidence/README.md`: raw run evidence lives outside the repository, the repository carries the durable record, and the README's own rule governs the files themselves.

> Do not edit these files. They are a record of what the instruments emitted, not prose.

That rule applies here without exception. A transcript with the boring parts tidied out is not evidence.

**Where:** a new directory `/home/tf/ardop-campaign-evidence/on-air-2026-MM-DD/`, with a `README.md` in the same shape as the existing one: a table of one row per file saying what it is, and the command that produced or reproduces it.

**What, per rung:**

| Source | File | Notes |
|---|---|---|
| `ardopcall --log` | `rN-*.log` | one per rung, both directions of the command socket plus every framed data block |
| the pre-flight probe | `preflight-tnc.txt` | the TNC's answers to item 5 and the `ARQBW` read-back of item 6, before and after |
| the node's journal | `journal-<rung>.txt` | `journalctl -u pdn-soundmodem --since ... --until ...`, covering each keyup |
| the station's frame log | `frames-<window>.csv` or a copy of the db | what the station itself heard around the session |
| a second receiver | `witness-*.wav` and its decode | see below |
| the differential check | `ardopcf-diff.txt` | the 5.2 transcripts, ours against ardopcf's |
| the operator | `notes.md` | UTC times, drive level, ALC behaviour, what the band sounded like, what was heard by ear |

**The second receiver is not optional.** The station is deaf while it transmits (4.2), so nothing in the first four rows above can tell you what you actually radiated. Two independent witnesses exist and cost nothing:

- **A public UberSDR instance**, which this project already uses as a receive chain: `m9psy-1.instance.ubersdr.org` decoded this exact slot plan off air on 2026-08-02 ([docs/mode-validation.md](../mode-validation.md)). A second `pdn-soundmodem` pointed at it with `--device ubersdr:...` and an `ardop` modem entry gives a full RXO transcript of our own transmissions from somebody else's antenna.
- **The station's own public page**, https://monitor.ukpacketradio.network/r/gb7rdg/ , which relays the station's own transmitted audio to viewers at -35 dB ([docs/uplink-plan.md](../archive/uplink-plan.md):92, :190, :608). That shows the keyup and its shape from the station's side of the path.

**After the session**, run `sm-ota ardop-monitor` over whatever raw capture covers the window, exactly as the off-air campaign does, so the session is scored by the same instrument as the wild corpus:

```sh
sm-ota ardop-monitor --raw <capture dir> --centre 1500 --quiet --csv "$EV/session-scan.csv"
```

**The durable record** goes in the repository, not in the evidence directory, and cites the evidence directory:

1. **This file**: results written into a new "Results" section, the way [docs/freedv-hf-loop.md](../archive/freedv-hf-loop.md) says to ("Record results in this file (replace the blank matrix)").
2. **[docs/mode-validation.md](../mode-validation.md)**: a dated ledger entry. The standing rule in CLAUDE.md is explicit that a proven mode gets one, naming the transition and the PR or issue that did it. `ardop` has no matrix row (it is a daemon modem entry, not a `ModemCatalog` mode) and the 2026-08-02 entry sets the precedent for recording it anyway.
3. **[docs/ardop/plan.md](../archive/ardop/plan.md)**: leg 2 of "Open legs" closed, with the exit criterion quoted and answered.
4. **[docs/roadmap.md](../roadmap.md)**: #6 closed, and #11's dummy-load item closed if rung 0b ran.
5. **[docs/plan.md](../plan.md) §17**: the amendment-log entry, which is authoritative where the three roadmap documents disagree.

**Honest negatives are recorded with their mechanism**, which is the campaign's own standing discipline. A session that did not connect, a ping that was never answered, a peer that behaved unlike ardopcf: all of those are results, and the one thing that must not happen is a ladder run whose failures go unwritten because the exit criterion was not reached.
