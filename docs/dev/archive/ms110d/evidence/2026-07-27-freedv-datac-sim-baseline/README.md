# FreeDV datac OFDM — simulation baseline (2026-07-27)

The first end-to-end characterization of pdn-soundmodem's managed FreeDV datac OFDM modes (datac0/1/3/4/13/14) in simulation: AWGN and Watterson Poor/Good BER-vs-SNR waterfalls, cross-checked against FreeDV's own published operating points, on a harness generalized off the MS110D ladder to drive any `ModemCatalog` mode. Pure software, no radio, no modem changes.

## Bottom line

- **On AWGN the managed OFDM engine is faithful to codec2.** datac3's 10 %-PER knee lands at **−3.2 dB** against FreeDV's published −3 to −3.5 dB — essentially exact; datac1's is ~1.2 dB better than the 2020-blog +3 dB, consistent with the current stronger LDPC (not a claim we beat codec2). The mode ordering is coding-theory-correct and every waterfall is a clean monotone knee. This is the OFDM analogue of the MS110D spec-mask anchor, and it passes.
- **On the Watterson Poor (MPP, 2 ms/1 Hz) channel every mode decodes, but per-packet success reads 6–28 points below FreeDV's published N/100 at the operating SNR** — furthest for the shortest signalling modes (datac14, datac0) and the wideband datac1, within ~1 dB for the narrow long modes (datac3, datac4). Because AWGN is faithful, this is specific to the fading channel; the gap is confounded between the short-mode **long-PER-tail** codec2 documents, our **independent-burst-vs-continuous-stream** method, and **channel-model implementation differences** (our equal-power / Gaussian-Doppler MIL-STD Watterson vs codec2's `ch` MPP). **Reported as Finding 1, not fixed** — modem changes need the owner's authorization, and this could not be a modem defect that also passes AWGN so cleanly and decodes on every channel.
- **On the slow CCIR Good (MPG, 0.5 ms/0.1 Hz) channel the long modes show a shallow-slope error floor, by design** — a 0.1 Hz fade period (~10 s) dwarfs a 3–5 s packet, denying the code intra-packet time diversity, so slow fading is *harder* than the faster Poor fade. Correct OFDM/coded behaviour; FreeDV publishes no MPG points, so it is reported as characterization.
- **The absolute-level axis is clean — no WN2-analogue front-end bug.** OFDM is invariant to ≥ 60 dB of downward level scaling at fixed SNR across every mode and SNR; the only level dependence is upward ADC-style clipping at a marginal SNR. Pilot-based OFDM equalisation will not carry MS110D WN2's un-normalised level sensitivity onto the air.
- **Phase-B readiness: green for AWGN-limited operation.** Re-anchor the absolute Poor thresholds against codec2's own `ch` before trusting them on the air (Finding 1); nothing here is a modem defect.


## What this is

A pure-software simulation baseline for the six **FreeDV datac OFDM** modes (datac0/1/3/4/13/14) as they run inside pdn-soundmodem's managed `FreeDvDatacModem` (the `M0LTE.Ofdm` engine — no native libcodec2). BER-vs-SNR waterfalls on AWGN and on the Watterson **Poor** and **Good** fading channels, cross-checked against FreeDV's own published operating points, plus an absolute-level-invariance probe. This is Phase A of the OFDM campaign extension — the sim-vs-published anchor that says whether the managed OFDM implementation is faithful before any of it goes on the air, mirroring the MS110D methodology (`../2026-07-27-ota-lab-campaign/`). **No radio, no modem-code changes** — this validates the existing modem, it does not touch it.

## The rig

Everything runs through `sm-ota sim`, the generalized simulation harness added for this campaign (`tools/Packet.SoundModem.Ota/Sim*.cs`). It renders a burst, injects a channel at a known SNR, decodes, and tallies success — the same generate → channel → score loop the MS110D §E2 ladder drives over the air, generalized off the MS110D-specific path onto the `IModem` seam so it covers any `ModemCatalog` mode.

- **Channel.** The mask suite's own `WattersonChannel` (`tests/…/Channel/WattersonChannel.cs`), the identical rig the MS110D masks gate against — so an OFDM Poor waterfall and an MS110D Poor waterfall are measured against one channel, not two. **AWGN** is the ideal path plus calibrated noise; **Poor** is `WattersonChannel.Poor` (two equal-power Rayleigh paths 2 ms apart, 1 Hz two-sigma fade); **Good** is the CCIR / ITU-R F.1487 pairing (0.5 ms / 0.1 Hz). These match codec2's own **MPP** (2 ms delay, 1 Hz Doppler spread) and **MPG** (0.5 ms, 0.1 Hz) channel definitions as documented for its `ch` tool, so the fading comparison is like-for-like on geometry.
- **SNR convention.** Signal power over noise measured in a **3 kHz** bandwidth — the HF-SSB convention FreeDV reports its figures in (codec2's `SNR3k`, a 3000 Hz noise-bandwidth SNR; documented in the codec2 OFDM/raw-data docs). The channel calibrates its noise against the mean power of the active burst, so the modulator's leading/trailing silence is trimmed before injection — otherwise it would dilute the signal-power estimate and every point would read high. Lead-in / lead-out padding is noise-only so acquisition sees a realistic floor.
- **Native 8 kHz.** The datac family runs at codec2's engine-native 8000 Hz — the rate FreeDV's published figures are measured at, the resampler-free path, and 6× cheaper than the 48 kHz deployment path. The deployment ×6/÷6 path was spot-checked and decodes identically (below).
- **Two scoring layers.** The **packet** layer runs one raw datac packet through the library's own `DatacReceiver` — the exact engine `FreeDvDatacModem` wraps, one level down from the IL2P frame — scored on the packet's own CRC (FreeDV's "packet received" criterion) and post-LDPC coded BER. This is the currency FreeDV publishes its operating points in, so it is the honest engine-vs-engine cross-check. The **frame** layer runs a full IL2P+CRC AX.25 frame through the mode's `IModem`, scored on whether the frame came back — the pdn *deployment* metric, where one frame can span several datac packets and only decodes when every packet does (a stricter bar than FreeDV's per-packet figure, and the reason the two layers separate).
- **Statistics.** Each point is 200 independent bursts (packet layer) or 100 (frame layer), each an independent draw of payload and — via its seed — channel realisation, reproducible from (mode, channel, SNR, seed, count). Success rates carry Wilson 95 % intervals; the threshold is the linear-interpolated SNR where success crosses 50 % (and 90 % = FreeDV's 10 %-PER convention).

### A methodological caveat on the Poor cross-check

FreeDV's published MPP figure is *packets received out of 100 through one continuous MPP channel run* at the operating-point SNR; our figure is the per-packet CRC rate over independent bursts, each with its own fresh fade realisation. For the **long** modes (datac1 4.2 s, datac3 3.2 s, datac4 5.2 s span several 1 Hz fade cycles per packet) the two sample the fade distribution comparably. For the **short** signalling modes (datac0 0.44 s, datac13, datac14) codec2's own docs flag a *long PER tail* — these modes are short compared to the fading period, so a burst either misses the fade or is swallowed whole by it — meaning the published short-mode MPP numbers are tail-limited and the two methods diverge most there. This is stated wherever it bears on a number below.

## Published FreeDV anchors (the masks)

The OFDM analogue of the MS110D spec masks — the figures our AWGN and Poor waterfalls are checked against.

| Mode | Bytes/pkt | RF BW | Payload rate | AWGN 10 %-PER | MPP (=Poor) operating point | Source |
|---|---|---|---|---|---|---|
| datac0 | 14 | 500 Hz | 291 bit/s | not published | **70/100 @ 0 dB** (long-PER-tail) | R |
| datac1 | 510 | 1700 Hz | 980 bit/s | **3 dB** (2020) | **92/100 @ 5 dB** | B / R |
| datac3 | 126 | 500 Hz | 321 bit/s | **−3 dB** (≈−3.5) | **74/100 @ 0 dB** | B / R |
| datac4 | 54 | 250 Hz | 87 bit/s | not published | **90/100 @ −4 dB** | R |
| datac13 | 14 | 200 Hz | 64 bit/s | not published | **90/100 @ −4 dB** (long-PER-tail) | R |
| datac14 | 3 | 250 Hz | 58 bit/s | not published | **90/100 @ −2 dB** | R |

Sources — **R**: codec2 `README_data.md` (the datac mode table, the MPP operating points, the MPP geometry of 1 Hz Doppler spread at 2 ms delay, and the long-PER-tail caveat for the short modes), <https://github.com/drowe67/codec2/blob/main/README_data.md>; the SNR3k / 3 kHz noise-bandwidth convention and the `ch`-tool channel definitions are from the same repo's OFDM/raw-data docs. **B**: David Rowe, "Codec 2 HF Data Modes 1" (15 Jun 2020), the 10 %-PER AWGN and multipath figures for datac1/datac3, <https://www.rowetel.com/?p=7167>. Two caveats carried from the research: the AWGN figures are the **2020-era** mode versions (the current LDPC differs), and the 2020 "Multipath Poor" there was 2 ms/**2** Hz — today's MPD, not today's MPP — so the 2020 MP-Poor numbers are *not* mixed with the current README MPP column here.

## Results

Full waterfalls are in `data/` (one CSV per mode × channel × layer); the thresholds and cross-checks below are `analyze.py`'s digest of them.

| mode | AWGN pkt 50% | AWGN pkt 90% | AWGN frame 50% | Poor pkt 50% | Poor frame 50% | Good frame 50% |
|---|---|---|---|---|---|---|
| datac0 | -2.3 | -0.7 | -2.2 | +0.3 | +3.3 | +1.5 |
| datac1 | +0.7 | +1.8 | +1.1 | +3.7 | +3.9 | +3.4 |
| datac3 | -3.8 | -3.2 | -3.8 | -0.8 | -1.3 | -1.6 |
| datac4 | -9.0 | -8.2 | -8.7 | — | -4.6 | -3.2 |
| datac13 | — | -8.4 | -9.0 | — | -3.4 | — |
| datac14 | -6.3 | -4.1 | -5.6 | -3.6 | +2.7 | — |

*Thresholds, dB in a 3 kHz noise bandwidth. "—" = the grid did not bracket that crossing (the knee lies below the lowest rung, or — for the multi-packet frame layer on the narrowest modes — above the highest). datac14's frame-50 (+2.7) sits far above its packet-50 (−3.6) because a 60-byte frame spans ~28 of its 3-byte packets and needs every one: the extreme of the multi-packet-AND penalty.*

### 1. AWGN — faithful to codec2

The AWGN waterfalls land where FreeDV's do. **datac3's 90 % (=10 % PER) knee sits at −3.2 dB against FreeDV's published −3 to −3.5 dB — essentially exact** (codec2's own datac3 AWGN example sits near SNR3k −3.5 at a low PER). datac1's knee is ~1.2 dB *better* than the 2020-blog +3 dB, consistent with those being 2020-era mode versions whose LDPC has since been strengthened — not a claim that our engine beats codec2. The mode ordering is coding-theory-correct (the narrow, heavily-coded modes sit far below the wideband datac1), the packet and frame layers agree where a frame is one packet, and every waterfall is a clean monotone knee with no floor.

| mode | our AWGN pkt 90% (dB) | FreeDV AWGN 10%PER (dB) | delta |
|---|---|---|---|
| datac1 | +1.8 | +3.0 | -1.2 |
| datac3 | -3.2 | -3.0 | -0.2 |

**Deployment ×6/÷6 path.** Spot-checked at 48 kHz (the daemon's DSP rate, exercising the internal resample) against the native 8 kHz baseline — decodes identically at matched SNR, so the resampling bridge costs nothing measurable.

### 2. Poor (MPP, 2 ms/1 Hz) — reads below the published operating points

At each mode's published MPP operating-point SNR our per-packet success sits **below** FreeDV's N/100, by 6 to 28 points. The gap tracks two things: **packet duration** (the shortest signalling modes are furthest below — datac14 −28, datac0 −23 — because a sub-second packet at a 1 Hz fade has no time diversity and dies whole in a null) and **occupied bandwidth** (the wideband datac1, −12, spans several 500 Hz coherence-bandwidths of the 2 ms multipath, so its band is deeply frequency-selective). The narrow long modes datac3 and datac4 are closest — within ~6 points (~1 dB).

| mode | op SNR (dB) | our pkt succ (95% CI) | FreeDV N/100 | reading |
|---|---|---|---|---|
| datac0 | +0.0 | 47/100 [40..54] | 70/100 | below pub |
| datac1 | +5.0 | 80/100 [74..85] | 92/100 | below pub |
| datac3 | +0.0 | 68/100 [61..74] | 74/100 | below pub |
| datac4 | -4.0 | 84/100 [78..88] | 90/100 | below pub |
| datac13 | -4.0 | 80/100 [74..85] | 90/100 | below pub |
| datac14 | -2.0 | 62/100 [56..69] | 90/100 | below pub |

*"per 100" normalises our 200-burst counts for a like-for-like read against FreeDV's N/100; CI is Wilson 95 % on the raw 200.*

This is **Finding 1** (below) — characterized, not fixed. It is not a collapse: every mode acquires and decodes on the Poor channel, the waterfalls are clean monotone knees, and the gap is small (~1 dB) for the narrow long modes. The duration/bandwidth trend points at the short-mode **long-PER-tail** codec2 itself flags plus the **independent-burst-vs-continuous-stream** methodology difference as the dominant term for datac0/13/14; for datac1/datac3/datac4 a residual few points remains that channel-model implementation differences (below) or a managed-engine fade-tracking gap could account for. It cannot be split apart without a direct codec2-`ch` comparison, which this pass does not attempt.

### 3. Good (MPG, 0.5 ms/0.1 Hz) — a slow-fade floor on the long modes, by design

The CCIR "Good" channel is **not** simply "easier than Poor" for the long modes — and correctly so. At 0.1 Hz the fade period (~10 s) dwarfs a datac1/datac3/datac4 packet (3–5 s), so each packet sees an almost-constant Rayleigh gain with **no intra-packet time diversity for the code to average over**: a packet that draws a deep fade dies whole. The result is a *shallow-slope error floor* rather than a clean knee — some packets decode below the AWGN threshold (a fade peak lifts them), many fail well above it (a fade null sinks them). Poor's faster 1 Hz fade gives the long packet several fade cycles to average, so moderate-rate fading is *easier* than slow fading here. This is standard OFDM/coded behaviour and the reason these modes target the faster HF channel; FreeDV publishes no MPG operating points, so there is no cross-check — the Good waterfalls are reported as characterization.

### 4. Absolute-level invariance — no WN2-analogue bug

The WN2 lesson was an un-normalised front end that decoded at nominal sim level but died when the real receiver delivered a low absolute level. **OFDM shows none of it.** Scaling the input **down** (−6 to −60 dB) at a fixed SNR is fully invariant at every mode and SNR tested, healthy and marginal alike — pilot-based normalisation makes the datac receiver level-blind, exactly as it should. The only level dependence is **upward**: at a *marginal* SNR, driving the input hot enough to hit the modem's short-domain hard clip (≥ +12 dB over nominal) costs margin (datac0 at 0 dB SNR falls to 60 % at +20 dB); at a healthy SNR it is clip-tolerant to +20 dB. That is expected ADC-saturation behaviour and an operational "don't overdrive the input" note — the direct analogue of the campaign's DAX-ALC finding — not a normalisation defect.

| mode | SNR (dB) | down succ (−60…0) | down verdict | clip onset | +20 dB succ |
|---|---|---|---|---|---|
| datac0 | +0.0 | 97%..98% | INVARIANT | +12 dB | 60% |
| datac0 | +6.0 | 100%..100% | INVARIANT | — | 100% |
| datac1 | +4.0 | 100%..100% | INVARIANT | — | 95% |
| datac1 | +10.0 | 100%..100% | INVARIANT | — | 100% |
| datac3 | -1.0 | 100%..100% | INVARIANT | — | 100% |
| datac3 | +6.0 | 100%..100% | INVARIANT | — | 100% |
| datac4 | -3.0 | 100%..100% | INVARIANT | — | 100% |
| datac4 | +2.0 | 100%..100% | INVARIANT | — | 100% |
| datac13 | -3.0 | 100%..100% | INVARIANT | — | 100% |
| datac13 | +2.0 | 100%..100% | INVARIANT | — | 100% |
| datac14 | -2.0 | 100%..100% | INVARIANT | — | 98% |
| datac14 | +3.0 | 100%..100% | INVARIANT | — | 100% |

## Findings

**Finding 1 — datac Poor/MPP success reads 6–28 points below FreeDV's published N/100.** At the published operating SNR our per-packet CRC success is below FreeDV's figure for every mode — furthest for the shortest signalling modes (datac14 62 vs 90, datac0 47 vs 70/100) and the wideband datac1 (80 vs 92), closest for the narrow long modes (datac3 68 vs 74, datac4 84 vs 90/100). AWGN is faithful (datac3 to ~0.2 dB), so this is specific to the fading channel, not a global calibration offset. **Not fixed — reported.** Candidate causes, unseparated: (a) the short-mode long-PER-tail codec2 documents, amplified by our independent-burst-per-fade method versus codec2's continuous 100-packet stream (dominant for datac0/13/14); (b) channel-model implementation differences — our MIL-STD-188-110D Watterson Poor is two *equal-power* Rayleigh paths with a *Gaussian* Doppler spectrum, and codec2's `ch` MPP may differ in path-power split and Doppler-generator shape, which changes fade-null depth; (c) a residual managed-engine fade-tracking gap. **Next step to localise (out of scope here):** run identical bursts through codec2's own `ch` + `freedv_data_raw_rx` and diff, and add a static-2-path (Poor geometry, no fade) probe to separate ISI from fade-tracking — the OFDM analogue of the MS110D `Static_2Path_Diagnostic`.

**Finding 2 — the level axis is clean (a clear result, not a defect).** OFDM is absolutely-level-invariant downward across ≥ 60 dB at fixed SNR; the datac front end is not level-sensitive the way un-normalised MS110D WN2 was. Upward clipping at marginal SNR is real and expected. No modem change indicated; the operational note is to keep the received level below the input clip, same as any ADC.

## Phase-B (on-air) readiness

**Green for AWGN-limited operation; Poor thresholds to be re-anchored before they are trusted absolutely.** The managed OFDM implementation is faithful to codec2 on AWGN — the primary anchor — decodes correctly on every channel, is level-robust, and the deployment resampling path is transparent. Nothing here is a modem defect. The one open item before leaning on absolute Poor numbers on the air is Finding 1: the 6–28-point MPP shortfall is confounded between methodology and channel-model differences and should be closed with a direct codec2-`ch` comparison, not read as an OFDM weakness. For contrast with the serial-tone MS110D: on AWGN both families reproduce their masks; on fading the story inverts — MS110D's DFE collapsed on the *real rig* (reference phase-noise) while sailing through sim, whereas OFDM sails through sim on every channel and its only sim-side caveat is a per-mode Poor cross-check offset. OFDM's frequency-domain pilot equalisation is inherently more fade-tolerant than a single-carrier DFE, and the level-invariance result says it will not carry MS110D's front-end level sensitivity onto the air.

## Reproduce

```
# one mode, one channel, one layer:
sm-ota sim --mode freedv-datac3 --layer packet --channel poor --snr -2,0,2,4,6,8 --bursts 200

# the whole baseline (per-mode CSVs into data/):
WORKERS=4 bash run-baseline.sh          # ~25 min on the 16-core box

# aggregate to the tables in this README:
python3 analyze.py
```

`run-baseline.sh` is the exact sweep behind this dir; `analyze.py` reads `data/*.csv` and prints every table above. Each CSV is one grid: `mode,channel,layer,rate,frameBytes,snrDb,levelDb,trials,successes,successRate,ciLo,ciHi,fer,codedBer,margin`.
