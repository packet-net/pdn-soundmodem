# ARDOP oracle fixtures

Checked-in ardopcf reference material for the ARDOP Phase A tests
(`tests/.../Ardop/`). They let the suite validate against the interop oracle
without an ardopcf build present. See `docs/ardop-design.md` §6 for the
validation ladder these serve.

## Provenance

Everything here derives from **ardopcf v1.0.4.1.3, git
`a7c92289b569afbe4259dc556d749405ebc008f5`** (MIT, © 2014-2024 Rick Muething
KN6KB, John Wiseman G8BPQ, Peter LaRue AI7YN — `github.com/pflarue/ardop`),
built with gcc from unmodified sources.

| File(s) | What |
|---|---|
| `txframe_*.wav` | One TXFRAME transmission each (leader 240 ms, trailer 20 ms, drive 100, session ID 0xFF), written by ardopcf's null-device TX path (`--writetxwav`, `NOSOUND` audio — no sound card involved). 16-bit mono PCM at ARDOP's native 12 kHz. Every Phase A frame type: the short control frames, ACK/NAK with quality, ConAck timing frames, PingAck, IDFrame, all eight ConReq variants, Ping, and the ten 4FSK data frames (even types at full capacity, odd types partially filled). |
| `txframe-manifest.txt` | Expected frame type / session ID / payload (or decoded field values) per WAV — what the RX tests assert against. |
| `ardop-reference-vectors.txt` | Component byte vectors: CRC-16 (the nonstandard 0x8810 formulation), CRC-8 session IDs, the frame-type parity of all 256 codes, and Reed-Solomon parity for every Phase A geometry. Produced by `gen-reference-vectors.c`, which compiles ardopcf's **actual** `lib/rockliff/rrs.c` (Simon Rockliff's RS codec, permissive licence — attribution in `rrs.txt` there) together with verbatim copies of `GenCRC16`, `GenCRC8` and `ComputeTypeParity` from the reference sources. |
| `generate.sh` + `gen-payloads.py` | Regeneration scripts. Payloads come from a fixed xorshift32 stream so regeneration is byte-stable. |
| `gen-reference-vectors.c` | The component-vector harness (compile with `gcc -I<ardop>/lib -o gen gen-reference-vectors.c <ardop>/lib/rockliff/rrs.c`). |

## Regenerating

```sh
git clone --depth 1 https://github.com/pflarue/ardop ardop-ref
( cd ardop-ref && git checkout a7c9228 && make )
ARDOPCF=$PWD/ardop-ref/ardopcf sh generate.sh
```

Every fixture decodes in ardopcf's own receiver (`--decodewav`), verified at
generation time: 33/33.
