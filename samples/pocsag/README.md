# pocsag/ — POCSAG paging transmissions (multimon-ng cross-validation corpus)

Our POCSAG transmitter's output, one WAV per standard rate — mono 48 kHz 16-bit, the
rate the daemon actually transmits at. **Reproducible**: `sm-pocsag encode` regenerates
them byte-for-byte (commands below). They are checked in as the fixed corpus behind the
multimon-ng cross-validation results recorded here, and so the waveform can be played
into receivers without building anything.

POCSAG is implemented spec-first (CCIR Radiopaging Code No. 1 / ITU-R M.584-2);
multimon-ng is the *cross-check*, not the source — see `PROVENANCE.md` § `Pocsag/*`.

## Files

| file | rate | pages |
|---|---|---|
| `pocsag1200-mixed-48k.wav` | 1200 bd (DAPNET) | 5 — alpha, numeric, alpha (function 2), numeric at RIC 2097151, tone-only |
| `pocsag512-48k.wav` | 512 bd | 2 — alpha + numeric |
| `pocsag2400-48k.wav` | 2400 bd | 2 — alpha + numeric |

Addresses exercise the edges: RIC 8 (frame 0, smallest non-zero high bits), 133703 and
2007287 (frame 7), 2097151 (the top of the 21-bit RIC space), 21 and 42 (mid frames).

Polarity is the spec convention ('0' bit = high frequency = positive baseband sample) —
what multimon-ng expects with no flags, and what an FM discriminator feeding a DAPNET
receiver produces. multimon-ng 1.3.0 (Debian/Ubuntu package) does **not** auto-detect
inverted input (`-i` exists for that); our own `PocsagDecoder` auto-detects both.

## Validation — multimon-ng 1.3.0, 2026-07-16

Every page decodes exactly (9/9 across the three files):

```sh
$ sox samples/pocsag/pocsag1200-mixed-48k.wav -t raw -e signed-integer -b 16 -r 22050 - \
    | multimon-ng -q -c -t raw -a POCSAG1200 -
POCSAG1200: Address:  133703  Function: 3  Alpha:   Hello DAPNET interop
POCSAG1200: Address:       8  Function: 0  Numeric: 0123456789-U.[]
POCSAG1200: Address: 2007287  Function: 2  Alpha:   Frame seven, function two
POCSAG1200: Address: 2097151  Function: 0  Numeric: 999 111
POCSAG1200: Address:      21  Function: 1
```

(and likewise with `POCSAG512` / `POCSAG2400` for the other two files; the 512 alpha
page shows one trailing `<NUL>` — the zero-bit padding completing the final codeword,
per the DAPNET encoder convention, and the numeric pages show trailing spaces from the
0xC space-nibble padding.)

The same suite runs live in CI: `PocsagMultimonTests` renders at multimon's native
22050 Hz and asserts the exact decode lines (skips when multimon-ng is absent). Our own
decoder also decodes 5/5, 2/2, 2/2:

```sh
dotnet run --project tools/Packet.SoundModem.Pocsag -- decode samples/pocsag/pocsag1200-mixed-48k.wav
```

**No independent known-good POCSAG *encoder* was installable** (multimon-ng ships no
generator; UniPager needs a DAPNET core to drive it), so the reverse leg — foreign audio
into our decoder — is covered only by our own encoder's output. The multimon-ng leg
above carries the interop weight; first off-air DAPNET capture should be added here when
one is taken (439.9875 MHz).

## Regenerating

```sh
dotnet run --project tools/Packet.SoundModem.Pocsag -- encode samples/pocsag/pocsag1200-mixed-48k.wav \
    "133703:3:a:Hello DAPNET interop" \
    "8:0:n:0123456789-U.[]" \
    "2007287:2:a:Frame seven, function two" \
    "2097151:0:n:999 111" \
    "21:1:t"
dotnet run --project tools/Packet.SoundModem.Pocsag -- encode samples/pocsag/pocsag512-48k.wav \
    --baud 512 "133703:3:a:pocsag512 leg" "42:0:n:8675309"
dotnet run --project tools/Packet.SoundModem.Pocsag -- encode samples/pocsag/pocsag2400-48k.wav \
    --baud 2400 "133703:3:a:pocsag2400 leg" "42:0:n:8675309"
```
