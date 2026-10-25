# Ambient RGB

Lightweight screen- and audio-reactive ambient lighting for Windows, driving your RGB hardware through [OpenRGB](https://openrgb.org).

A tray app that uses ~75 MB of RAM and practically no CPU — written as a replacement for SignalRGB, which used ~900 MB on the same machine.

- **Calm background color** by default (green out of the box, any color you pick).
- **Follows the screen in fullscreen games and video**: the lights take the dominant vivid color on screen. Gray or dark scenes keep the background color, and the desktop is ignored.
- **Flashes on sudden loud sounds** — gunshots, explosions, hits.
- **Optional music pulse**: brightness breathes with the beat.
- Works with **every device OpenRGB exposes**: motherboard and its ARGB/12V headers, GPU, RAM, coolers, strips…
- Optional, experimental: **AULA Hero HE keyboards** over USB cable.

## How it works

| Part | Approach |
|---|---|
| Screen | DXGI Desktop Duplication of the primary monitor. The frame is shrunk on the GPU with a mip chain, so the CPU reads only ~60×25 pixels. A hue histogram weighted by saturation picks the dominant *vivid* color. |
| Fullscreen detection | The foreground window covers the whole primary monitor (exclusive or borderless fullscreen, fullscreen video). |
| Audio | Peak meter of the default playback device (`IAudioMeterInformation`) — no capture stream. A fast vs. slow envelope detects transients; a short-window envelope drives the music pulse. |
| Color | Mixed in linear light (gamma 2.2), so a color on the LEDs looks like the same color on screen. |
| Output | OpenRGB SDK protocol over TCP `127.0.0.1:6742` (protocol v3), devices switched to Direct mode. |
| Build | C# 5 / .NET Framework 4.8, compiled with the `csc.exe` that ships with Windows. No NuGet, no SDK, no runtime to install. |

## Requirements

- Windows 10 or 11, x64 (.NET Framework 4.8 is preinstalled).
- **OpenRGB 1.x** with the SDK server running on port 6742. The OpenRGB MSI installs a Windows service that starts at boot and runs the server — that's all you need; the OpenRGB window can stay closed.
- **[PawnIO](https://pawnio.eu)** if you want RAM (and other SMBus devices) to light up. OpenRGB needs it for SMBus access.
- No other RGB software controlling the same devices (SignalRGB, Armoury Crate lighting, iCUE, …) — they fight over the hardware.

## Build and install

```powershell
.\build.ps1            # builds bin\AmbientRGB.exe and bin\AmbientRGB-diag.exe
.\build.ps1 -Tools     # also builds the developer tools (see below)
.\install.ps1          # copies to %LOCALAPPDATA%\AmbientRGB, enables autostart, starts it
```

The app lives in the tray as a dot showing the current color. Click it for settings; right-click for Pause / Exit.

**Uninstall:** untick *Start with Windows* in settings, exit from the tray, delete `%LOCALAPPDATA%\AmbientRGB`.

## Settings

| Setting | Meaning |
|---|---|
| Background color | The calm color shown when nothing happens. |
| Brightness | Brightness of the background. |
| Screen reaction | How strongly vivid screen colors replace the background (0 = off). |
| Smoothing | How smoothly colors change. |
| Audio sensitivity | Flash trigger sensitivity (0 = off). |
| Flash strength / Flash color | How far and toward which color a flash pushes. |
| Music pulse | Brightness pulsing with the beat (0 = off). |
| Follow the screen only in fullscreen apps | On by default, so the desktop and normal windows never tint the lights. |
| Pause | Background color only. |
| AULA keyboard | Experimental, USB cable only (see Limitations). |

Settings are stored in `%LOCALAPPDATA%\AmbientRGB\settings.ini`, and the log is `ambientrgb.log` next to it.

## ARGB headers

OpenRGB reports **0 LEDs** on addressable headers until you tell it how long the strip is. Set the size in the OpenRGB window (device → zone → *Resize*) or with the tool below. OpenRGB remembers it. Overestimating is harmless.

## Diagnostics and tools

- `AmbientRGB-diag.exe [seconds] [--selftest]` prints what the engine sees and sends. `--selftest` briefly shows full-screen red, blue and gray and plays three quiet noise bursts.
- `.\build.ps1 -Tools` builds the developer tools:
  - `ListDevices.exe` lists OpenRGB devices, zones and current colors; `ListDevices.exe resize <device> <zone> <leds>` sets an ARGB header size.
  - `AudioProbe.exe [seconds]` prints the audio envelopes. It doesn't touch the lights.
  - `ScreenProbe.exe` prints the screen color the engine would pick. It doesn't touch the lights.

## Limitations

- Primary monitor only.
- One color for all LEDs (no per-LED / spatial mapping yet).
- Some anti-cheat systems may dislike low-level hardware drivers. That's about PawnIO/OpenRGB, not this app.
- **AULA Hero HE keyboards:** the 2.4G receiver firmware doesn't forward lighting commands, so only a USB cable connection can work. This path is untested on real hardware and is off by default.

## Credits

- [OpenRGB](https://openrgb.org) for the hardware support and the SDK protocol.
- The AULA / BY Tech keyboard packet format was learned from the publicly available SignalRGB device plugin for these keyboards.

## License

[MIT](LICENSE)
