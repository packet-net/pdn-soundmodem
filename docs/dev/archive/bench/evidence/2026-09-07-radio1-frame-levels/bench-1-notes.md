# Bench 1 on radio1: main 690486f (tarball sha256 ea94625e...)

As found: service 0.59.0 active, Mic numid=8 value 0 (-12 dB), Speaker 37,37 (0 dB), AGC off. Restored at the end, service active again on 0.59.0.

- FM radio kind: page config sideband=fm, dialHz=145300000, rfShown=false, channel line 145.300 MHz. PASS (probe-1.txt)
- KISS chip: hosts message "KISS 8405, no host" then "KISS 8405: 1 host" with a TCP client attached. PASS (probe-kiss.txt)
- TX test Stop: 999 Hz 15 s tone, stop sent at t+3.0 s, page told "stopped after 2.2 s" 0.212 s after the stop; daemon log "tx test: stopped after 2.2 s". One keying. PASS (txstop.txt, pi-block-4.txt)
- Per-frame level at 11 dB capture gain (meter railing, clip true every interval): seven real GB7RDG>GB7WOD S-frames of len=15 carried NO level (peakDbFs absent) because qpsk3600 needs 17 bytes under the 240-sample margin plus whole-cell rounding; one 57-byte frame carried peakDbFs -3, level loud, TOO LOUD. FAIL on the traffic that matters; fix requested from the #429 author (per-modem margin, sample-exact measurement, catalogue test at 15 bytes). snrDb was null on all of these frames (existing burst SNR path, not #429).
- Not run on this build: the -12 dB and 0 dB probes (block-2, block-3); to be done on the fixed build.

# Bench 2 on radio1: main 3c455aa (PR #431 in; tarball sha256 ae9e9c3f...)

Same throwaway FM config (qpsk3600, ptt cm108, ports 8405/8407), no keying this time. Real GB7RDG traffic, all 15-byte S-frames plus one 79-byte ID and one 57-byte frame.

- 11 dB capture gain: 15-byte frames peakDbFs -3.6 and -4.1, no badge (just under the -3 loud line). probe-2-11db.txt
- 23 dB: four frames (three 15-byte, one 79-byte ID) peak 0 dBFS, clipped true, TOO LOUD. probe-2-23db.txt
- -12 dB: four 15-byte frames peak -26 to -27.3 dBFS, TOO QUIET. probe-2-m12db.txt
- 0 dB: four frames (15, 57, 79, 15 bytes) peak -13.7 to -15.1 dBFS, no badge; meter peak -10.8 / rms -19.8. probe-2-0db.txt
- snrDb null on every frame at every gain (existing burst SNR path; follow-up).
- Card restored to Mic 0 / Speaker 37,37 / AGC off, packaged 0.59.0 active again. pi-block-5-2.txt
