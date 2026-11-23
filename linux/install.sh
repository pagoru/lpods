#!/usr/bin/env bash
# Builds lpods and installs it for the current user: ~/.local/lib/lpods, ~/.local/bin/lpods and a systemd user service.
# Needs the .NET 10 SDK (Fedora: sudo dnf install dotnet-sdk-10.0).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
prefix="${PREFIX:-$HOME/.local}"

command -v dotnet >/dev/null || { echo "dotnet not found. Fedora: sudo dnf install dotnet-sdk-10.0" >&2; exit 1; }
command -v pactl >/dev/null || echo "note: pactl not found (Fedora: sudo dnf install pulseaudio-utils); audio profile features need it." >&2

dotnet publish "$here/LPods/LPods.csproj" -c Release --nologo -o "$prefix/lib/lpods"
mkdir -p "$prefix/bin"
ln -sf "$prefix/lib/lpods/lpods" "$prefix/bin/lpods"

unit_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
mkdir -p "$unit_dir"
sed "s|%h/.local/bin/lpods|$prefix/bin/lpods|" "$here/packaging/lpods.service" > "$unit_dir/lpods.service"
systemctl --user daemon-reload
systemctl --user enable --now lpods.service
systemctl --user restart lpods.service

echo "Installed. Try: lpods status"
