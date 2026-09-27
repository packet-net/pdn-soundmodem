# pdn-soundmodem-linux

Linux support for applications that run the pdn-soundmodem core in their own process over a USB
radio interface: the Linux counterpart of `pdn-soundmodem-windows`. Published to NuGet as
`pdn-soundmodem-linux` by the same release as the core, on the same version. The daemon does not use
or reference this library; the core knows nothing about it.

Audio and PTT themselves need nothing new on Linux: the core's `AlsaAudioInput`,
`AlsaAudioOutput`, `Cm108Ptt`, `SerialPtt` and `AlsaMixer` are what a station opens. What an
application needs beyond them is knowing *which* card, hidraw node and serial port to open, whether
it may, and what to do when it may not. That is this library.

## What is here

- `RadioInterfaces.Discover()`: every ALSA sound card, with the CM108-compatible hidraw node and
  the serial port of the same USB device, grouped by the USB device they hang off in sysfs (the
  Linux counterpart of a Windows container ID). An AIOC comes back as one interface: its card
  (`plughw:CARD=AllInOneCable,DEV=0`), `/dev/hidraw0` for PTT and
  `/dev/serial/by-id/usb-AIOC_All-In-One-Cable_...-if04`. Each has a `Key` (USB IDs and serial
  number, or USB port for a dongle with no serial number) for finding it again after card numbers
  have moved.
- `DeviceAccess.Check`: which of the nodes a station needs this process cannot open, and whether
  each is missing or refused, before anything is opened. The usual answer is the hidraw node, which
  is root's alone without a udev rule.
- `CardUsers.Holder`: who has a busy card open ("pipewire (pid 1234)"), from
  `/proc/asound/.../status`, so EBUSY can be explained.
- `PipeWire.NodesFor`: the card's PipeWire source and sink, and the `pipewire:NODE=...` ALSA
  device strings that go through PipeWire when the desktop will not let go of the card.
- `MixerHygiene`: the rules for a radio interface's mixer, as `EndpointHygiene` has them on
  Windows: nothing above 0 dB, AGC and mic boost off, neither side muted, and no input monitored
  to the output (a CM108's "Mic" playback switch, which sends received audio back to the
  transmitter). Controls are found by what they do as well as by name, because an AIOC calls its
  own "AIOC Audio In" and "AIOC Audio Out Volume".
- `AlsaLevel`: a card level in dB with a 0 dB ceiling, noticing changes made in alsamixer and
  putting back any that go above the ceiling.
- `DeviceWatcher`: an event when interfaces are plugged or unplugged (inotify on `/dev`).

## Permissions

A packaged application should install a udev rule that gives the logged-in user (`uaccess`) and
the `audio` group the interface's hidraw node, and its serial port, for example:

```
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0d8c", TAG+="uaccess", GROUP="audio", MODE="0660"
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="1209", ATTRS{idProduct}=="7388", TAG+="uaccess", GROUP="audio", MODE="0660"
SUBSYSTEM=="tty", ATTRS{idVendor}=="1209", ATTRS{idProduct}=="7388", TAG+="uaccess"
```

and a WirePlumber rule that keeps the desktop's sound server off radio interfaces, so that the
card can be opened directly. pdn-lin, the Linux package of pdn-win, ships both.

## Licence

**AGPL-3.0-or-later**, as the Windows library is and unlike the GPL-3.0-or-later core. It is new
code written for the Linux application and derived from nothing in QtSoundModem or Dire Wolf;
GPLv3 section 13 permits the combination.

## Builds everywhere, runs on Linux

The project targets plain `net10.0` and marks its Linux-only entry points with
`[SupportedOSPlatform("linux")]`. Discovery, the access and holder diagnoses and the mixer rules
all read through one seam (`IDeviceTree`, `IAlsaMixer`), so the tests
(`tests/Packet.SoundModem.Linux.Tests`) run against written sysfs trees and fake cards on any
platform; the real paths need a Linux machine with an interface attached.
