# Receiving pdn-mailcast

GB7RDG sends its recent packet mail bulletins on 40 m every daylight hour, as [pdn-mailcast](https://github.com/packet-net/pdn-mailcast). Your station can listen for them and hand each bulletin it rebuilds to your own BBS, as if it came from a forwarding partner. Nothing is sent on the air for this: the receiver only listens.

## Before you start

- A station on 40 m that pdn-soundmodem already runs: a sound card on a rig, a FlexRadio, or a web receiver.
- LinBPQ or Linux FBB, with a login for the receiver. It logs in as `Q0CAST`; pdn-mailcast's [receiver guide](https://github.com/packet-net/pdn-mailcast/blob/main/docs/receiver.md#the-receivers-login-on-your-bbs) shows how to add that login to LinBPQ (on its FBBPORT) or FBB.

Using QtSoundModem rather than pdn-soundmodem? This page is not for you. Run pdn-mailcast's standalone receiver instead; its LinBPQ `XMITOFF` interlock, which keeps LinBPQ off the air while it borrows the radio, comes in pdn-mailcast v0.4.0.

## Add the section

```json
{
  "mailcast": {
    "bbs": { "type": "linBpq", "host": "127.0.0.1", "port": 8011, "login": "Q0CAST", "password": "pick-one", "command": "BBS" },
    "dialKHz": 7052.0,
    "retune": false
  }
}
```

Only `bbs.password` is needed; everything else above is the default. `type` is `"fbb"` for Linux FBB. Every key is in [`mailcast`](reference/config.md#mailcast).

Restart the service, and the journal says where the receiver listens:

```
mailcast: listening on the station's own passband, the signal's centre 7.0538 MHz at 3700 Hz audio (2300-5100 Hz); no retuning
mailcast: GB7RDG's slots are every hour on the hour, in daylight from 120 minutes after sunrise to 30 minutes before sunset at IO91lk (its own; its directory updates them once heard)
mailcast: delivering rebuilt bulletins to LinBPQ at 127.0.0.1:8011 as Q0CAST; state in /var/lib/pdn-soundmodem/mailcast
```

## Where it listens

The signal is centred on 7.0538 MHz, 1.8 kHz above a USB dial of 7.052 MHz, and fills 7.0522 to 7.0554 MHz. There are two ways your station can hear it.

**Your passband already reaches it.** Nothing is retuned, and the receiver listens all the time beside your other modems. This is the case when:

- a headless FlexRadio's slice is within reach of it: pdn-soundmodem opens the slice's receive filter to include it, as on GB7RDG's own slice at 7.0501 MHz;
- your band plan or `dialFrequency` puts the whole signal inside your receive window;
- a web receiver's SSB window (`ubersdr.ssbLowHz` to `ssbHighHz`) covers it;
- your radio is on 7.052 MHz USB already.

**It does not.** Set `"retune": true` and add a [`rig`](reference/config.md#rig) section, and the rig is retuned to 7.052 MHz USB from 1 minute before each of GB7RDG's slots to 12 minutes after, then put back:

```
mailcast: rig on 7.052 MHz USB for the 12:00 UTC slot until 12:12 UTC; nothing is transmitted until it is put back
mailcast: the 12:00 UTC slot's listening window has ended; the rig goes back
```

While the rig is away your station sends nothing at all: frames from your node wait and are dropped after 30 s (AX.25 simply retries), idents wait, and ARDOP, the transmitter test and a transmit lease are refused. That is about 13 minutes an hour in daylight, so think about what else uses the radio. A station stopped in the middle of a window puts the rig back when it next starts, before it sends anything.

Neither? The station will not start, and says why and what to change.

GB7RDG's slots are its own until the receiver hears its directory, which carries them; from then on the receiver follows the directory.

## Hamlib and flrig

Retuning goes through Hamlib's `rigctld`. For a rig Hamlib drives directly:

```sh
sudo apt install libhamlib-utils
rigctld -m 3073 -r /dev/ttyUSB0     # 3073 is an IC-7300; rigctl -l lists the others
```

If flrig already runs your rig, point `rigctld` at flrig instead, which keeps flrig in charge of the radio:

```sh
rigctld -m 4                        # model 4 is FLRig; start flrig first
```

Then:

```json
{ "rig": {}, "mailcast": { "bbs": { "password": "pick-one" }, "retune": true } }
```

A rig that takes data-jack audio only in a data mode is retuned in the `rig` section's `mode` (such as `PKTUSB`). [03-radios-and-interfaces.md](03-radios-and-interfaces.md#other-radios-through-hamlib) has more on the `rig` section.

## What you see

On the station page a Mailcast box in the header shows the next slot, the last slot's tone (how far off frequency and its signal-to-noise ratio), the frames heard in it, bulletins complete, partial and delivered, the BBS's state and, when retuning, the rig's. Hover it for the detail. The same numbers are at `GET /api/mailcast`; the BBS password is never shown or logged.

The journal has a line for the tone, each bulletin as it completes, and what the BBS said about it:

```
mailcast: tone +1.3 Hz from where it should be, SNR 14.2 dB in 3 kHz, 10 s
mailcast: bulletin complete: 1001_GB7ABC from G8ABC to ALL@GBR, "Net tonight"
mailcast: bbs: 1001_GB7ABC accepted by the BBS
```

The pieces, the rebuilt bulletins waiting for the BBS, and `deliveries.jsonl` (what the BBS said about each) are in `mailcast/` in the state directory, so nothing is lost across a restart.

## If it did not

- `mailcast: the signal on ... is outside what this station hears` at start-up: follow its advice, usually `"retune": true` with a `rig` section.
- `mailcast: bbs: LinBPQ refused the login`: the login and password must match a `USER=` line on LinBPQ's FBBPORT, and the mail configuration must have the login as a BBS.
- `mailcast: WARNING - cannot retune the rig for the 12:00 UTC slot yet`: rigctld is not answering, the rig is keyed, or something else holds a tuning window. It keeps trying until the slot's window ends.
- No tone and no frames: check that 40 m is open from you (GB7RDG only sends in daylight) and that the station page's waterfall shows the signal around the mailcast dial.
