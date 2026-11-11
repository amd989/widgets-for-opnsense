# Packages a tree made by linux/packaging/stage.sh; nothing is compiled here.
#   rpmbuild -bb --target x86_64|aarch64 --define "_version 1.2.0" --define "_stageroot /path" opnsense-widgets.spec
# The daemon is a .NET single-file bundle: stripping it corrupts the bundle, so the
# post-install processing (strip, debuginfo) is turned off.
%global debug_package %{nil}
%global __os_install_post %{nil}
%global _build_id_links none

Name:           opnsense-widgets
Version:        %{_version}
Release:        1%{?dist}
Summary:        Widgets for OPNsense on KDE Plasma and GNOME
License:        MIT
URL:            https://github.com/amd989/widgets-for-opnsense

# Loaded at run time by the .NET runtime, so not found by the automatic dependencies.
Requires:       openssl-libs
Requires:       libicu
Requires:       zlib
Recommends:     qt6-qtwebengine
Recommends:     libsecret
Recommends:     alejandro-md-release

%description
Shows devices with DHCP leases, live traffic and firewall activity of an
OPNsense router on the Linux desktop: three KDE Plasma 6 widgets and a
GNOME Shell 46+ top bar indicator with new-device notifications. A small
user service, opnsense-widgetsd, talks to the router API and feeds both.

Not affiliated with Deciso B.V. or the OPNsense project.

%prep

%build

%install
cp -a %{_stageroot}/. %{buildroot}/

%post
if [ $1 -eq 1 ]; then
    # First install: start the user service in every graphical session from now on.
    systemctl --global enable opnsense-widgetsd.service >/dev/null 2>&1 || :
    echo "opnsense-widgets: log out and in again (or run: systemctl --user start opnsense-widgetsd),"
    echo "  then add the KDE widgets or enable the GNOME extension (Extensions app)."
fi

%preun
if [ $1 -eq 0 ]; then
    %{_prefix}/lib/opnsense-widgets/user-managers stop opnsense-widgetsd.service
    systemctl --global disable opnsense-widgetsd.service >/dev/null 2>&1 || :
fi

%postun
if [ $1 -ge 1 ]; then
    # Upgrade: sessions running the service pick up the new daemon now.
    %{_prefix}/lib/opnsense-widgets/user-managers daemon-reload
    %{_prefix}/lib/opnsense-widgets/user-managers try-restart opnsense-widgetsd.service
fi

%files
%license %{_docdir}/%{name}/LICENSE
%{_prefix}/lib/opnsense-widgets/
%{_prefix}/lib/systemd/user/opnsense-widgetsd.service
%{_datadir}/plasma/plasmoids/md.alejandro.opnsensewidgets.dhcp/
%{_datadir}/plasma/plasmoids/md.alejandro.opnsensewidgets.traffic/
%{_datadir}/plasma/plasmoids/md.alejandro.opnsensewidgets.firewall/
%{_datadir}/gnome-shell/extensions/opnsense-widgets@alejandro.md/
%{_datadir}/glib-2.0/schemas/org.gnome.shell.extensions.opnsense-widgets.gschema.xml
