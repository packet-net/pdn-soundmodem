# NinoTNC mode capture corpus — 2026-07-31

Real NinoTNC transmit-audio captures of every DIP mode, recorded on the studybox bench (NinoTNC N9600A4, firmware v3.44 per the 2026-07-15 flash; DIPs at 1111 = software mode select; CM108 widget, TNC TXA → CM108 IN). Captured by pdn-soundmodem's Claude session; the capture driver is `~/nino_capture.py` beside this file.

## Recording conditions

- TNC TXA range: **MIC (0–200 mV)**; CM108 **Auto Gain Control OFF**, Mic capture 32 (+8 dB) → peak ≈ 0.17–0.28 full-scale (deterministic, no AGC opinion in the levels).
- 48 kHz, 16-bit signed LE, mono.
- KISS settings per capture: **PERSIST 255, SLOTTIME 0**, TXDELAY per the file name.
- Mode set via KISS SetHardware (payload mode+16) and **verified via GETALL** (`BrdSwchMod`) before every capture; two sacrificial frames flush the TNC's stale-parameter behaviour (see Gotchas) before recording starts.

## File naming

`{DIP}-{baud}{modulation}-{framing}-txd{N}ms.wav`, e.g. `1011-2400qpsk-il2pc-txd200ms.wav`. Three TXDELAY tiers per mode — aggressive / standard / conservative — scaled to the symbol rate (acquisition budget ∝ symbol time):

| Baud | aggressive | standard | conservative |
|---|---|---|---|
| 300 | 240 ms | 500 | 1000 |
| 600 | 160 | 300 | 600 |
| 1200 | 120 | 240 | 500 |
| 2400 | 100 | 200 | 400 |
| 3600 | 80 | 160 | 300 |
| 4800 | 60 | 150 | 300 |
| 9600 | 50 | 120 | 250 |
| 19200 | 40 | 100 | 200 |

## Contents per file

Three AX.25 UI frames (QST < M0LTE-1, PID F0), short/medium/long info fields (~70 / ~130 / ~180–255 B; exact sizes in `MANIFEST.tsv` — each frame's text self-describes the file's parameters). For IL2P modes the TNC performs the IL2P encoding itself (KISS in = AX.25).

## Decode provenance (QC)

Every file was energy-segmented (all 3 bursts physically present) and decoded through pdn-soundmodem's paired catalog mode (`NinoCorpusQcTests`, report `nino-qc-final.txt`): **45/45 decode 3/3** (final: after the capture-glitch remediation below, the 2026-07-31 QPSK detector default reversal — differential detection copies all nine QPSK files; under the earlier coherent default `qpsk600` copied 0–1/3 and `qpsk3600` 1–2/3 — and the c4fsk equalizer below).

**The c4fsk "acquisition band" was a misdiagnosis.** The last two failures (`0011` txd50 0/3, txd120 2/3) were first read as a preamble-length floor (50 ms below it, 120 marginal, 250 reliable). Instrumented tracing against the known transmitted wire bytes disproved that: preamble trimmed to 20 ms of the txd250 capture still decoded, grafting 150 ms of known-good preamble onto the txd50 bursts did not help, and every symbol error in a calibrated replica of the demodulator was an **outer→inner demotion at 0.53–0.67 normalised** — outer symbols squeezed under the ⅔ slicing boundary by pattern-dependent ISI. The three tiers transmit different payload text (the tier tag is embedded in each frame), and the txd50/txd120 text simply contains more of the borderline symbol patterns; the recordings themselves are indistinguishable in level, spectrum and DC. Fix: a 5-tap symbol-spaced decision-directed NLMS equalizer at the decision instants of `C4fskModem` (per-burst trained, silence-gated, identity start) — nine bursts drop from 103 symbol errors to 25, all within RS budget, and the corpus went 43/45 → 45/45. TXDELAY was a confounder, not a cause; there is no 250 ms floor.

**Capture-chain glitch finding (2026-07-31 QC).** The studybox capture chain injects isolated single-sample discontinuities (~1 per 2.5–9 s of recording; jumps 3.6–7× the band-limited maximum; source unidentified — not ALSA buffering, not USB autosuspend, no audio daemon present). These were sample-localised inside exactly the bursts that failed decode (`bpsk1200`@500 ×2, `afsk1200-il2p`, `afsk300`@240) and recaptures cured every one. The capture driver is now self-healing: a post-capture band-limit glitch scan retries up to 3× (narrowband modes; wideband FSK slews faster naturally and never exhibited decode-failing glitches).

## TNC behaviours discovered during capture (N9600A4 v3.44)

1. **The first frame after a TXDELAY change transmits with the previous TXDELAY** (measured via burst-preamble durations across tier files). Flush with a sacrificial frame.
2. **SETHW mode select is fire-and-forget and can silently not apply** — verify via GETALL (`BrdSwchMod` low byte → mode, mapping in the driver) with retries.
3. **In mode 1100 (300 Bd AFSK AX.25) the TNC never transmitted a ~253 B-info AX.25 frame** (short/medium aired, the long burst simply absent; the 300 Bd IL2P modes transmit comparable-duration frames fine). The corpus's 1100 long frame is ~183 B as a workaround; the mechanism is unexplained — possibly a mode-specific length/time limit worth a look.

## Acquisition floors behind real NinoTNC preambles (2026-08-01)

Measured by surgically trimming each mode's conservative-tier capture down a keep-ladder (40/20/10/5/2/0 ms of preamble retained, per burst, sample-precise) and QC-decoding all 90 variants through the shipped receiver. "Floor" = shortest kept preamble decoding 3/3; single capture per mode, 3 frames per rung, clean-channel — read as ±1 rung. NinoTNC reference floors from the 2026-07-16 TNC-to-TNC survey (1 × 16-bit word in 13/15 modes; ~10 ms on 9600 GFSK AX.25).

| Mode | Our floor | NinoTNC floor | Verdict |
|---|---|---|---|
| `fsk9600-il2p` (0010) | **0 ms** (sync only) | 1.7 ms | beat |
| `fsk4800-il2p` (0100) | **0 ms** (sync only) | 3.3 ms | beat |
| `qpsk2400` (1011) | **0 ms** (sync only) | 3.3 ms | beat |
| `qpsk3600` (0101) | 2 ms (0 ms: 2/3) | 2.2 ms | match/beat |
| `fsk9600` AX.25 (0000) | 5 ms | ~10 ms | beat |
| `bpsk1200` (1010) | 5 ms | 13.3 ms | beat |
| `c4fsk9600` (0011) | 10 ms | 1.7 ms | behind |
| `qpsk600` (1001) | 10 ms | 13.3 ms | beat |
| `afsk1200-il2p` (0111) | 10 ms | 13.3 ms | beat |
| `c4fsk19200` (0001) | 20 ms | 0.8 ms | behind |
| `bpsk300` (1000) | 20 ms | 53 ms | beat |
| `afsk300` AX.25 (1100) | 40 ms | 53 ms | beat |
| `afsk300-il2p` (1101) | 40 ms | 53 ms | beat |
| `afsk300-il2pc` (1110) | 40 ms | 53 ms | beat |
| `afsk1200` AX.25 (0110) | 40 ms ≈ 70 %/burst (phase lottery); 120 ms reliable | 13.3 ms | behind |

Notes: (1) the sub-floor misses are dominated by the FIRST burst (cold start from silence) — warm re-acquisition is typically a rung or two lower (e.g. `afsk1200` decodes bursts 2–3 at 20 ms kept). For `afsk1200` specifically the 40 ms cell was forensically dissected (2026-08-01, `AfskColdStartProbe`): it is a **DPLL arrival-phase lottery**, not state contamination — success flips pseudo-randomly with 1 ms lead-length changes (7/12 noise-led, 10/12 zero-led), the framer and envelope trackers are measured innocent, and the arithmetic lands on the knife-edge (worst-case phase needs ~5 crossing-nudges ≈ 3 flags ≈ 20–27 ms to settle, plus a clean flag for the deframer ≈ 34 ms of the 40 available). Closing the gap to the NinoTNC's 13.3 ms needs a different acquisition design (flag-pattern matched timing estimation), not tuning — the searching/locked inertia switch is a banked regression (`BitDpll` remarks); (2) the two C4FSK floors are bounded by the energy gate's assert latency (~a block or two at 20 ms/block), the deliberate silence-immunity trade documented in `C4fskModem`; (3) occasional non-monotonic blips (a 0 ms rung decoding where 2 ms did not) are marginal-zone sync-hunt lottery, expected at ±1-bit sync tolerance.
