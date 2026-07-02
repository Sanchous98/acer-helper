# RPM for AcerHelper — packages the prebuilt self-contained Linux binary plus the udev/tmpfiles rules that
# grant the "wheel" group access to the root-only laptop-control sysfs knobs, so the app runs unprivileged.
# The package IS the installer: %post reloads udev + applies tmpfiles; dnf remove is the uninstaller.
#
# Built in CI from the `dotnet publish -r linux-x64 --self-contained` output (see .github/workflows):
#   Source0 = tarball of the publish tree; Source1..3 = the packaging files from this dir.
#   rpmbuild -bb --define "appversion <ver>" packaging/acer-helper.spec

%global appver %{?appversion}%{!?appversion:0.14.0}
%global debug_package %{nil}
# Self-contained binary: don't scan the bundled .so's for (bogus) library deps/provides.
AutoReqProv: no

Name:           acer-helper
Version:        %{appver}
Release:        1%{?dist}
Summary:        Tray app to control Acer/Dell laptop hardware (profiles, fans, battery, lighting)
License:        MIT
URL:            https://github.com/Sanchous98/acer-helper
ExclusiveArch:  x86_64

Source0:        acer-helper-%{appver}-linux-x64.tar.gz
Source1:        60-acer-helper.rules
Source2:        acer-helper.conf
Source3:        acer-helper.desktop

BuildRequires:  systemd-rpm-macros

%description
AcerHelper is an Avalonia tray application that controls laptop firmware features through the
platform's native hardware interfaces (Linux sysfs / kernel classes; Windows WMI) — performance
profiles, fans, sensors, battery charge mode/limit, keyboard backlight and RGB lighting, USB
charging and more. It adapts to whatever the detected vendor (Acer, Dell, or a generic laptop)
exposes. This package installs udev + tmpfiles rules so the root-only control nodes are writable
by the "wheel" group, letting the app run without root.

%prep
# nothing to build — Source0 is the prebuilt publish tree

%build

%install
install -d %{buildroot}%{_libexecdir}/%{name}
tar -xzf %{SOURCE0} -C %{buildroot}%{_libexecdir}/%{name}
install -d %{buildroot}%{_bindir}
# Relative symlink (../libexec/acer-helper/AcerHelper) — avoids rpm's absolute-symlink warning.
ln -sr %{buildroot}%{_libexecdir}/%{name}/AcerHelper %{buildroot}%{_bindir}/%{name}
install -Dpm0644 %{SOURCE1} %{buildroot}%{_udevrulesdir}/60-acer-helper.rules
install -Dpm0644 %{SOURCE2} %{buildroot}%{_tmpfilesdir}/acer-helper.conf
install -Dpm0644 %{SOURCE3} %{buildroot}%{_datadir}/applications/acer-helper.desktop

%post
# Apply the permission rules immediately (also applied automatically on the next boot/hotplug).
# On atomic/rpm-ostree systems this runs in the compose chroot with no live system, so these no-op (guarded
# by `|| :`); the rules then take effect on the reboot rpm-ostree requires to apply the layered package.
systemd-tmpfiles --create %{_tmpfilesdir}/acer-helper.conf >/dev/null 2>&1 || :
udevadm control --reload-rules >/dev/null 2>&1 || :
udevadm trigger --subsystem-match=power_supply --subsystem-match=leds --subsystem-match=platform-profile >/dev/null 2>&1 || :

%postun
# On full removal, drop the relaxed permissions by reloading/triggering without our rules.
if [ $1 -eq 0 ]; then
    udevadm control --reload-rules >/dev/null 2>&1 || :
    udevadm trigger --subsystem-match=power_supply --subsystem-match=leds --subsystem-match=platform-profile >/dev/null 2>&1 || :
fi

%files
%{_libexecdir}/%{name}/
%{_bindir}/%{name}
%{_udevrulesdir}/60-acer-helper.rules
%{_tmpfilesdir}/acer-helper.conf
%{_datadir}/applications/acer-helper.desktop

%changelog
* Thu Jul 02 2026 Andrea Palladio <andrea.palladio@kiv.dev> - 0.14.0-1
- Initial RPM: bundles the udev/tmpfiles permission rules with the app.
