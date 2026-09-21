# C4FSK on air: what is known, what is not, and where the traps are

**Status at handover, 2026-09-21.** `c4fsk9600` and `c4fsk19200` decode nothing over the radio1/radio2 bench, in either direction, at every frame size tried. Both pass a virtual-audio loopback byte-identically, so the implementation modulates and demodulates correctly. The transmit level was wrong and is now fixed (#516); that was a real defect and it is **not** why they fail.

This is a handover rather than a report. The point is to save the next person the eight hours it took to get here, and in particular to save them the four wrong turnings, which are written up as prominently as the findings.

## The rig

Two Tait TM8110s at 146.900 MHz, wide (25 kHz) channel, 1 W into 20 dB attenuators into antennas about a metre apart. Audio injected at the **T12** tap, which bypasses the limiter, the 3 kHz low pass and pre-emphasis; receive from the discriminator. CM108 sound cards, `pdn-soundmodem` on a Pi at each end, KISS on 8105, config API on 8107.

An SDRplay RSP1 is on radio1 for RF observation. The SoapySDRPlay3 module was missing and is now built and installed there.

`ofdm-fm-8k` delivers 100 % on this rig, all day. That is the control: when something does not work, the rig is not the first suspect.

## What is established

**The implementation is correct.**

```
scripts/two-station-pipe.py c4fsk9600     -> HEARD on station B, identical
scripts/two-station-pipe.py c4fsk19200    -> HEARD on station B, identical
```

**What the radio actually emits**, measured with the RSP1 on 2026-09-21, before #516:

| mode | peak deviation | published | 99 % OBW | -26 dBc |
|---|---|---|---|---|
| `ofdm-fm-8k` (control) | 4.01 kHz | ~4.8 | 10.7 kHz | 12.2 kHz |
| `c4fsk9600` | 6.92 kHz | 2.5 | 14.2 kHz | 19.5 kHz |
| `c4fsk19200` | 6.73 kHz | 5.0 | 17.1 kHz | 22.5 kHz |

The radio's wide IF is 12.6 kHz at -3 dB (service manual MMA-00005-05 issue 5, Table 3.1). Both were far outside it. #516 fixes the ratio between the two modes; the absolute level is still the operator's.

**Correcting the level does not fix decoding.** At -9 dB on the transmit volume, `c4fsk9600` measured 3.18 kHz peak and 7.8 kHz at -26 dBc, properly formed and comfortably inside the IF. Delivery was still **0 of 30**. A sweep of the transmit volume from 0 to -12 dB in 3 dB steps delivered zero at every step.

**What the receiver gets**, from radio1's own 48 kHz `rawCapture` during a correctly levelled `c4fsk9600` transmission that quieted the receiver by 11.1 dB:

| audio | 1 kHz | 2 kHz | 3 kHz | 4 kHz | 4.8 kHz | 6 kHz |
|---|---|---|---|---|---|---|
| dB below peak | -7.8 | -10.1 | -13.5 | -23.0 | **-28.6** | -39.1 |

`c4fsk9600` runs 4800 sym/s and needs usable energy out to about 4.8 kHz.

## The leading hypothesis

**The receive audio path rolls off too steeply for a 4-level eye.** 29 dB of tilt across the signal's own band collapses the inner and outer levels together. OFDM-FM lives with the same path because it estimates the channel from a preamble and equalises every subcarrier independently, so a 20 dB tilt is routine for it; C4FSK has only a short feed-forward equaliser (`ResetFfe`, the `_ffeTaps` chain in `C4fskModem.cs`) and cannot recover that.

**It is a hypothesis, not a finding.** What would settle it:

- Measure the receive path's response directly, by injecting a swept tone at the far end's T12 and reading the discriminator, rather than inferring the path from a signal that has been through it.
- Try `c4fsk9600` with receive audio taken from a flatter tap than the current one, if one exists on this radio.
- Feed a clean, locally generated `c4fsk9600` waveform through a simulated version of that measured rolloff and see whether the decoder fails in the same way. If it does, the mechanism is confirmed without touching a radio.
- If confirmed, the question becomes whether the equaliser can be made to cope, which is a real piece of DSP work and should be scoped on its own.

## Ruled out, with evidence

- **The modem.** Loopback passes for both modes.
- **The mode not being applied.** Journals show `modem 0: c4fsk9600` and `tx[0] c4fsk9600 M0LTE>M9YYY 80 bytes`.
- **Audio bandwidth being too wide for the path.** This was my first theory and it is wrong: `c4fsk9600` is 4800 sym/s, so about 4.8 kHz of audio, *narrower* than the 8 kHz OFDM preset that works perfectly on the same rig.
- **Transmit level alone.** Swept 0 to -12 dB, zero throughout, with the deviation confirmed correct by measurement at -9 dB.
- **Anything recoverable arriving at all.** `pdn-decode` sweeping all 54 modes over radio1's recording of a C4FSK transmission: nothing decoded, 54 tried, 54 silent. The same tool over a recording of an OFDM transmission recovered 12 distinct frames, so the tool and the recording chain are both sound.

## Traps, all of which cost me time

**An envelope detector cannot find a narrowband signal in a wideband capture.** A 25 kHz signal in a 2 MHz capture is a thousandth of the bandwidth and moves total power by a fraction of a dB. I concluded from this that the RSP1 was dead and said so, and the carrier was sitting there 64 dB over the noise. Use a spectrogram and track power in the signal's own band. `~/ofdm-fm-campaign/narrowband.py` does this.

**Do not tune the dial onto the signal.** A direct-conversion receiver puts its LO leakage at exactly 0 Hz, and a signal tuned dead-on hides underneath it. Tune a few hundred kHz off and mask DC when looking for the carrier.

**An FM receiver goes QUIET when a carrier arrives.** Looking for the received audio to rise finds nothing; the level DROPS. `IChannelBusySource` documents this and I still walked into it.

**`bulk.py --bytes 1900` is silently refused by C4FSK.** IL2P's byte count is 10 bits, so 1023 payload bytes maximum; 1900 plus the AX.25 header is 1916 and the daemon drops it with `tx[0] DROPPED ... exceeds the IL2P maximum of 1023`. Nothing is radiated. Use 1007 or below for any C4FSK cell.

**`rx_sdr` backgrounded over ssh dies when the session closes.** Use `nohup setsid ... </dev/null &` and write to a file rather than redirecting stdout.

**`RFGR=4` is out of range on an RSP1** and fails with `sdrplay_api_Fail` at `activateStream`. Use `RFGR=0` and control gain with `IFGR`.

**`sm-decode` does not know these modes.** It takes `<file.wav> <mode>` positionally and accepts only a small set. Use `pdn-decode`, which sweeps the whole catalogue.

## Tools and artefacts

- `~/ofdm-fm-campaign/narrowband.py` - occupied bandwidth and deviation of a narrowband FM signal in a wide IQ capture, spectrogram-based, masks DC, handles a capture that is keyed throughout.
- `~/ofdm-fm-campaign/2026-09-19/m2-deviation/deviation2.py` - the older deviation analyser. Note it prints four peak figures; "sine from rms" assumes sinusoidal modulation and under-reads a multi-level FSK waveform, and "absolute max" is noise-inflated. The 99.9th percentile is the one to use for a data waveform.
- `~/work/pdn-ofdm-fm/tools/campaign/` - `setmode.py` (one-run mode change over the API, non-persisting), `bulk.py` (delivery and goodput).
- `~/ofdm-fm-campaign/c4fsk-rf/` - the IQ captures and the received-audio recordings behind every figure above.
- `~/ofdm-fm-campaign/c4fsk-20260920T223626Z/` - the first structured run, with its log.

Capture recipe that works:

```sh
ssh pi@radio1 "nohup setsid rx_sdr -d driver=sdrplay -f 147200000 -s 2000000 -F CF32 \
    -g 'AGC=false,IFGR=40,RFGR=0' -n 16000000 /tmp/cap.iq >/tmp/rx.err 2>&1 </dev/null &"
```

146.900 then appears at -300 kHz, clear of the spur.

## Leave the rig as you found it

Both stations should end on `ofdm-fm-8k` with transmit volume at 0 dB. `setmode.py` is non-persisting, so a restart returns a station to `/etc/pdn-soundmodem/soundmodem.json` by itself, but the mixer is not: set it back explicitly.
