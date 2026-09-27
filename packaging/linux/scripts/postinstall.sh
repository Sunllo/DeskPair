#!/bin/sh
# After the program is installed or upgraded (deb postinst, rpm %post, Arch post_install and post_upgrade).
#
# Unattended access runs its own copy of the program from /opt/deskpair, which a package upgrade does not
# touch. When it is turned on, hand it the version just installed, the same way the app's own updater does, so
# the service and the app never drift apart. Nothing here turns unattended access on.

if [ -f /etc/systemd/system/sunllo-deskpair.service ]; then
    /usr/lib/deskpair/DeskPair --install-service --system-stage ||
        echo "deskpair: the unattended service could not be moved to this version; turn it off and on again in Settings" >&2
fi

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
exit 0
