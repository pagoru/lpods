%global debug_package %{nil}
# The managed build output is not an ELF with a build ID.
%global __os_install_post %{nil}

Name:           lpods
Version:        0.1.1
Release:        1%{?dist}
Summary:        Battery, noise control and ear detection for AirPods
License:        GPL-3.0-only
URL:            https://github.com/pagoru/lpods
Source0:        %{name}-%{version}.tar.gz

BuildArch:      noarch
BuildRequires:  dotnet-sdk-10.0
BuildRequires:  nodejs
BuildRequires:  npm
BuildRequires:  desktop-file-utils
BuildRequires:  systemd-rpm-macros

Requires:       dotnet-runtime-10.0
Requires:       bluez
Requires:       gtk3
Requires:       webkit2gtk4.1
Requires:       xdg-utils
# pactl: moves the AirPods between the audio profiles, ducks the volume on conversation awareness.
Requires:       pulseaudio-utils
Recommends:     pipewire-pulseaudio
# GNOME shows tray icons only with this extension.
Recommends:     gnome-shell-extension-appindicator

%description
LPods talks to AirPods over Bluetooth with Apple's accessory protocol, like an iPhone does. It shows their battery,
switches between noise cancellation, transparency and adaptive, pauses media when an AirPod comes out, lowers the
volume when you start a conversation, and offers to connect AirPods that are nearby.

A Linux port of WPods. Not made, sponsored, endorsed or supported by Apple.

%prep
%autosetup

%build
(cd ui && npm ci --no-audit --no-fund && node build.mjs)
cd linux
dotnet publish LPods/LPods.csproj -c Release --nologo -p:UseAppHost=false -p:DebugType=none -o %{_builddir}/publish

%install
install -d %{buildroot}%{_prefix}/lib/lpods %{buildroot}%{_bindir}
cp -a %{_builddir}/publish/. %{buildroot}%{_prefix}/lib/lpods/
cat > %{buildroot}%{_bindir}/lpods <<'WRAPPER'
#!/bin/sh
exec dotnet /usr/lib/lpods/lpods.dll "$@"
WRAPPER
chmod 0755 %{buildroot}%{_bindir}/lpods
install -Dm0644 linux/packaging/lpods.service %{buildroot}%{_userunitdir}/lpods.service
sed -i 's|%%h/.local/bin/lpods|%{_bindir}/lpods|' %{buildroot}%{_userunitdir}/lpods.service
install -Dm0644 linux/packaging/io.github.pagoru.LPods.desktop %{buildroot}%{_datadir}/applications/io.github.pagoru.LPods.desktop
install -Dm0644 linux/packaging/io.github.pagoru.LPods.svg %{buildroot}%{_datadir}/icons/hicolor/scalable/apps/io.github.pagoru.LPods.svg
install -Dm0644 linux/packaging/io.github.pagoru.LPods.metainfo.xml %{buildroot}%{_metainfodir}/io.github.pagoru.LPods.metainfo.xml

%check
desktop-file-validate %{buildroot}%{_datadir}/applications/io.github.pagoru.LPods.desktop

%files
%license LICENSE
%doc linux/README.md
%{_bindir}/lpods
%{_prefix}/lib/lpods/
%{_userunitdir}/lpods.service
%{_datadir}/applications/io.github.pagoru.LPods.desktop
%{_datadir}/icons/hicolor/scalable/apps/io.github.pagoru.LPods.svg
%{_metainfodir}/io.github.pagoru.LPods.metainfo.xml

%changelog
* Mon Nov 23 2026 LPods maintainers <noreply@github.com> - 0.1.1-1
- Fix a second title bar on KDE.

* Mon Nov 23 2026 LPods maintainers <noreply@github.com> - 0.1.0-1
- First release: battery, noise control, ear detection, media pause, conversation ducking, tray and nearby offers.
