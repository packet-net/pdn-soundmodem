# FreeDV OFDM oracle harness

`oracle_harness.c` drives **codec2 1.2.0** (git `310777b1`, LGPL-2.1) to generate the reference
TX vectors under [`samples/freedv/`](../../samples/freedv/) that
`tests/Packet.SoundModem.Tests/Ofdm/OfdmModulatorOracleTests.cs` checks
`Packet.SoundModem.Ofdm.OfdmModulator` against. It is a **build-time / one-off generator**, not
part of the .NET build or the runtime — libcodec2 never appears on the `src/` reference graph
(the checked-in `.s16`/`.f32` vectors are what the tests read).

See [`samples/freedv/PROVENANCE.md`](../../samples/freedv/PROVENANCE.md) for the exact
clone/build/run commands and what each generated file contains.

```sh
# from a directory containing the codec2-ref checkout built at 310777b1 (see PROVENANCE.md)
gcc oracle_harness.c -I codec2-ref/src -I codec2-ref/build/src \
    -L codec2-ref/build/src -lcodec2 -lm -o oracle_harness
LD_LIBRARY_PATH=codec2-ref/build/src ./oracle_harness samples/freedv
```

Regenerate the vectors only on an intentional codec2 bump, and record the new SHA in
`samples/freedv/PROVENANCE.md`.
