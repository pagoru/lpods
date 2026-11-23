# LPods: WPods on Linux

A Linux port of [WPods](https://github.com/FPNDev/wpods). It runs upstream's AirPods protocol code unchanged
(`src/WPods/Core`, `src/WPods/Nearby`) on a Linux platform layer that talks to BlueZ and PipeWire. Developed and
tested for Fedora; any distribution with BlueZ, PipeWire and .NET 10 should work.

> LPods is not made, affiliated with, sponsored, endorsed or supported by Apple Inc. GPL-3.0, like upstream.

## Status

This is the first, command-line stage. There is no window or tray yet.

| Feature | Linux |
| --- | --- |
| Battery (left, right, case), charging, ear detection state | Yes |
| Noise control (Off, Transparency, Adaptive, Noise Cancellation) | Yes |
| Conversation awareness, Personalized Volume, one-bud ANC, allow Off | Yes |
| Press speed, hold duration, press-and-hold cycle, tone volume, mic side | Yes |
| Hearing Aid, Media Assist, heart rate (models that support them) | Yes |
| Follow the default output to another pair | Yes |
| Nearby detection (proximity adverts) | Yes (see known issues) |
| Waybar / status bar module | Yes |
| Window, tray, nearby popup | Not yet |
| Pause media when an AirPod comes out, conversation ducking | Not yet |
| Stereo mic, HD voice, mic modes, voice enhancement, mixer, low latency | Not yet |

## Install (Fedora)

```sh
sudo dnf install dotnet-sdk-10.0 pulseaudio-utils
git clone https://github.com/pagoru/lpods && cd lpods
./linux/install.sh
```

This builds LPods, installs it to `~/.local/lib/lpods` with `~/.local/bin/lpods`, and starts it as a systemd user
service (`systemctl --user status lpods`).

Pair your AirPods in GNOME Settings > Bluetooth first. LPods attaches to them whenever they connect.

## Use

```sh
lpods status                       # battery, ear, noise control and settings
lpods noise next                   # or: off | nc | transparency | adaptive
lpods set conversation-awareness off
lpods set stem-modes nc,transparency,adaptive
lpods connect                      # last used pair; pages it if needed
lpods help                         # everything else
```

**Hotkeys:** bind commands in your desktop's keyboard settings. GNOME: Settings > Keyboard > View and Customize
Shortcuts > Custom Shortcuts, command `lpods noise next`. Global key grabs are not possible on Wayland, so LPods
does not grab keys itself.

**Waybar:**

```json
"custom/airpods": {
  "exec": "lpods waybar",
  "return-type": "json",
  "format": "🎧 {}",
  "on-click": "lpods noise next"
}
```

`lpods watch` prints the full status as one JSON line on every change, for other bars and scripts.

## How it works

On Windows, WPods needs a background service that takes over the Bluetooth adapter to reach the AirPods' control
channel (AAP, L2CAP PSM 0x1001). BlueZ lets a normal user open that channel, so LPods has no service. The files in
`LPods/Platform` replace upstream's Windows-only files under the same class names:

| Upstream (Windows) | Linux replacement |
| --- | --- |
| `ServicePipes` (named pipe to the service) | L2CAP socket bridged through a SOCK_SEQPACKET socket pair, so `AapSession` sees the same message framing |
| `BluetoothLink` (config manager notifications) | BlueZ `Device1` objects over D-Bus |
| `NearbyNative` (WinRT BLE watcher) | BlueZ LE discovery, Apple manufacturer data |
| `Native` (DPAPI, device dates, radio notifications) | BlueZ adapter power events; keys stored in a user-only folder |
| `DefaultOutputWatcher` (Windows audio) | `pactl subscribe` / default PipeWire sink |
| `HandsFree`, `ServiceStatus`, `MicCatalog` | Left to WirePlumber; no HD voice; mic ids only |

Settings and logs use upstream's format and live in `~/.local/share/WPods`.

## Develop

```sh
cd linux
dotnet build
dotnet test --solution LPods.slnx     # BlueZ tests need dbus-daemon and python3-dbusmock (sudo dnf install python3-dbusmock)
```

The tests run upstream's `AapSession` against a fake AirPods over the Linux bridge, and the BlueZ layer against
python-dbusmock's BlueZ template on a private bus.

## Keeping up with upstream

`.github/workflows/upstream-sync.yml` checks FPNDev/wpods daily and opens a PR with the new commits. The PR says
whether the Linux build and tests pass with them, and lists the Windows-only files that changed, so you can port
those fixes to `linux/` by hand. Upstream files are never edited here, so the merge itself stays clean.

To turn it on: in the repository's Settings > Actions > General, check "Allow GitHub Actions to create and approve
pull requests".

## Known issues

- Nearby detection runs BlueZ LE discovery continuously. That is an active scan; on some adapters it can cause
  audio stutter. A passive scan (BlueZ advertisement monitors) is planned.
- The proximity keys are stored unencrypted in `~/.local/share/WPods` (made readable only by you). Upstream encrypts
  them with Windows DPAPI.
- Not tested on real AirPods hardware yet. Logs: `~/.local/share/WPods/logs`.
