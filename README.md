# Unknown OFDM signal on the GB7RDG 40 m slot, 2026-10-26

GB7RDG's own receive audio (8 kHz mono, dial 7049.750 MHz USB, silence while GB7RDG transmits). The signal is clearest from 14:42:54 to about 14:46:20 UTC. The pdn-soundmodem kept-audio format: audio.wav, detector.csv, waterfall.png, window.json.

- `20261026T144254Z-unknown-signal-a`: 14:42:54 to 14:45:30 UTC
- `20261026T144530Z-unknown-signal-b`: 14:45:30 to 14:55:30 UTC (signal in the first ~50 s; clean bursts at 40.7-43.9 s and 44.5-48.1 s)
- `closeup-14-45-55.png`: 25-50 s of window b

Measured: about 2.36 kHz wide (-3 dB), centre about 1810 Hz audio (7051.56 MHz), 46.875 Hz carrier spacing, 24.0 ms symbols (21.33 ms plus 2.67 ms guard), bursts of 3.6 to 4 s. Suspected Mercury v1 wide mode (Rhizomatica), not yet confirmed.
