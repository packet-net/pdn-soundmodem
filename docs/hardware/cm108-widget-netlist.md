# CM108 radio widget: netlist

Derived netlist for the CM108AH interface board used with the Tait TM8100 assembly in
[tm8100-cm108-interface.md](tm8100-cm108-interface.md). Recorded here because the interface
design depends on what the board actually does, and reading it off a schematic viewer each
time invites the errors documented at the bottom of this file.

## Provenance

Source: `tomwardill/cm108radiowidget`, file `single-sided-radio-interface.kicad_sch`,
KiCad 8 (`version 20231120`), repository state `e506de1` (2025-01-01). The upstream project
is MIT licensed, which is GPL-compatible, so restating its connectivity here is fine; the
schematic itself is not vendored.

Extracted mechanically with an s-expression parser, not by eye. The extractor is
[tools/kicad-netlist.py](../../tools/kicad-netlist.py); re-run it against the upstream file
to reproduce or to check a newer revision:

```
python3 tools/kicad-netlist.py path/to/single-sided-radio-interface.kicad_sch
```

## Components

| Ref | Value | Role |
|---|---|---|
| U1 | CM108AH | USB audio codec, 48-pin |
| Y1 | 12 MHz | crystal, loaded by C1/C2 (20p) |
| Q1 | BSS138 | PTT switch, open drain |
| D1 | B5819W (BAT46W) | Schottky, squelch pad to VOLDN |
| L1 | 10u | VBUS to VCC |
| R1, R3 | 22 | USB D-/D+ series |
| R2 | 1.5k | USB D+ pull-up to +3V3 (full speed) |
| C1, C2 | 20p | crystal load |
| C3, C5 | 100n | VCC and +3V3 decoupling |
| C4, C6 | 4.7u | VCC and +3V3 bulk |
| C7 | 4.7u | BGREF bandgap reference |
| C8 | 1u | **audio out coupling** |
| C9 | 1u | **audio in coupling** |
| J5 | PTT | pad |
| J6 | AUDIO_OUT | pad |
| J7 | AUDIO_IN | pad |
| J8 | SQUELCH | pad |
| J9 | GND | pad |
| J10 | USB micro-B | |

## Nets

```
+3V3    C5.1, C6.1, R2.2, U1.37(REGV), U1.38(MSEL), U1.7(PWRSEL)
VCC     C3.1, C4.1, L1.1, U1.29(AVDD), U1.34(AVDD), U1.35(DVDD)
GND     C1.2, C2.2, C3.2, C4.2, C5.2, C6.2, C7.2, J9.1, J10.5(GND), Q1.2(S),
        U1.10(MODE), U1.14(DVSS), U1.22(ADSEL), U1.23(TEST), U1.24(AVSS),
        U1.33(AVSS), U1.36(DVSS), Y1.2, Y1.4
AUDIO_OUT   C8.1, J6.1
AUDIO_IN    C9.1, J7.1
PTT         J5.1, Q1.3(D)
SQUELCH     D1.1(K), J8.1
N$01    R3.1, U1.41(USBDP)          N$08    C9.2, U1.27(MICIN)
N$02    J10.3(D+), R2.1, R3.2       N$09    Q1.1(G), U1.13(GPIO3)
N$03    R1.1, U1.42(USBDM)          N$10    D1.2(A), U1.48(VOLDN)
N$04    J10.2(D-), R1.2             N$11    J10.1(VBUS), L1.2
N$05    C2.1, U1.9(XO), Y1.3        N$12    C8.2, U1.30(LOL)
N$06    C1.1, U1.8(XI), Y1.1        N$13    J10.4(ID)
N$07    C7.1, U1.26(BGREF)          N$14    J10.6(Shield)
```

Every other U1 pin is a single-pin net carrying an explicit no-connect flag: 1 SPDIFO,
2 DI, 3 DO, 4 SK, 5 CSO, 6 MUTER, 11 GPIO2, 12 LED0, 15 GPIO4, 16 SDIN, 17 ADSCLK,
18 MUTEP, 19 ADLRCK, 20 ADMCLK, 21 LEDR, 25 VBIAS, 28 N.C., 31 LOBS, 32 LOR, 39 VOLUP,
40 PDSW, 43 GPIO1, 44 SDOUT, 45 DAMCLK, 46 DALRCK, 47 DASCLK.

## What this means for the interface

- **Transmit is LOL only, AC coupled.** `U1.30 (LOL)` -> C8 (1u) -> `AUDIO_OUT`. The right
  channel `LOR` is a no-connect, so it makes no difference whether the host emits mono
  duplicated to both channels or drives left alone. The 1u against the divider's 4k input
  puts a pole at about 40 Hz, well below anything the modem uses; against a 1M scope probe
  it is 0.16 Hz, so bench readings see no droop.
- **Receive is MICIN, AC coupled.** `AUDIO_IN` -> C9 (1u) -> `U1.27 (MICIN)`.
- **No microphone bias network.** `VBIAS` (25) is a no-connect, which is what makes the
  receive divider clean: Rp is the whole shunt. This confirms the claim in the interface
  note, and is why values from stock-dongle write-ups with a 2k2 bias resistor do not apply.
- **`LOBS` (31) is a no-connect.** The line-out bias reference is left floating and
  undecoupled. Worth knowing when chasing output-level oddities, though a 1.4 dB level
  discrepancy chased on 2026-08-14 turned out to be measurement, not the board.
- **PTT is open drain.** `GPIO3` -> `Q1.1 (G)`, `Q1.3 (D)` -> `PTT` pad, `Q1.2 (S)` -> GND.
  Confirms `"ptt": { "type": "cm108" }` with the default GPIO 3. **There is no gate
  pull-down**, which is the basis for the suggested 100k from gate to ground: GPIO3 floats
  briefly during USB enumeration.
- **SQUELCH reaches VOLDN through a diode.** `J8` -> `D1` cathode, `D1` anode -> `U1.48
  (VOLDN)`. Pulling the pad low pulls VOLDN low, so a squelch wire presents as the CM108's
  volume-down HID key. `VOLUP` (39) is a no-connect and therefore floats.
- **Power.** `VBUS` -> L1 (10u) -> VCC (AVDD, DVDD), 4.7u + 100n. The chip's own regulator
  output `REGV` is the +3V3 rail, 4.7u + 100n, and supplies the USB pull-up.

## Extraction traps

Four bugs were hit deriving this, and each produced a plausible netlist rather than an
error, which is the dangerous failure mode. Anyone re-deriving it should check for these:

1. A wire regex assuming both points sit on one line matches **zero** of the 69 wires in a
   KiCad 8 file, and every part then reads as unconnected.
2. Matching library symbols by name prefix lets `Device:C` inherit `CM108AH`'s pin list, so
   capacitors get reported with pins called AVDD and LOL.
3. Ignoring `(mirror y)` places a symbol's pins where no wire is. Q1 is mirrored, so the
   entire PTT circuit came out as three isolated single-pin nets.
4. `PWR_FLAG` is an ERC marker, not a net name. Treating its Value as a label gives every
   flagged net the same name, welding VCC to GND, and AVDD/DVDD then appear grounded.

The extractor asserts U1 has 48 distinct pins and that no supply pin lands on GND. Both
checks exist because both failed at some point.
