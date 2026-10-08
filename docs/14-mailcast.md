# Receive GB7RDG's 40 m bulletins into your own BBS

Experimentally, GB7RDG sends packet BBS bulletins on 40 m every daylight hour using MS110D (a STANAG-ish mode) on 7052 dial USB. This page sets up your station to receive them and drop them into your own LinBPQ or FBB. You don't connect to GB7RDG, and you transmit nothing: your station only listens.

It is for a station in or near the UK that runs pdn-soundmodem with an HF rig and a LinBPQ or FBB BBS, usually one already on 40 m packet. If that isn't you:

- **No pdn-soundmodem** (QtSoundModem, say): use pdn-mailcast's [standalone receiver](https://github.com/packet-net/pdn-mailcast/blob/main/src/Mailcast.Receiver/README.md).
- **No radio**: the same standalone receiver can listen through a public web SDR.
- **Sending bulletins** rather than receiving them: that is pdn-mailcast's [head end](https://github.com/packet-net/pdn-mailcast/blob/main/docs/headend.md), not this page.

## What you need

- pdn-soundmodem 0.87.1 or later. `pdn-soundmodem --version` says which you have, and [01-install.md](01-install.md#upgrading) shows how to upgrade.
- An HF rig with CAT control and a sound card interface, already working 40 m packet with pdn-soundmodem, for example with LinBPQ on its KISS ports. On a FlexRadio, see [On a FlexRadio](#on-a-flexradio) instead.
- Hamlib's `rigctld` (`sudo apt install libhamlib-utils`), or flrig if it already runs your rig.
- LinBPQ with its mail, or Linux FBB, that this machine can reach.
- A `waterfall` section, if you want the Mailcast panel on the station page.

## What GB7RDG sends

- **Who**: GB7RDG, as AX.25 UI frames to `MCAST`. The transmissions may move to M0LTE, so the receiver takes frames from either; that is what `mailcast.sources` says.
- **When**: every hour on the hour, UTC, in daylight only: from 2 hours after sunrise to 30 minutes before sunset at GB7RDG (IO91lk). That is about 09:00 to 17:00 UTC in October, 11:00 to 15:00 in December and 06:00 to 19:00 in June. GB7RDG announces its timetable in every slot, and the receiver follows that once it has heard it.
- **What**: a 10 second tone, then a few minutes of MS110D bursts, usually 2 to 8 minutes in all. Slots alternate between 1200 and 600 bps, and the receiver follows either by itself.
- **Where**: centred on 7.0538 MHz and filling about 7.0524 to 7.0553 MHz. A USB dial of 7.052 MHz puts the centre at 1800 Hz audio.

A bulletin is sent in pieces, and pieces from different slots add up, so one you only half heard at 10:00 can complete at 11:00. Sending the bulletins with a fountain code like this was Perry M0PYL's idea.

## Why the rig has to move

A rig set up for the UK HF packet channels, around 7.0503 to 7.0516 MHz, has its dial around 7.049 MHz and hears up to about 7.052 MHz. The bulletins start just above that, and at 2.9 kHz wide they don't fit an ordinary 2.4 kHz SSB filter anyway, so one dial can't hear both.

So pdn-soundmodem moves the rig for each slot: to 7.052 MHz USB from 1 minute before the hour to 12 minutes after, then back where it was. It does this through Hamlib's `rigctld`.

## Start rigctld

**A CAT radio Hamlib drives directly**, such as an IC-7300 on a USB cable:

```sh
rigctl -l | grep -i 7300            # find your rig's model number
rigctld -m 3073 -r /dev/ttyUSB0     # 3073 is an IC-7300
```

**A rig flrig already runs.** Only one program can hold the CAT port, so point rigctld at flrig instead, and flrig stays in charge of the radio:

```sh
rigctld -m 4                        # model 4 is Hamlib's flrig backend; start flrig first
```

Start rigctld before pdn-soundmodem, without `--vfo`, and have it start at boot the way your other station software does.

## Add the receiver

Add a `rig` section and a `mailcast` section to your config. Here they are added to the 40 m station from [08-hf.md](08-hf.md); only the last two lines are new:

```jsonc
{
  "device": "plughw:CARD=Device,DEV=0",
  "ptt": { "type": "cm108", "device": "/dev/hidraw0", "gpio": 3 },
  "sideband": "usb",
  "modems": [
    { "subChannel": 0, "mode": "afsk300-il2pc", "rfFrequency": 7050300, "port": 8101 },
    { "subChannel": 1, "mode": "ardop", "rfFrequency": 7050950, "bandwidth": 500, "port": 8200 },
    { "subChannel": 2, "mode": "bpsk300", "rfFrequency": 7051600, "port": 8102 }
  ],
  "waterfall": { "port": 8107 },
  // Added for the bulletins:
  "rig": { "rigctld": "127.0.0.1:4532", "mode": "PKTUSB" },
  "mailcast": { "bbs": { "password": "pick-one" }, "retune": true, "sources": ["GB7RDG", "M0LTE"] }
}
```

- `rig.mode` is for a rig that only takes data-jack audio in a data mode; the rig is put in it on the mailcast dial too. Leave it out for plain USB.
- With modems placed by `rfFrequency`, a `rig` section also lets pdn-soundmodem set your dial from the band plan at start-up, so check the `rig: setting the rig to` line reads the dial you use.
- `bbs.password` is the one you give the receiver's login when you [set up the BBS login](#set-up-the-bbs-login).
- `sources` is who the bulletins are taken from, any SSID. Leave it out and you get the same two.

More on the `rig` section is in [03-radios-and-interfaces.md](03-radios-and-interfaces.md#other-radios-through-hamlib). Restart the service, and the journal says how it will listen:

```
mailcast: the station's passband does not reach the signal on 7.0538 MHz, so the rig is retuned to 7.052 MHz USB from 1 minute before each slot to 12 minutes after, and put back; nothing is transmitted meanwhile
```

Without `"retune": true` or the `rig` section, the station stops instead (exit 2, and systemd leaves it stopped), saying why:

```
mailcast: the signal on 7.0524 to 7.0552 MHz is outside what this station hears. This station's dial is 7.04945 MHz USB and it hears 300-2700 Hz of audio (7.04975 to 7.05215 MHz); the signal would be at 2950-5750 Hz. Add a "rig" section (rigctld) and "mailcast"."retune": true, ...
```

Never set `retune` on GB7RDG's own station, or on any head end sending the bulletins: while the rig is away the transmit lease the head end needs is refused, so its slot would never go out.

## Choosing 7.052 or 7.0523

The signal itself does not move: it fills about 7.0524 to 7.0553 MHz whichever USB dial you use. The dial only changes where that fixed signal lands in your own audio, and on a rig with a tight receive filter, where it lands matters.

| Your rig's receive filter | Dial |
| --- | --- |
| An SDR, a data-mode audio path, or 2.7 kHz and wider | 7.052 MHz (the default) |
| 2.4 kHz or narrower | 7.0523 MHz |

On 7.052 the signal's centre lands at 1800 Hz audio, close enough to a 2.4 kHz (or narrower) SSB filter's upper roll-off to lose frames through it. On 7.0523 it lands at 1500 Hz instead, clear of that roll-off, at no cost to a wide filter, a data-mode audio path or an SDR - so 7.0523 works for everyone, but 7.052 is kept as the default because a station already on it keeps working.

If your station retunes for the bulletins (`"retune": true`), set `mailcast.dialKHz` to pick up the narrower-filter dial:

```json
{ "mailcast": { "bbs": { "password": "pick-one" }, "retune": true, "dialKHz": 7052.3 } }
```

If your station already hears the signal on its own passband (no `retune`), just set your own dial to whichever of the two suits your filter; pdn-soundmodem accepts either, no `mailcast` setting needed.

## Your packet traffic while the rig is retuned

While the rig is on 7.052 MHz your station transmits nothing at all, so nothing of yours is ever keyed on the bulletin frequency:

- frames from LinBPQ wait, and are dropped if they wait more than 30 s (AX.25 simply retries later);
- idents wait until the rig is back;
- ARDOP sessions and transmit leases are refused, and the transmitter test gives up after 60 s;
- your modems keep receiving, but on 7.052 MHz, so they hear nothing of your packet channels.

That is about 13 minutes in every daylight hour, roughly 2 hours a day in October. A connected session that is going on when the window opens will probably time out. Anything that keys the radio without going through pdn-soundmodem, such as Ardopcf or VARA on another sound card, is not held, and needs [the hooks below](#running-your-own-commands-around-each-slot).

If the station stops in the middle of a window, it puts the rig back when it next starts, before it sends anything.

Rather not take your packet radio off its channels? Give the bulletins a second radio on 7.052 MHz USB, with a pdn-soundmodem instance of its own as in [01-install.md](01-install.md#more-than-one-modem-on-one-machine): its own sound card and ports, no `ptt`, `"dialFrequency": 7052000` and the same `mailcast` section without `retune`.

## On a FlexRadio

pdn-soundmodem never retunes a Flex. A headless slice (a `flex:` device with no `@station`) tuned anywhere from about 7.046 to 7.052 MHz already hears the bulletins, because pdn-soundmodem opens the slice's receive filter to them, so just add the `mailcast` section without `retune`. Otherwise use a second slice as a second receiver: a second headless instance on 7.052 MHz with its own `daxChannel` and `"receiveOnly": true`, as in [DAX channels](03-radios-and-interfaces.md#dax-channels). Where else the receiver can listen without retuning is in [`mailcast`](reference/config.md#mailcast).

## Set up the BBS login

The receiver logs in to your BBS as `Q0CAST`, a forwarding partner of its own. Never use GB7RDG, M0LTE or your own callsign. A Q callsign is never issued, so Q0CAST clashes with nobody, and it is never sent on the air.

### LinBPQ

1. Stop LinBPQ. In `bpq32.cfg`, in your Telnet port's `CONFIG` section, add an `FBBPORT` (keep yours if you have one) and a user for the receiver:

   ```
    FBBPORT=8011
    USER=Q0CAST,pick-one,Q0CAST,,
   ```

   Leave the fourth field empty: the receiver sends the `BBS` command itself. Start LinBPQ again.

2. In LinBPQ's web page, open **Mail Mgmt**. Under **Users**, add **Q0CAST** and tick **BBS**.

3. On Q0CAST's **Forwarding** page, tick **FBB Blocked** (forward in FBB's binary blocks, not line-by-line text), **Allow Binary** (LinBPQ's label for allowing compressed forwarding, which blocked forwarding also needs) and **Use B1 Protocol** (the simpler of FBB's two binary protocols). Leave the TO, AT, TIMES, Connect Script and HR Routes boxes empty, so nothing is ever queued for it. It doesn't need forwarding enabled: LinBPQ never calls it. Click **Update** to save.

4. Put the same password in `mailcast.bbs.password`. If your `FBBPORT` isn't 8011, set `mailcast.bbs.port` to it.

### Linux FBB

Not tested yet; this follows FBB 7.0.11's documentation.

1. In `port.sys`, add a telnet port and a TNC line for it with mode `T`.
2. Add the user Q0CAST (`EU Q0CAST`) with the **B** (BBS) and **M** (modem and telnet) flags, and set its password.
3. Don't add Q0CAST to `forward.sys`.
4. Set `"type": "fbb"`, and FBB's `host` and `port`, in `mailcast.bbs`:

   ```json
   { "mailcast": { "bbs": { "type": "fbb", "port": 6300, "password": "pick-one" } } }
   ```

Every `bbs` key, with its default, is in [`mailcast`](reference/config.md#mailcast). pdn-mailcast's [receiver guide](https://github.com/packet-net/pdn-mailcast/blob/main/docs/receiver.md#the-receivers-login-on-your-bbs) has more on both.

## Running your own commands around each slot

Optional. If something else shares the radio, Ardopcf say, the station can run a command of yours before each slot to stop it, and another afterwards to start it again:

```json
{
  "mailcast": {
    "bbs": { "password": "pick-one" },
    "retune": true,
    "sources": ["GB7RDG", "M0LTE"],
    "hooks": {
      "before": { "command": "/usr/local/bin/mailcast-hook", "args": ["stop"], "timeoutSeconds": 30 },
      "after": { "command": "/usr/local/bin/mailcast-hook", "args": ["start"] }
    }
  }
}
```

"before" is done before the rig is retuned, and if it fails the rig isn't retuned for that slot. "after" runs once the rig is back. This script stops or starts Ardopcf on another machine:

```sh
#!/bin/sh
# /usr/local/bin/mailcast-hook: stop or start Ardopcf on the shack PC.
K=/var/lib/pdn-soundmodem/.ssh
exec ssh -i $K/id_ed25519 -o UserKnownHostsFile=$K/known_hosts -o BatchMode=yes -o ConnectTimeout=10 ardop@shack-pc "sudo systemctl $1 ardopcf"
```

On the shack PC, a sudoers line such as `ardop ALL=(root) NOPASSWD: /usr/bin/systemctl stop ardopcf, /usr/bin/systemctl start ardopcf` lets it do that without a password.

The commands run as the service's user, `pdn-soundmodem`, which has no home directory, so its ssh key lives in the state directory. Make the key, copy it across (this asks for `ardop`'s password once), and record the shack PC's host key:

```sh
sudo install -d -m 700 -o pdn-soundmodem -g pdn-soundmodem /var/lib/pdn-soundmodem/.ssh
sudo -u pdn-soundmodem ssh-keygen -t ed25519 -N "" -f /var/lib/pdn-soundmodem/.ssh/id_ed25519
sudo ssh-copy-id -i /var/lib/pdn-soundmodem/.ssh/id_ed25519.pub ardop@shack-pc
sudo -u pdn-soundmodem ssh -i /var/lib/pdn-soundmodem/.ssh/id_ed25519 -o UserKnownHostsFile=/var/lib/pdn-soundmodem/.ssh/known_hosts -o StrictHostKeyChecking=accept-new ardop@shack-pc true
```

Make the script executable (`sudo chmod 755 /usr/local/bin/mailcast-hook`), try it with `sudo -u pdn-soundmodem /usr/local/bin/mailcast-hook stop` and `start`, then restart the service. The timing, the environment variables your command is given and what happens when the station stops mid-slot are in [`mailcast.hooks`](reference/config.md#mailcasthooks).

## Check it works

Follow the journal:

```sh
journalctl -u pdn-soundmodem -f | grep -E 'mailcast|rig:'
```

At start-up, after the line saying where it listens:

```
mailcast: taking frames to MCAST from GB7RDG or M0LTE (any SSID)
mailcast: the slots are every hour on the hour, in daylight from 120 minutes after sunrise to 30 minutes before sunset at IO91lk (the built-in timetable; the broadcast's directory updates it once heard)
mailcast: delivering rebuilt bulletins to LinBPQ at 127.0.0.1:8011 as Q0CAST; state in /var/lib/pdn-soundmodem/mailcast
mailcast: status on the station page and at http://127.0.0.1:8107/api/mailcast
```

A first slot on a retuned rig then looks like this. The rig moves a minute early, and you see a `rig: mailcast renewed its window` line every minute while it is there:

```
mailcast: rig on 7.052 MHz PKTUSB for the 12:00 UTC slot until 12:12 UTC; nothing is transmitted until it is put back
rig: tuned to 7.052000 MHz PKTUSB (3000 Hz passband) for mailcast until 2026-10-06T12:04:00Z; transmissions are held until it is put back to 7.049450 MHz PKTUSB (2400 Hz passband)
mailcast: tone +1.3 Hz from where it should be, SNR 14.2 dB in 3 kHz, 10 s
mailcast: 1 frame heard in this slot
mailcast: the broadcast's directory gives its slots as every hour on the hour, in daylight from 120 minutes after sunrise to 30 minutes before sunset at IO91lk; using that
mailcast: directory for 2026-10-06: 9 bulletins in rotation
mailcast: bulletin complete: 1001_GB7ABC from G8ABC to ALL@GBR, "Net tonight"
mailcast: bbs: 1001_GB7ABC accepted by the BBS
rig: the window on 7.052000 MHz PKTUSB (3000 Hz passband) for mailcast has ended (mailcast released it)
rig: put back to 7.049450 MHz PKTUSB (2400 Hz passband); transmissions resume
mailcast: the 12:00 UTC slot's listening window has ended; the rig goes back
```

A tone of about 10 s at the top of the hour is GB7RDG's. On a Flex or a second radio that already hears the signal there are no `rig:` lines, and the rest is the same.

On the station page, the Mailcast box in the header shows the next slot, the last slot's tone (offset and SNR), the frames heard, bulletins complete, partial and delivered, the BBS (`ok`, `failing` or `not tried yet`) and, when retuning, the rig (`on mailcast` or `waiting`). Hover it for the detail. The same numbers are at `GET /api/mailcast`. The BBS password is never shown or logged.

Pieces and rebuilt bulletins are kept in `mailcast/` in the state directory, so nothing is lost across a restart, and a bulletin waits there until the BBS has answered for it.

## If it did not

- **`mailcast: the signal on ... is outside what this station hears`** at start-up: your station can't hear it as configured. Follow its advice, or see [Add the receiver](#add-the-receiver).
- **`rig: WARNING - cannot reach rigctld at ...`**: rigctld isn't running, or is on another address. The station carries on and keeps trying.
- **`mailcast: WARNING - cannot retune the rig for the 12:00 UTC slot yet (...)`**: the reason is in the brackets. Usually the rig was keyed at that moment, or rigctld was away. It tries again every 5 s until the slot's window ends.
- **`mailcast: hooks: WARNING - "before" for the 12:00 UTC slot ...`**: your command failed, so the rig wasn't retuned for that slot. Run it by hand as `sudo -u pdn-soundmodem` to see why.
- **`mailcast: bbs: LinBPQ refused the login; ...`**: the login and password must match the `USER=` line, and `port` must be the `FBBPORT`, not the ordinary telnet port.
- **`the BBS closed the connection before it offered a forwarding session`**: Q0CAST isn't a BBS user in LinBPQ's mail configuration yet (step 2).
- **`no answer from 127.0.0.1:8011`**: the BBS isn't running, or isn't listening on that port.
- **`rejected by the BBS: it already has this BID`**: nothing is wrong. Your BBS already had that bulletin from another partner.
- **`bbs: WARNING - the BBS tried to send the receiver's login ... message(s)`**: something is queued for Q0CAST. Empty the TO, AT and HR boxes on its forwarding page.
- **A tone `... not at a slot's start, so it is not the broadcast's; ignored`**: someone else near the frequency, or your clock is wrong. The slots are timed by this machine's clock, so keep it on NTP.
- **No tone and no frames**: check the time against the slots (daylight only), that 40 m is open from you, and that the waterfall shows the signal during a slot. On a retuned rig, check the radio really moved to 7.052 MHz, and that it is in a mode that takes your data-jack audio.
- **A tone but few frames**: the signal is weak at your end. In GB7RDG's first tests, stations with 10 dB SNR or more in 3 kHz decoded nearly every frame, and one at 7.6 dB about half. The missing pieces come in later slots. Check your receive level in [04-levels.md](04-levels.md).
- **`a frame uses a compression dictionary this version does not have`**: upgrade pdn-soundmodem.

## Related

- [`mailcast`](reference/config.md#mailcast) and [`rig`](reference/config.md#rig) in the configuration reference.
- [Rig tuning windows](reference/ports-and-endpoints.md#rig-tuning-windows) and `GET /api/mailcast` in [ports and endpoints](reference/ports-and-endpoints.md).
- pdn-mailcast's [fact sheet](https://github.com/packet-net/pdn-mailcast/blob/main/docs/factsheet.md), for the signal and the timetable in detail.
