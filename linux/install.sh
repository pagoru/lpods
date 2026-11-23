#!/usr/bin/env bash
# Builds lpods and installs it for the current user: ~/.local/lib/lpods, ~/.local/bin/lpods and a systemd user service.
# Needs the .NET 10 SDK, Node.js and the window libraries.
# Fedora: sudo dnf install dotnet-sdk-10.0 nodejs npm gtk3 webkit2gtk4.1 pulseaudio-utils
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
prefix="${PREFIX:-$HOME/.local}"

command -v dotnet >/dev/null || { echo "dotnet not found. Fedora: sudo dnf install dotnet-sdk-10.0" >&2; exit 1; }
command -v pactl >/dev/null || echo "note: pactl not found (Fedora: sudo dnf install pulseaudio-utils); audio profile features need it." >&2

if [ ! -d "$here/../src/WPods/web" ]; then
    command -v npm >/dev/null || { echo "npm not found (needed once to build the web pages). Fedora: sudo dnf install nodejs npm" >&2; exit 1; }
    (cd "$here/../ui" && npm ci --no-audit --no-fund && node build.mjs)
fi

dotnet publish "$here/LPods/LPods.csproj" -p:UseAppHost=true -c Release --nologo -o "$prefix/lib/lpods"
mkdir -p "$prefix/bin"
ln -sf "$prefix/lib/lpods/lpods" "$prefix/bin/lpods"

# Icon and launcher entry.
install -Dm0644 "$here/packaging/io.github.pagoru.LPods.svg" "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/scalable/apps/io.github.pagoru.LPods.svg"
install -Dm0644 "$here/packaging/io.github.pagoru.LPods.desktop" "${XDG_DATA_HOME:-$HOME/.local/share}/applications/io.github.pagoru.LPods.desktop"
sed -i "s|^Exec=lpods|Exec=$prefix/bin/lpods|" "${XDG_DATA_HOME:-$HOME/.local/share}/applications/io.github.pagoru.LPods.desktop"

unit_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
mkdir -p "$unit_dir"
sed "s|%h/.local/bin/lpods|$prefix/bin/lpods|" "$here/packaging/lpods.service" > "$unit_dir/lpods.service"
systemctl --user daemon-reload
systemctl --user enable --now lpods.service
systemctl --user restart lpods.service

echo "Installed. Try: lpods status"
