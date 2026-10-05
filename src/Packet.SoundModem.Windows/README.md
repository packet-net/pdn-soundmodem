# pdn-soundmodem-windows

Windows backends for the pdn-soundmodem core, so that a Windows application can run the modem in
its own process over a USB radio interface. Published to NuGet as `pdn-soundmodem-windows` by the
same release as the core, on the same version, depending on the core at exactly that version. The Linux daemon does not use or reference this
library; the core knows nothing about it.

## What is here

- `WasapiAudioInput` / `WasapiAudioOutput`: WASAPI capture and render as the core's `IAudioInput`
  and `IAudioOutput`. Shared mode, mono float at 48 kHz, with Windows doing any conversion from the
  endpoint's mix format. All COM runs on one dedicated MTA thread per stream. The output plays
  silence between transmissions and its `Drain` returns once the last real sample has left the
  audio engine, which is what the transmitter unkeys on.
- `HidPtt`: PTT through a CM108-compatible HID GPIO pin (CM108/CM119 dongles, Digirig-style
  interfaces, and the AIOC from firmware 1.2.0). The same report bytes as the core's Linux
  `Cm108Ptt`. Serial PTT needs nothing Windows-specific: the core's `SerialPtt` works as it is
  with a `COMn` port.
- `RadioInterfaces.Discover()`: every audio device, with the HID collection and COM port that
  belong to the same physical device (by Windows container ID), so an application can offer
  "AIOC Audio (HID PTT, COM8)" rather than three unrelated lists.
- `EndpointLevel`: the endpoint's own level in dB, capped at 0 dB.
- `EndpointHygiene`: checks, and puts right, the Windows settings that damage a radio interface's
  audio: level above 0 dB, mute, audio enhancements, hardware AGC, hardware gain above 0 dB, an
  open CM108 mic-to-speaker monitor path, and "Listen to this device". Spatial sound is reported
  but cannot be switched off programmatically.

The endpoint rules and the undocumented `IPolicyConfig` and topology interop come from
M0LTE/altmixer, where they were measured against real CM108 and AIOC devices; see its
`docs/windows-audio.md`.

## Licence

**AGPL-3.0-or-later**. It is new code written for a Windows application and derived from nothing
in QtSoundModem or Dire Wolf, so none of the GPL-only files [LICENSING.md](../../LICENSING.md)
lists is in it.

## Builds everywhere, runs on Windows

The project targets plain `net10.0` and marks its Windows-only types with
`[SupportedOSPlatform("windows")]`, so the solution still builds on the Linux CI runners without
Windows targeting packs. Its tests (`tests/Packet.SoundModem.Windows.Tests`) cover only the
device-free logic and run on any platform; the WASAPI and HID paths need a Windows machine with an
interface attached.
