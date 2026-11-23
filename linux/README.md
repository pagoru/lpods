# LPods: WPods on Linux

A Linux port of [WPods](https://github.com/FPNDev/wpods). It runs upstream's AirPods protocol code and web pages
unchanged (`src/WPods/Core`, `src/WPods/Nearby`, `ui/`) on a Linux platform layer that talks to BlueZ, PipeWire and
the desktop. Developed and tested for Fedora; any distribution with BlueZ, PipeWire, GTK 3, WebKitGTK 4.1 and .NET 10
should work.

> LPods is not made, affiliated with, sponsored, endorsed or supported by Apple Inc. GPL-3.0, like upstream.

## Status

Everything below is covered by tests against mocked BlueZ, D-Bus and notification services. None of it has run on
real AirPods yet.

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
| Window with upstream's pages (battery, noise control, all settings, languages) | Yes |
| Tray icon with battery and menu | Yes (GNOME needs the AppIndicator extension) |
| Nearby AirPods offered as a notification with a Connect button | Yes |
| Pause media when an AirPod comes out (MPRIS), conversation ducking, low battery notification | Yes |
| Stereo mic over A2DP, HD voice, mic modes, voice enhancement, mixer, low latency, hotkeys, auto update | No ([notes](docs/stereo-mic.md)) |

## Install (Fedora)

From an RPM (built by CI, see the Actions artifacts of the Linux workflow):

```sh
sudo dnf install ./lpods-*.noarch.rpm
systemctl --user enable --now lpods.service
```

From source:

```sh
sudo dnf install dotnet-sdk-10.0 nodejs npm gtk3 webkit2gtk4.1 pulseaudio-utils
git clone https://github.com/pagoru/lpods && cd lpods
./linux/install.sh
```

The script builds the web pages and LPods, installs them to `~/.local/lib/lpods` with `~/.local/bin/lpods`, adds the
launcher entry and icon, and starts LPods as a systemd user service (`systemctl --user status lpods`).

Pair your AirPods in GNOME Settings > Bluetooth first. LPods attaches to them whenever they connect.

**GNOME tray:** GNOME has no tray of its own. Install `gnome-shell-extension-appindicator` and enable it, and the LPods
icon appears. Without it, LPods still works: open the window from the launcher (or `lpods open`), and nearby offers and
low battery warnings are notifications.

## Use

```sh
lpods                              # open the window (starts LPods if it is not running)
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
| `TrayApp`, `WebPane` (WinForms + WebView2) | `LPods/Ui`: GTK 3 and WebKitGTK 4.1 by P/Invoke, StatusNotifierItem tray, notifications |
| `EarPause`, `ConversationDucker` (Windows media keys, Core Audio) | MPRIS, `pactl` volume |

The window loads the unchanged pages of `ui/` through a `lpods://` URL scheme. `Ui/linux.js` provides the
`window.chrome.webview` bridge the pages expect and tells them the mic is Hands-Free only; `Ui/linux.css` hides
the controls that have no effect on Linux (Hands-Free, HD voice, low latency, mic modes, mixer, hotkeys, updates). When
upstream changes those pages, check that both still fit. Labels say Linux and LPods through `Platform/LinuxLabels.cs`;
the legal texts keep naming WPods and its author.

Settings and logs use upstream's format and live in `~/.local/share/WPods`.

## Develop

```sh
(cd ui && npm ci && node build.mjs)   # the web pages, once; the window needs them
cd linux
dotnet build
dotnet test --solution LPods.slnx     # sudo dnf install python3-dbusmock python3-gobject dbus-daemon
```

The tests run upstream's `AapSession` against a fake AirPods over the Linux bridge, the BlueZ layer against
python-dbusmock's BlueZ template on a private bus, and the tray and notifications against a StatusNotifier host and a
notification daemon that read and click them. Without those Python packages the D-Bus tests are skipped.

To look at the window without AirPods: run `lpods open` under Xvfb (or your desktop); with no paired device it shows
the pairing guide.

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
- The window cannot place itself on Wayland, so it opens where the compositor puts it and remembers only its height.
- Without a tray host the Close button hides the window and LPods keeps running in the background.
- Non-English labels get "Linux" and "LPods" swapped in for "Windows" and "WPods" mechanically; a few sentences read
  slightly off until they are translated properly.
