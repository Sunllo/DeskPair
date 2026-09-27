# NAT traversal test matrix

How a controller reaches a host by id (`PeerConnector.ConnectAsync`):

1. **NAT classification** (`NatTypeDetector`): two TCP connections from the same local port to the
   rendezvous NAT-test port (21115) and signalling port (21116); equal observed ports = asymmetric
   (cone) NAT, different = symmetric. Cached 10 minutes. Skipped when `ForceRelay` is set.
2. **Punch request** over TCP from a reusable local port *Q*; the server records the controller's public
   `addr:Q`. The server then:
   - same public IP as the host → `FetchLocalAddr` to the host, which answers `LocalAddr` (its LAN
     address and a fresh listening port) → controller gets `PunchHoleResponse{is_local}` and connects
     within 1.5 s;
   - otherwise → `PunchHole` to the host (UDP push). The host binds port *P*, opens a listener on *P*,
     sends a 30 ms warm-up SYN from *P* to the controller (creates its NAT mapping), reports
     `PunchHoleSent` over a TCP connection from *P* (the server observes the host's public `addr:P`), then
     races accept-on-*P* against connect-from-*P* to the controller for 8 s;
   - the host answers `RelayResponse` instead when either NAT is symmetric (or `PreferRelay`).
3. The controller connects from *Q* to the host's address (6 s; 1.5 s when a side is symmetric or the
   address is local). On failure it re-requests with `force_relay` and goes through the relay.
4. Direct targets (`ip[:port]`, `host:port`) skip the server and pin the host key on first use
   (`known_hosts.json`); a changed key aborts the handshake.

Automated coverage (`NatTraversalTests`, in-process servers on loopback): NAT detection, LAN offer,
TCP hole punch (`AlwaysPunch`), symmetric → relay answer, failed direct attempt → relay fallback,
forced relay, unanswered punch → `OFFLINE`.

## Manual matrix

Record the result of each cell as the transport shown in the session toolbar (`Lan`, `PunchedTcp`,
`Relay`, `DirectTcp`) and the time to first frame.

| Controller ↓ / Host → | Home NAT (cone) | Symmetric (mobile hotspot, CGNAT) | Same LAN | Public IP / port-forwarded 21118 |
|---|---|---|---|---|
| Home NAT (cone) | expect `PunchedTcp` | expect `Relay` | expect `Lan` | `PunchedTcp` (by id) / `DirectTcp` (by address) |
| Symmetric | expect `Relay` | expect `Relay` | expect `Lan` | `PunchedTcp` or `Relay` |
| Same LAN | `Lan` | `Lan` | `Lan` | `Lan` |
| Corporate (TCP egress only) | `PunchedTcp` or `Relay` | `Relay` | `Lan` | `DirectTcp` |

Platforms to cross: Windows ↔ Windows, Windows ↔ Linux, Windows ↔ macOS, Linux ↔ macOS.

Checks per cell:

- the session toolbar shows the expected transport and RTT;
- `DeskPair --server --verbose` logs `NAT type:` at start and the punch/LAN/relay
  decision per connection;
- relay fallback happens within ~8 s when direct paths are blocked (drop inbound TCP on the host NAT);
- a Docker-hosted rendezvous must run with `network_mode: host`, otherwise the bridge rewrites the
  observed source ports and every peer looks symmetric.
