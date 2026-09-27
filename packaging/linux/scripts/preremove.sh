#!/bin/sh
# Before the program is removed (deb prerm, rpm %preun, Arch pre_remove) -- but not when it is only being
# upgraded, which each package manager says differently: deb passes "upgrade" (or "deconfigure",
# "failed-upgrade"), rpm the number of versions left afterwards (0 on removal), and Arch calls this on removal
# only, with the version being removed.
#
# Removing DeskPair takes the unattended service with it: what would be left could still be reached from the
# network, and there would no longer be an app on this machine to turn it off from.

case "$1" in
    upgrade | failed-upgrade | deconfigure) exit 0 ;; # deb: an upgrade, not a removal
    *[!0-9]* | "" | 0) ;;                             # deb "remove", Arch's version string, rpm's last version
    *) exit 0 ;;                                      # rpm: other versions remain, so this is an upgrade
esac

if [ -f /etc/systemd/system/sunllo-deskpair.service ] && [ -x /usr/lib/deskpair/DeskPair ]; then
    /usr/lib/deskpair/DeskPair --uninstall-service ||
        echo "deskpair: the unattended service is still installed; remove it with: sudo /opt/deskpair/DeskPair --uninstall-service" >&2
fi
exit 0
