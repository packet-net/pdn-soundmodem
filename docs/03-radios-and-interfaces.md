# Radios and interfaces

By the end of this page you will know which `device` string and which `ptt` block your hardware needs.

## Before you start

- pdn-soundmodem installed, from [01-install.md](01-install.md).
- A radio, and whatever carries audio between it and the machine.
- Root on the machine, if you are going to key through a CM108 GPIO pin.

## Pick the audio path

`device` says where receive audio comes from and where transmit audio goes. One string covers both directions.

| What you have | `device` |
|---|---|
| A USB sound card, or a CM108-class interface | `plughw:CARD=Device,DEV=0` |
| A FlexRadio on the LAN | `flex:10.45.0.76` |
| A public UberSDR web receiver | `ubersdr:m9psy-1.instance.ubersdr.org` |
| Two FIFOs standing in for a card | `pipe:/tmp/in,/tmp/out` |
| Nothing at all | `null` |

`pipe:` lets two modems on one machine talk to each other with no radio between them, and `null` opens ALSA's null device, which neither hears nor transmits. Both are bench tools. Every accepted form is listed under [`device`](reference/config.md#device).

## Wire a sound card or an interface

Write the stable `plughw:CARD=Device,DEV=0` name rather than `plughw:1,0`, because card numbers move when you replug something; [02-first-station.md](02-first-station.md#find-the-sound-card) finds yours.

A CM108-class interface is a USB sound card with a GPIO pin wired to the radio's PTT line, so one USB lead carries audio both ways and the keying. A DRA board, an RB-USB RIM and the board wired to a Tait TM8100 are all this kind of thing. The card is the `device` string; the pin is the `ptt` block below.

At start-up the journal says what was opened and at what rate:

```
audio: plughw:CARD=Device,DEV=0 capture 48000 Hz -> 12000 Hz
```

Wiring an interface to a radio is a page of its own, one per radio: [hardware/tait-tm8100-cm108.md](hardware/tait-tm8100-cm108.md) for a Tait TM8100 or TM8200 on FM, and [hardware/yaesu-ft450d-cm108.md](hardware/yaesu-ft450d-cm108.md) for a Yaesu FT-450D's rear-panel DATA jack on HF. Setting the card's gains comes next, in [04-levels.md](04-levels.md).

## Choose how the radio is keyed

### A serial RTS or DTR line

```json
{ "ptt": { "type": "serial", "device": "/dev/ttyUSB0", "line": "rts" } }
```

`line` is `rts` or `dtr`, and `rts` is the default. Use a `/dev/serial/by-id/...` name if you have one, since `ttyUSB` numbers move about. The service user is already in the `dialout` group, so nothing else has to be opened up.

```
ptt: serial /dev/ttyUSB0 (rts)
```

### The GPIO pin on a CM108 interface

```json
{ "ptt": { "type": "cm108", "device": "/dev/hidraw0", "gpio": 3 } }
```

GPIO 3 is what interfaces of this class use, and it is the default, so you can leave `gpio` out. To find which node is yours, search for the C-Media vendor ID `0d8c`:

```sh
grep -il 0d8c /sys/class/hidraw/*/device/uevent
```

The `hidrawN` in the path it prints is your node, so `/sys/class/hidraw/hidraw0/device/uevent` means `device` is `/dev/hidraw0`.

`/dev/hidraw*` is root-only, so the unprivileged service user cannot open it until you add a udev rule. [02-first-station.md](02-first-station.md) has the rule. Add it, replug the interface, then restart the service.

```
ptt: cm108 /dev/hidraw0 (gpio 3)
```

### VOX

Leave the `ptt` section out and the modem keys nothing. The radio's own VOX trips on the transmit audio instead.

It is a poor way to run data. The radio decides when the carrier comes up, so TXDELAY, the pause between keying and the first data, has to be long enough to cover the VOX attack, and the hang time keeps the transmitter up while the other station is answering. The transmitter test is refused:

```
tx test: unavailable - no "ptt" is configured, so this daemon does not key the radio
```

## FlexRadio

A FlexRadio needs no sound card and no PTT lead. Audio goes over the LAN on DAX, and the radio keys itself, so a `ptt` section beside a `flex:` device is refused at start-up. A Flex station has no sound card, so an `alsa.mixer` section is refused too.

```json
{ "device": "flex:10.45.0.76", "flex": { "antenna": "ANT1", "mode": "DIGU" } }
```

The string is `flex:<radio>[:slice][@station]`. The radio is `discover` to find one by broadcast, a `host` or `host:port`, `serial=...`, `name=...`, or `mock` for a fake radio on this machine. The slice is a letter `A` to `H`.

Headless mode, with no `@station`, is the usual deployment: no SmartSDR anywhere. The modem creates its own slice and tunes it. `frequency`, `antenna` and `mode` configure that slice; `stationName` is what the radio calls this client. With `rfFrequency` on the modems the band plan tunes the slice instead, see [08-hf.md](08-hf.md).

Attach mode, with a trailing `@station`, binds the slice a running SmartSDR already owns, for coexisting with an operator at the radio. SmartSDR configures the slice, so the keys above do nothing.

Either way the journal names the slice, the DAX channel and which mode you got:

```
audio: flex:10.45.0.76 DAX 24000 Hz -> 12000 Hz (slice A, dax 2, headless 14.100000 MHz ANT1 DIGU)
flex: slice 0 health Healthy at bring-up (owned by this client)
```

### DAX channels

A running SmartSDR takes DAX channel 1, so a headless station defaults to channel 2 and the order the two are started in stops mattering. Attach mode keeps channel 1, SmartSDR's own. `daxChannel` sets it on either path.

Two headless instances that both take the default land on channel 2 and displace each other. Give the second one its own `daxChannel`, and set `"receiveOnly": true` so it never writes the radio's global transmit state and never contends for a slice.

### The transmit filter

The transmit filter high cut is a radio-global setting that survives whoever set it last, and it decides how much of your audio reaches the air. A headless station works it out from the modems you configured and says so:

```
flex: setting the transmit filter high cut to 3150 Hz - modem 0 (afsk1200) reaches 2906 Hz
flex: setting the slice receive filter to 300-3150 Hz, to hear everything the modems are placed across
```

Pin it with `transmitFilterHighHz` (500 to 10000 Hz), or set it to `0` to leave whatever the radio already had. Attach mode sets neither filter, leaving both to SmartSDR.

### On the station page

A Flex station has no sound card, so the page has no mixer group. Transmit level is the radio's business: set `txPowerWatts` in the `flex` section, or leave it and the radio's own setting is used and printed.

```
flex: transmit power 100 W, limit 100 W (radio's own setting)
```

After a transmission the page header shows forward power and SWR, see [07-station-page.md](07-station-page.md).

Every key is in [`flex`](reference/config.md#flex), and the four override flags are under [FlexRadio flags](reference/command-line.md#flexradio-flags).

## A web receiver

With no antenna at all, point `device` at a public UberSDR web receiver and the station receives over the internet. It is receive only: no `ptt`, no transmitter test, and frames sent to it over KISS are dropped. Every modem needs an `rfFrequency`, or `dialFrequency` must be set, so the receiver knows where to tune. [09-web-receivers.md](09-web-receivers.md) covers the rest.

## Check it worked

Start the service and read the first few lines of the journal.

```sh
sudo systemctl restart pdn-soundmodem
journalctl -u pdn-soundmodem -n 30 --no-pager
```

You want one `audio:` line naming your device, and one `ptt:` line if you configured one. VOX and Flex stations have none.

## If it did not

`cannot open the sound device "..."` means the name is wrong, or the card has not enumerated yet. The message itself lists the commands that show what the machine has. The service keeps retrying, so a late USB device clears on its own.

`cannot open the cm108 PTT device "..."` with `Access to the path '/dev/hidraw0' is denied.` under it means the udev rule is missing or has the wrong IDs. Add it, replug, restart.

`--device flex: keys the radio itself; remove the conflicting --ptt (serial:/cm108:)` means what it says. Delete the `ptt` section. The same goes for `ubersdr:`, which has no transmitter.

`cannot reach the FlexRadio "..."` means the radio is off, still booting, or on another network. `ping` the address, or open SmartSDR.

Anything else in the journal is in [12-troubleshooting.md](12-troubleshooting.md).

## Related

- [02-first-station.md](02-first-station.md) for putting this into a working config.
- [04-levels.md](04-levels.md) for receive and transmit levels once the audio path is in place.
- [07-station-page.md](07-station-page.md) for the mixer, the meters and the transmitter test in the browser.
- [08-hf.md](08-hf.md) for SSB, band plans and where the dial goes.
- [hardware/tait-tm8100-cm108.md](hardware/tait-tm8100-cm108.md) for wiring an interface to a TM8100.
- [reference/config.md](reference/config.md) for `device`, `ptt`, `alsa`, `flex` and `ubersdr` key by key.
