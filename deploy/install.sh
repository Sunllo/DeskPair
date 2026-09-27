#!/bin/bash
#
# Installs one DeskPair server on this machine.
#
#   ./install.sh rendezvous
#   ./install.sh relay
#
# One role per run, on purpose. The version this replaces took a single tarball holding two servers, wrote
# both unit files from one loop and enabled both together, so there was no way to restart the relay without
# restarting signalling — and no way to move a role to another machine at all. Each role now installs,
# restarts and upgrades on its own, whether or not the others share the box.
#
# Expects, in /tmp:
#   deskpair-<role>.tgz     a self-contained linux-x64 publish of that server
#   <role>.json             its appsettings.json
set -euo pipefail

ROLE="${1:-}"
case "$ROLE" in
  rendezvous) EXE=DeskPair.Rendezvous ;;
  relay)      EXE=DeskPair.Relay ;;
  *) echo "usage: $0 <role>   (see the top of this file)" >&2; exit 2 ;;
esac

USER_NAME=deskpair
ROOT=/opt/deskpair/$ROLE
DATA=/var/lib/deskpair/$ROLE

id -u "$USER_NAME" >/dev/null 2>&1 || useradd -r -s /usr/sbin/nologin -d /var/lib/deskpair "$USER_NAME"
mkdir -p "$ROOT" "$DATA"

# The rendezvous signing key is the one thing on this machine that cannot be regenerated quietly: every
# client that has pinned it -- a directory that hands it out, and every phone with the address typed in --
# refuses every peer until somebody updates it by hand. So: an old-layout key is carried over, and minting a
# new one is never a side effect of an install -- it has to be asked for.
if [ "$ROLE" = rendezvous ] && [ ! -f "$DATA/server.key" ]; then
  for old in /var/lib/deskpair/server.key /var/lib/sunllo-deskpair/server.key; do
    if [ -f "$old" ]; then
      echo "carrying the signing key over from $old"
      cp -p "$old" "$DATA/server.key"
      break
    fi
  done
fi
if [ "$ROLE" = rendezvous ] && [ ! -f "$DATA/server.key" ] && [ "${DESKPAIR_NEW_KEY:-}" != yes ]; then
  echo "no signing key at $DATA/server.key: starting the server would mint a new one, and every client" >&2
  echo "that pinned the old key (a directory that hands it out, every phone with the address typed in) would stop" >&2
  echo "verifying hosts. Restore the key from a backup, or, for a genuinely new server, run again with" >&2
  echo "DESKPAIR_NEW_KEY=yes." >&2
  exit 3
fi

systemctl stop "deskpair-$ROLE" 2>/dev/null || true
rm -rf "${ROOT:?}"/*
tar xzf "/tmp/deskpair-$ROLE.tgz" -C "$ROOT"
cp "/tmp/$ROLE.json" "$ROOT/appsettings.json"
chmod +x "$ROOT/$EXE"

# The data directory holds the rendezvous signing key, which every client pins. It is the one file here
# that cannot be regenerated without every installed client refusing every peer until it is reconfigured.
chown -R "$USER_NAME:$USER_NAME" "$ROOT" "$DATA"
chmod 700 "$DATA"

cat > "/etc/systemd/system/deskpair-$ROLE.service" <<UNIT
[Unit]
Description=DeskPair $ROLE server
After=network-online.target
Wants=network-online.target

[Service]
User=$USER_NAME
WorkingDirectory=$ROOT
ExecStart=$ROOT/$EXE
# Workstation GC: this runs beside other roles on a small machine, and server GC would claim a heap per
# core and per process.
Environment=DOTNET_gcServer=0
# Kestrel binds programmatically from the role's own HttpPort; an inherited URL list would fight it.
Environment=ASPNETCORE_URLS=
Restart=always
RestartSec=3
LimitNOFILE=65536

# It serves the public internet and needs nothing from the rest of the machine.
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$DATA
ProtectKernelTunables=true
ProtectControlGroups=true
RestrictSUIDSGID=true

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now "deskpair-$ROLE"
sleep 2
systemctl --no-pager --lines=0 status "deskpair-$ROLE" | head -5
