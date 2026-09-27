# Deploying DeskPair

The servers install one role at a time. Roles may share a machine or not; nothing here assumes either.

| Role | Ports | State |
|---|---|---|
| `rendezvous` | 21116 tcp+udp (signalling), 21115 tcp (NAT test), 21114 tcp (admin) | `server.key`, `peers.db` |
| `relay` | 21117 tcp+udp (udp carries media), 21114 tcp (admin) | none |

## The one thing to get right first: DNS

**Never put the rendezvous or the relay behind an HTTP proxy** such as Cloudflare's orange cloud. The proxy
handles HTTP/HTTPS on a fixed set of ports and does not carry UDP at all, while signalling is UDP on 21116 and
relayed media is UDP on 21117.

Worse than the ports: the rendezvous decides NAT type and hole punching from the **source address and port
it observes**. Anything that rewrites those makes every peer look symmetric, and every connection that would
have gone peer to peer falls back to the relay instead. That is the same reason the compose file uses host
networking rather than a bridge.

| Name | Type | Value | Proxy |
|---|---|---|---|
| `rdv.example.com` | A | the rendezvous host | **DNS only** |
| `relay.example.com` | A | the relay host | **DNS only** |

These records expose the origin address. That is not a leak to be fixed — a peer has to reach the signalling
server directly for any of this to work.

## systemd

```sh
# on a build machine, per role
dotnet publish src/DeskPair.Rendezvous -c Release -r linux-x64 --self-contained -o out/rendezvous
tar czf deskpair-rendezvous.tgz -C out/rendezvous .

scp deskpair-rendezvous.tgz rendezvous.json install.sh root@HOST:/tmp/
ssh root@HOST 'bash /tmp/install.sh rendezvous'
```

`install.sh` takes one role and touches only that role: its own unit, its own directory under
`/opt/deskpair/<role>`, its own state under `/var/lib/deskpair/<role>`. Restarting or upgrading one leaves
the others running, which the version this replaced could not do — it carried both servers in one tarball
and enabled them together.

**The rendezvous signing key (`/var/lib/deskpair/rendezvous/server.key`) must survive every install.**
Every client pins it — a directory that hands out the address and key, and every phone with the address typed
into its settings — and a server that starts without it mints a new one, after which those clients refuse every
host with "signature … does not verify" until each is updated by hand. `install.sh` therefore carries an
old-layout key over and otherwise refuses to run when the key is missing; a genuinely new server is installed
with `DESKPAIR_NEW_KEY=yes`. Keep a copy of the key off the machine, and after any key change update whatever
hands the key out and tell everyone with a typed-in server.

Copy `<role>/appsettings.example.json` to `<role>.json` and fill it in. Every `Admin:ApiKey` **must** be
set: with it empty, or still `CHANGE-ME`, the admin API answers 401 to everything (only a `Development`
environment is let through without one) and the service logs an error at start-up. `Admin:BindAddress`
picks the interface the HTTP port listens on. The rendezvous example leaves it empty, so that `/key` can be
served to clients that fetch it (see Firewall for when they do); the relay example sets `127.0.0.1`,
because when it shares the machine the only caller of its admin API is the rendezvous next door -- give it
the LAN address instead if the two are ever split.

`Rendezvous:Report` is optional: with a `Url` set, the rendezvous posts its counts there once a minute -- hosts
online, connections and how many went through a relay, the relays' own totals -- with `ApiKey` as a bearer
token. Counts only; nothing in a report names a person, an address or a computer.

## Relay tickets (protocol 2)

Since protocol 2 a relay pairs nothing without a ticket signed by the rendezvous server: set
`Relay:RendezvousPublicKey` to that server's `/key` (the same base64 clients pin). Without it the relay
logs an error at start-up and refuses every request, on TCP and on the UDP media port alike.
`Relay:RequireTicket=false` restores the old any-two-sockets behaviour for the window in which peers that
predate tickets are still being updated, and for nothing after that. A rendezvous key rotation therefore
has to reach the relay's settings too, or every relayed connection dies with "ticket signature does not
verify" in the relay's journal.

## Verifying

Where 21114 is closed to the outside (see Firewall), run the first three on the server itself with
`127.0.0.1` for HOST.

```sh
curl http://HOST:21114/healthz                                     # rendezvous
curl http://HOST:21114/key                                         # the key clients pin
curl -H "Authorization: Bearer KEY" http://HOST:21114/api/stats     # includes each relay's health and load
curl http://HOST:21124/healthz                                     # relay, when it shares the machine
journalctl -u deskpair-rendezvous -u deskpair-relay -f
```

The rendezvous `/api/stats` lists every configured relay with `healthy`, `load` and when it last answered.
A relay that never answers is the usual sign that `StatsAddress` points at the wrong port — the relay's
admin port moves to 21124 when it shares a machine with the rendezvous, because 21114 is already taken.

## Firewall

Only 21114 and 21124 are administrative, and they are the two to close to everything but the machines that
poll them. Until there is TLS in front of them, their bearer keys cross the network in cleartext -- which
is why the relay's port binds loopback by default.

Whether 21114 is also open to everyone depends on how clients learn the server's key. `/key` is there for a
**self-hosted** server whose users type its address in: the desktop's "Fetch from server" and the phones'
key button ask `http://<address>:21114/key`, and a desktop with an address but no key asks once by itself.
Such a server opens 21114; `/api/*` stays behind its key either way.

A deployment whose clients never type an address -- because a directory hands them the address and the key
together -- has no audience for `/key`, and keeps 21114 closed twice: the firewall does not let it in, and the
rendezvous sets `Admin:BindAddress` to `127.0.0.1`, so a firewall rule going missing does not open it either.
That does not make the server's address a secret -- the name resolves publicly and every connection shows it --
it only stops handing out what nobody outside needs.
