# Levels

When you finish this page your RX gain will sit in the target zone on the station page's level meter, and you will have measured your transmit audio with a test tone through your own radio.

## Before you start

- A station that is decoding frames, from [02-first-station.md](02-first-station.md).
- A sound card of its own. The mixer group and the level meter appear only on a station with a sound card, so a FlexRadio or a web receiver station has neither; see [03-radios-and-interfaces.md](03-radios-and-interfaces.md).
- The station page reachable in a browser, with the mixer group switched on. That needs either an `api.key` or `"waterfall": { "enableAudioControls": true }` in the config file, which is `/etc/pdn-soundmodem/soundmodem.json`. See [`api`](reference/config.md#api) and [`waterfall`](reference/config.md#waterfall).
- For the transmit half, a `ptt` block so the modem can key the radio, and something to watch the transmitter with: a monitor receiver, a panadapter, or a deviation meter.

## Why level matters

Receive level matters at the top and hardly at all at the bottom. Every demodulator here is tolerant of a quiet signal over tens of dB. Clipping is the failure that costs decodes: once the sound card runs out of codes, the samples it hands the modem are the wrong shape and no amount of gain elsewhere puts them back. The four-level C4FSK modes lose a decibel of link margin the moment clipping starts and stop decoding at all 9 dB past it. So set the gain for headroom; a bar that fills the scale is a bar that will clip on the next station.

Transmit level matters for a different reason. Too much audio into an SSB radio drives the ALC, which flattens the waveform and sprays intermodulation products across the band. Too much audio into an FM radio overdeviates, which splatters into the adjacent channel and reads as distortion at the far end. Both look like a working station on your own screen and like a poor one at everybody else's.

## Open the mixer group

Open the station page and look in the header for the group labelled **Mixer**. It carries an **RX gain** slider, the level meter, a **CLIP** indicator and a **TX gain** slider. With `api.key` set and no `enableAudioControls`, both sliders are disabled and read `key needed` until you press **Key** and paste the key, which is kept in that browser only.

If the group is not there, the modem did not answer `/api/mixer` with a card. Check that the station has a sound card and that one of `api.key` or `enableAudioControls` is set.

## Set the RX gain

The meter shows the peak of the last 200 ms of audio arriving from the card, in dBFS, on a scale from -60 to 0. A hairline on it marks the RMS. It updates five times a second, whether or not the page's Listen button is playing audio. The sentence under it says what to aim for:

```
Aim for received signals to peak in the green, -18 to -9 dBFS; never into the red, and with nothing on the channel the bar should sit below -30.
```

Move the RX gain slider until a received signal peaks in the green band. The slider is bounded by the card's own dB range, so there is no way to ask for a level it has not got. The change reaches the card at once with no restart.

Three things to watch as you go.

- The **CLIP** indicator lights for three seconds whenever a sample the card delivered sat on the top or bottom code of its range. Anything that lights it is too loud; turn the gain down until it stops during the loudest station you hear.
- Red starts at -6 dBFS. Turn the gain down if a signal reaches it.
- Below -30 dBFS the bar goes grey. With nothing on the channel it should sit there.

You should see the bar land in the green on a burst and fall to grey between bursts, with the CLIP indicator dark.

To pin a level so it is set at every start-up, write it in the config file instead:

```json
{ "alsa": { "mixer": { "captureGainDb": 6, "playbackDb": -8 } } }
```

Both are dB, both must be inside the card's range, and a level outside it is refused with one journal line naming the range while the station carries on. `pdn-soundmodem --mixer-show DEVICE` prints the card's controls and ranges without disturbing a running station. See [`alsa`](reference/config.md#alsa) and [`--mixer-show`](reference/command-line.md#one-shot-flags).

## Read the frame badges

The meter is the instrument for setting a gain. A single frame is judged separately, because a fast frame is over inside one of the meter's intervals. Each row in the decoded frames panel carries the peak measured over that frame's own burst, and a badge where the level has begun to cost that mode something.

A `TOO LOUD` badge means the card ran out of codes during the frame, or the frame's audio reached the mode's loud edge. Turn the RX gain down. If it happens on one station only, that station's transmit audio is hot and yours is fine.

A `TOO QUIET` badge means the frame was below the level its own mode measurably starts losing margin at. Turn the RX gain up, watching the meter, and check the aerial and the radio's own audio output before chasing the last decibel.

The dBFS figure appears on a row only on the modes whose slicer reads the level, which are the two C4FSK modes and the six 1200 baud AFSK modes. On every other mode the level does not affect the decode, so no figure is shown and only `TOO LOUD` can appear. The measurement is still written to the frame log and sent over the uplink on every mode.

Most rows earn no badge at all. That is what a healthy RX gain looks like. The measured thresholds behind the badges are in [dev/receive-levels.md](dev/receive-levels.md).

## Send a transmit test

The **TX test** group sits beside the mixer group on an operator's page. It never appears on a public page. Pick a kind, set the seconds, and press **Send**. The modem keys the radio, sends the tones through the same transmit path a frame takes at the station's own transmit level, and unkeys.

```
tx test: two-tone 700+1900 Hz, 5.0 s, peak level 0.80
tx test: done, 5.0 s on air
```

Two tone 700+1900 is the SSB check. The third-order products land either side of the pair, where nothing else is. Turn the TX gain down until the products sit as far below the tones as they will go. If the radio's ALC meter moves at all, you have too much audio.

One tone is the FM check, by Bessel null. Send a single tone and raise the transmit audio while watching the carrier on a spectrum display. At one particular deviation the carrier vanishes, and that deviation is set by the tone you chose. The page offers four presets:

| Tone | Carrier nulls at |
|---|---|
| 500 Hz | 1.2 kHz deviation |
| 999 Hz | 2.4 kHz deviation |
| 1248 Hz | 3.0 kHz deviation |
| 2079 Hz | 5.0 kHz deviation |

Aim for 2.5 to 3.0 kHz of deviation on a 12.5 kHz channel and 5.0 kHz on a 25 kHz one. The **One tone** entry takes any frequency you type between 50 Hz and half the channel's sample rate, 6000 Hz on most modes, and the page tells you the deviation its null would calibrate.

A test runs for 5 seconds unless you say otherwise, and is capped at `txTest.maxSeconds`, which is 30 by default and never above 60. **Stop** ends it early. See [`txTest`](reference/config.md#txtest) and [the station flags](reference/command-line.md#station-flags).

From a bench with no browser, stop the service so it lets go of the card, run the test with the same config file, then start the service again. `--tone 999 5` sends one tone instead, and both need the config file, which is where the `ptt` is.

```sh
sudo systemctl stop pdn-soundmodem
pdn-soundmodem --config /etc/pdn-soundmodem/soundmodem.json --two-tone 5
sudo systemctl start pdn-soundmodem
```

## Set the TX gain

Move the **TX gain** slider while the test is running and watch the effect on your monitor receiver or meter. It sets the card's playback level, which is what drives the radio's mic or data input, and it is bounded by the card's range in the same way as RX gain. Some cards mute at the bottom step; where that is so, the slider stops at the lowest step that is a level and says what is under it.

When the reading is right, either leave it, in which case it is remembered in the state file, or write it down as `alsa.mixer.playbackDb`.

## Check it worked

Watch the station page for a few minutes of real traffic.

- The meter peaks in the green on bursts and sits grey between them.
- The CLIP indicator stays dark.
- Rows in the decoded frames panel carry no level badge.
- Your transmit test showed no ALC movement on SSB, or nulled the carrier at the deviation you wanted on FM.

The start-up journal reports what the card ended up set to:

```
alsa: mixer: Mic capture 6.00 dB of -12.00 to 23.00 dB (set 6.00 dB, config), Auto Gain Control off (forced), Speaker playback -8.00 dB of -36.00 to 0.00 dB, below which it mutes (set -8.00 dB, state file)
```

## If it did not

`no reading` on the meter means nothing has arrived for a second. The station is transmitting, or its audio has stopped. A reading that never comes back is in [12-troubleshooting.md](12-troubleshooting.md).

A slider that is disabled with a read-back like `12% (no dB scale)` means the card publishes only raw steps. The journal says so by name.

`tx test: unavailable - no "ptt" is configured, so this daemon does not key the radio` beside a greyed-out TX test means the station cannot key. Add a `ptt` block; see [03-radios-and-interfaces.md](03-radios-and-interfaces.md).

`tx test: refused, the channel did not clear within 60 s, so the test was withdrawn and nothing was transmitted` means the test waited for a clear channel, as any transmission does, and gave up. A PTT line that does not key at all is in [12-troubleshooting.md](12-troubleshooting.md).

## How it works

AGC and mic boost are switched off at every start-up on any card that has them, whether or not the config file has an `alsa` section. Automatic gain fights the modem's own level tracking, so neither is a setting and there is no button for either. The journal names each one and says whether the card took it.

A level changed on the page or over `/api/mixer` is written to `mixer-state.json` in the state directory, which is `/var/lib/pdn-soundmodem/mixer-state.json` under the shipped unit. The config file is never written by a mixer change.

At the next start-up, a level pinned in `alsa.mixer` wins. The state file fills in only for a control the config file says nothing about. Where neither says anything, the card keeps whatever it had. See [files.md](reference/files.md#what-the-modem-writes-and-when).

## Related

- [02-first-station.md](02-first-station.md), getting the first decode this page tunes up.
- [03-radios-and-interfaces.md](03-radios-and-interfaces.md), the audio path and the PTT line.
- [05-modes.md](05-modes.md), which modes are in each of the three level groups.
- [07-station-page.md](07-station-page.md), every other control on the page.
- [12-troubleshooting.md](12-troubleshooting.md), when the audio or the keying is wrong rather than mis-set.
- [reference/config.md](reference/config.md), every key in `alsa` and `txTest`.
- [dev/receive-levels.md](dev/receive-levels.md), the measurements the thresholds came from.
