# Receive GB7RDG's 40 m bulletins into your own BBS

GB7RDG sends packet BBS bulletins on 40 m every daylight hour, and you can drop them into your own LinBPQ or FBB without transmitting anything. The program for that is now [pdn-mailcast-receiver](https://github.com/packet-net/pdn-mailcast/blob/main/docs/receiver.md), version 0.9.0 or later. It runs beside pdn-soundmodem on the same machine and listens through this station's audio, so your radio and sound card stay as they are.

## The built-in receiver is gone

pdn-soundmodem no longer has its own mailcast receiver; the `mailcast` section is not read. A station whose config still has the section starts and runs as a modem as before, and says so once in the journal at start-up:

```
mailcast: WARNING - the built-in mailcast receiver was removed and the "mailcast" section is ignored; install pdn-mailcast-receiver (audio "soundmodem") instead, guide: https://github.com/packet-net/pdn-mailcast/blob/main/docs/moving-from-built-in.md
```

Remove the section to stop the line. The folder the old receiver kept its files in (`mailcast/` in the state directory, or the section's `stateDirectory`) is left on disk untouched; delete it when you no longer want it.

## Moving across

[Moving from pdn-soundmodem's built-in receiver](https://github.com/packet-net/pdn-mailcast/blob/main/docs/moving-from-built-in.md) has the steps. In short:

1. Install pdn-mailcast-receiver from the packet-net apt repo, on the same machine.
2. In its config, set `"audio": "soundmodem"`, and copy your settings across with the table below. Most keep their names.
3. Remove the `mailcast` section from pdn-soundmodem's config, and restart both.

Your BBS needs no change: the receiver logs in as the same `Q0CAST`, with the same password. Once it is listening, this station's page names it, with a link to its own status page.

| In the old `mailcast` section | In the receiver's config |
|---|---|
| `bbs`, with `type`, `host`, `port`, `login`, `password` and `command` | `bbs`, the same keys |
| `sources` | `sources` |
| `dialKHz` | `dialKHz` |
| `hooks`, with `before` and `after`, each `command`, `args` and `timeoutSeconds` | `hooks`, the same keys |
| `"retune": true` | `"retune": true`. pdn-soundmodem still does the tuning, so keep its `rig` section. |
| `stateDirectory` | Not copied. The receiver keeps its own state, and your BBS turns away any bulletin it already has. |
| (nothing) | `"audio": "soundmodem"`, or `"soundmodem:PORT"` if this station's page is not on port 8107 |

Leave out of the receiver's config anything your `mailcast` section left out: the defaults are the same.

## Still need the old receiver?

Its set-up guide is [this page as it was in 0.99.0](https://github.com/packet-net/pdn-soundmodem/blob/v0.99.0/docs/14-mailcast.md), the last release that has the built-in receiver.
