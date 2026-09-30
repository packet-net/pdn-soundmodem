# lowsnr-fm

The probe behind [docs/dev/plans/low-snr-fm.md](../../docs/dev/plans/low-snr-fm.md): one slow
audio-tone MFSK waveform through M0LTE.FmChannel, and three receivers on every burst, so that what
an FM radio's audio can carry below its threshold is measured against what its IF can.

- `A` the tone energies in the discriminator audio (what feeding FT8 to an FM radio does)
- `P` the discriminator audio integrated back to a phase, `exp(j theta)`, and the tone's sideband
  pattern detected there, with a burst-level offset search
- `S` the same sideband receiver on the IF complex envelope from `FmChannel.ApplyToIf`, an SDR tap

Timing is a genie's in all three. Not part of the solution, not built by CI, and it needs
M0LTE.FmChannel at a commit with `ApplyToIf` (see the csproj).

```sh
dotnet build -c Release
./bin/Release/net10.0/lowsnr-fm --cnr 0,-8,-16,-20,-24            # symbol error rate per receiver
./bin/Release/net10.0/lowsnr-fm --frame --seeds 8 --cnr -16,-20     # one LDPC (512,256) codeword a burst
./bin/Release/net10.0/lowsnr-fm --link mic --cnr 0,-4,-8            # microphone and speaker path
./bin/Release/net10.0/lowsnr-fm --hpf 70 --inv 70 --leak 0.005 --track --cnr -8,-16   # a 70 Hz coupling, undone
./bin/Release/net10.0/lowsnr-fm --cfo 250 --cnr -16                 # the SDR with the carrier 250 Hz off
```

Options: `--link tait|tait25|mic|data`, `--dev` (Hz), `--T` (symbol seconds), `--M` (tones),
`--f0` (first tone Hz), `--K` (symbols a burst), `--seeds`, `--harm` (sideband pairs summed),
`--rxhigh` (override the receive audio low-pass), `--hpf` (a one-pole coupling applied to the audio,
Hz), `--inv` (the corner the receiver undoes, Hz), `--leak` (its integrator's leak, Hz), `--track`
(per-symbol offset, median-smoothed), `--search` (offset search half-width, Hz), `--cfo` (carrier
error the SDR sees, Hz), `--frame`, `--llr` (LLR scale), `--selftest` (the coupling inverse alone).
