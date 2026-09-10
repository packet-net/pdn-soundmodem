# FreeDV datac over the air, from the GB7RDG node to a remote SDR

The campaign Tom asked for on 2026-08-15: add a `freedv-datac*` modem to the **production**
`pdn-soundmodem` config on host `pdn-soundmodem` (10.45.0.37), pass traffic through it on 40 m,
and receive it on a second `pdn-soundmodem` instance listening to a public UberSDR.

**Run complete, 2026-08-15** - see [What happened](#what-happened-2026-08-15-run-complete) at the
end. It worked, on both modes, but not to the receiver this plan was written around: read that
section before repeating any of the frequency or receiver choices below.

This is not [`freedv-hf-loop.md`](freedv-hf-loop.md), which is a two-ended bench loop over a
short path with both ends on the desk. The point here is the **deployment path at real range**:
the production daemon, the production radio, a real ionospheric hop, and a receiver we do not own.

## What is already proven, and what this adds

All six `freedv-datac*` modes are `✅ working` in
[mode-validation.md](mode-validation.md): waveform interop with stock codec2 both directions, and
an on-air campaign on 2026-07-28 across the Flex to RSP1 pad rig that matched the simulation
baseline (datac1 AWGN clean to +1.8 dB, exactly the sim figure).

That campaign transmitted through `sm-ota`, over a 125 dB attenuator chain, into a receiver on
the same bench. This one adds the three things it could not:

1. **The daemon's own transmit path**, not the harness's; frames arriving over KISS and leaving
   as datac bursts, with the daemon's channel access in front of them.
2. **A real path**, roughly 550 km Reading to Dalgety Bay, with the fading and the QSB that come
   with it.
3. **Coexistence**, a fourth modem sharing one Flex slice with three that are carrying live
   traffic.

## Station and instruments

Every row below was exercised on 2026-08-15 before the campaign was approved, rather than
assumed from documentation.

| Role | What | Verified |
|---|---|---|
| Transmit | `pdn-soundmodem` 10.45.0.37, `pdn-soundmodem.service` 0.34.2, `device: flex:discover`, ANT1 | root SSH; startup banner read from the journal |
| Radio | FLEX-6500 10.45.0.76, `External ref locked`, `transmit power 50 W, limit 50 W` | banner; 4992 reachable |
| Receive | second daemon on the claude-code box, `device: ubersdr:m9psy-1.instance.ubersdr.org` | connected live; planned `freedv-datac1` at 7054.0 to 1500 Hz audio on a 7.052500 dial |
| Receiver quality | M9PSY-1, RX888 with 40 m full wave loop, GPSDO, Dalgety Bay | `reference offset 0 Hz`, session cap 10800 s, no 429 |
| Clear-channel check | `sm-iqcapture` against the same instance | 12 s capture centred 7056, i.e. 7032-7080, GPS-timestamped, SHA-256 recorded |
| Offline planning | `device: flex:mock` | full startup with the fourth modem, no radio touched |

**The GPSDO at both ends removes the dial-correction trap.** The per-receiver reference error
that produced a total non-acquisition on the MS110D campaign (M0EYT at -64.8 Hz against a
+/-75 Hz acquisition grid) does not apply: the Flex is locked to an external reference and M9PSY
reports 0 Hz. Measure it anyway at the start of the session; it costs one tone.

## Where to put it, and why not higher

The IARU Region 1 HF band plan (effective 2016-06-01) splits the space in a way that runs
opposite to the intuition that "higher is emptier":

| Segment | Max bandwidth | Usage |
|---|---|---|
| 7050 - 7053 | 2700 Hz | All modes. Digimodes, **automatically controlled data stations (unattended)** |
| 7053 - 7060 | 2700 Hz | All modes. Digimodes |

7050-7053, where GB7RDG already sits, is the **only** 2700 Hz segment on 40 m designated for
unattended operation. 7053-7060 is not, so anything we run there is an **attended test that gets
taken down afterwards**, never left up. The plan's notes are explicit that unmanned stations
"shall only be activated under operator control". 2700 Hz is also a real ceiling: `datac1`
occupies 1700 Hz and `datac3` 500 Hz, so both are comfortable, but it is a ceiling.

**Place the test modem at 7054.000.** That puts the whole `datac1` emission at 7053.15-7054.85,
entirely inside 7053-7060, and about 900 Hz clear of the node's `bpsk300` at 7051.6. Going higher
inside Tom's 7052-7060 range is possible and costs real money, measured on `flex:mock`:

| Test modem at | Dial | Slice RX filter | TX high cut |
|---|---|---|---|
| *(today, no test modem)* | 7.049450 | 450-2550 Hz | 2550 Hz |
| **7054.000** | 7.049750 | 150-5350 Hz | 5350 Hz |
| 7058.000 | 7.049750 | 150-9350 Hz | 9350 Hz |

Note that the band plan's frequencies are transmitted frequencies, not suppressed-carrier dial
settings, so the dial landing below 7050 is not itself a band plan question; the emissions are
what count, and each stays inside its segment.

## What this costs the live node

Measured, not estimated. The RF placement of the three existing modems does not move by a single
Hz, but three things do change:

- **The dial moves** 7.049450 to 7.049750, and with it every modem's audio centre (afsk300
  850 to 550 Hz, ardop 1500 to 1200, bpsk300 2150 to 1850).
- **The slice receive filter opens** from 450-2550 Hz to 150-5350 Hz. That is 2.5x the noise
  bandwidth, about **3.9 dB more noise** into the DAX stream. The daemon filters per modem
  digitally, so decode performance should not follow it down, but that is a claim to test rather
  than assert: compare decode counts in `frames.db` for the hour before and the hour after.
- **The transmit filter opens** to 5350 Hz. This one is **radio-global and persistent**: it
  outlives the daemon, so nothing else on the radio will put it back. Restoring the config does,
  though, because the daemon states the high cut from the band plan at every start-up rather than
  inheriting it - measured on the takedown, 4950 Hz back to 2550 Hz. Verify it off the banner.

The restart also drops the BPQ KISS sessions (10.45.0.121 on ports 8101 and 8102) while
GB7RDG-2 is passing traffic to GB7BPQ and EI0RSI-1.

**Power is shared and cannot be split.** `txPowerWatts` is one setting for the whole daemon, and
the node already runs 50 W against a radio ceiling of 50 W. The test transmits at whatever the
node's own traffic does; there is no lower-power test option that does not also turn the node
down.

## Prerequisite: CW identification (done, 0.35.0)

When this campaign was scoped the daemon could not identify at all: `grep -rln Morse src/`
returned nothing, and `MorseGenerator` plus the interval logic lived only in
`tools/Packet.SoundModem.Ota`, wired into the harness's Flex transmitters. `idBeacons` is the
opposite of what is wanted here - a receive-only ghost, and CONFIG.md was explicit that "it never
transmits".

Tom chose (2026-08-15) to **port it into the daemon** rather than work around it, because the
daemon needs the capability for any on-air use and not just this campaign. **Shipped in 0.35.0**;
see [`identify`](../CONFIG.md#identify) for the reference and the amendment-log entry of
2026-08-15 in [plan.md](plan.md) for the reasoning. Shape as built:

- `MorseGenerator` moves from `tools/` into `src/Packet.SoundModem/`. `Real()` is pure DSP with
  no Flex dependency, so this is a move rather than a rewrite; the OTA harness keeps using it
  from its new home.
- **`identify` is a per-modem property, not a station-wide one** (Tom, 2026-08-15). A modem
  without the block never idents, so the three production modems are untouched by this campaign
  and by the upgrade that carries it:

  ```json
  { "subChannel": 3, "mode": "freedv-datac1", "rfFrequency": 7054000, "port": 8103,
    "identify": { "callsign": "M0LTE", "intervalMinutes": 10, "wpm": 20 } }
  ```

- The keyed audio is queued through the transmit path the modems already use, so it obeys channel
  access instead of talking over somebody.
- Interval logic on `TimeProvider`, tested with `FakeTimeProvider` the way
  `IdentificationIntervalTests` already does, so a ten-minute rule costs no wall clock to prove.

**Per-modem is also what makes the placement right.** A conventional 700 Hz ident tone on a
7.049750 dial transmits at 7050.45, which is on top of the node's own afsk300 slot. Hanging the
ident off its modem means it defaults to **that modem's own `rfFrequency`**, so it identifies on
the signal it is identifying, and no separate frequency has to be kept in step with the band
plan by hand.

That the existing modems need no CW is a regulatory observation as much as a preference: they sit
in 7050-7053, the segment designated for automatically controlled data stations, and they already
identify in-band as `GB7RDG>ID` on both. It is the attended excursion above 7053 that owes
listeners something they can read without our software.

## Run procedure

Nothing below runs until Tom authorises the run itself; the access and the approach are agreed,
the transmission is not.

1. **Back up** `/etc/pdn-soundmodem/soundmodem.json` on 10.45.0.37, and record the current
   startup banner from the journal, including the transmit filter figure, so the radio-global
   setting can be put back.
2. **Check the channel is clear.** `sm-iqcapture` centred on 7056 for 60 s, occupancy computed
   across 7052-7060 **and 3 kHz either side** of the intended slot. The margin is not
   decoration: a +/-1.5 kHz check once called a slot clear while an LSB QSO 3.5 kHz up
   overlapped it. Repeat at our own end, because interference is caused at the receiver and the
   two ends do not see the same band.
3. **Bring up the receiver first**, on m9psy at 7054.0, with `survey` and `frameLog` on, and
   confirm it is hearing band noise rather than nothing.
4. **Measure the dial correction** against that receiver before scoring anything with it.
5. **Upgrade the node to 0.35.0 or later**, which is the release that can identify. The host runs
   0.34.2, which will accept the `identify` block's JSON and warn that it is not a setting it
   knows - a station that believes it is identifying and is not. Check the banner says it is.
6. **Deploy** the fourth modem, restart, and read the banner back. Confirm the dial, the
   passband and both filters match the `flex:mock` dry run.
7. **Pass traffic** (`scripts/kiss-send.py`). The first transmission makes an identification due
   at once and every 10 minutes thereafter, on the test frequency; watch for the `id[3]` lines.
8. **Take it down** when the session ends: restore the config and restart. That is sufficient,
   including for the radio-global transmit filter - the daemon *states* the high cut from the band
   plan at every start-up rather than inheriting it, so restoring the three-modem config puts the
   filter back to 2550 Hz as a side effect. (An earlier draft of this plan said the config alone
   would not do it. The run disproved that; verify it off the banner rather than trusting either
   claim.)

### Abort conditions

- Anything audible or visible in the slot that was not there at step 2, at either end.
- The node's decode counts in `frames.db` falling materially against the pre-change hour.
- Any failure to identify.
- SWR outside what the station normally reads. ANT1 is a real antenna measuring 1.53-1.64 across
  7.126-7.147; the harness's 1.50 abort is a dummy-load rule and does not apply here.

## What gets recorded

A dated entry in [mode-validation.md](mode-validation.md) naming the mode, the transition, and
the PR, per the standing rule; the IQ captures with their sidecars; the before-and-after decode
counts for the three production modems; and the measured dial correction. If `freedv-datac1`
decodes at 550 km through a production node sharing a slice with live traffic, that is the row
worth writing.

---

## What happened (2026-08-15, run complete)

Authorised and run on 2026-08-15 between roughly 09:02 and 09:35 UTC. **Both modes crossed a real
path; the node was restored and verified back in service.** The ledger entry in
[mode-validation.md](mode-validation.md) is the durable record; this is what the plan got right
and wrong.

| | Result |
|---|---|
| `freedv-datac3` to Wessex | **6 of 6, 100 %** |
| `freedv-datac1` to Wessex | **11 of 14, 79 %** |
| Decode quality | every frame `crc ok`, `fec 0`, **+2 Hz** |
| Morse ident on air | 09:18:50 and 09:28:56 - **10 min 06 s** against a configured 10 |
| Node afterwards | config byte-identical to the backup, filters back to 2550 Hz, working EI0RSI-1 |

**The receiver was the variable, not the mode.** The plan named m9psy-1 throughout and it was the
wrong choice: nothing decoded there at all, and the survey was *empty* - heard nothing, rather
than heard-and-failed. Rather than guess, an IQ capture taken through the transmissions showed our
bursts arriving at the right instants at only +6 to +10 dB of in-band excess, about +5 dB SNR3k,
which is precisely datac1's fading threshold. A 550 km hop on 40 m at 09:10 UTC in August simply
did not have the margin. Tom suggested `wessex.zapto.org` (~120 km, solid NVIS) and the same
waveform at the same power decoded 100 %. **For any repeat: pick the receiver by path length
first.** The `--dial-correction` trap never appeared - GPSDO at both ends held +2 Hz - so the
step-4 measurement is better spent choosing a receiver that can hear you.

**Placement moved to 7053.600**, not the planned 7054.000. The pre-transmission check found a
narrow persistent carrier at 7055.15-7055.25 kHz, 300 Hz off datac1's upper edge at 7054.0;
dropping 400 Hz bought a 700 Hz guard at the cost of the emission straddling 7053 by 250 Hz, which
is immaterial for an attended test since both segments carry digimodes at 2700 Hz. Passband
300-4831 Hz, transmit filter 4950 Hz, slice receive 150-4950 Hz - a little better than the 5350 Hz
the 7054.0 placement modelled.

**The costs landed as measured.** Dial 7.049450 to 7.049750 exactly as the `flex:mock` dry run
said; the mock and the production banner agreed line for line, which is the reason to keep doing
the dry run. Two restarts were budgeted and four were spent: one to deploy, one for a `sed` that
left a trailing space in `"freedv-datac3 "` and stopped the service on exit 2 (the config guard
working as designed, but a self-inflicted outage of about a minute - **edit the config with a JSON
parser, not `sed`**), one to switch to datac1, and one to restore.

**Still open:** nothing blocking. Worth doing next if the campaign continues - a longer datac1 run
to put a real number on 79 %, and the same test at a time of day with less D-layer absorption.
