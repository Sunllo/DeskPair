package com.sunllo.deskpair.store

import com.sunllo.deskpair.Target
import com.sunllo.deskpair.transport.Ports

/**
 * What the user typed, and which of the two ways to reach it that means.
 *
 * The home screen has one text box, the way the desktop's does, so something has to decide whether
 * "192.168.1.5" is an address and "123456789" is an id. That decision is
 * `PeerConnector.IsDirectTarget` (Core/Transport/PeerConnector.cs:65) and it is ported here rather than
 * re-invented: the two clients disagreeing about what a string means is the kind of bug that only shows up
 * on someone else's network.
 */
public object Targets {

    /**
     * True if this should be dialled as an address rather than looked up as an id.
     *
     * The order matters and is not obvious: **all-digits is tested first and wins**, because a nine-digit id
     * is a perfectly good integer and integer-form IPv4 parsing would swallow it. The C# comment says the
     * same thing, which is why it is repeated here.
     */
    public fun isDirect(target: String): Boolean {
        val t = target.trim()
        if (t.isEmpty() || t.all { it in '0'..'9' }) {
            return false
        }

        if (t.startsWith('[') || t.count { it == ':' } > 1) {
            return true // IPv6 literal, bracketed or bare
        }

        if (t.contains(':')) {
            return true // host:port or ipv4:port
        }

        return t.contains('.') && isIPv4Literal(t)
    }

    /**
     * A dotted quad, strictly: four parts, each 0..255, no leading zeros.
     *
     * .NET's `IPAddress.TryParse` is the reference, and it is stricter than it used to be — leading zeros
     * have been rejected since .NET 5. It still accepts some shorthand forms this does not, so a bare "10.1"
     * is a direct target on the desktop and an id here. Nobody types that, and erring towards "ask the
     * rendezvous server" fails with a message rather than by connecting somewhere unexpected.
     */
    private fun isIPv4Literal(value: String): Boolean {
        val parts = value.split('.')
        if (parts.size != 4) {
            return false
        }

        return parts.all { part ->
            part.isNotEmpty() && part.length <= 3 && part.all { it in '0'..'9' } &&
                (part.length == 1 || part[0] != '0') &&
                part.toInt() <= 255
        }
    }

    /** Splits `host:port`, or an id, into its parts. IPv6 literals keep their brackets off the host. */
    public fun splitAddress(target: String, defaultPort: Int = Ports.DIRECT_ACCESS): Pair<String, Int> {
        val t = target.trim()
        if (t.startsWith('[')) {
            val close = t.indexOf(']')
            if (close > 0) {
                val host = t.substring(1, close)
                val rest = t.substring(close + 1)
                val port = rest.removePrefix(":").toIntOrNull() ?: defaultPort
                return host to port
            }
        }

        // A bare IPv6 literal has several colons and no port; only a single colon separates a port.
        if (t.count { it == ':' } == 1) {
            val host = t.substringBefore(':')
            val port = t.substringAfter(':').toIntOrNull() ?: defaultPort
            return host to port
        }

        return t to defaultPort
    }

    /**
     * How a target is keyed for pinning: the normalised `host:port`, matching `TofuIdentityVerifier`'s
     * constructor argument on the desktop (`$"{host}:{port}"`, with the default port already filled in).
     * An id is its own key and is never pinned — those go through the server's signature instead.
     */
    public fun pinKey(target: String): String {
        if (!isDirect(target)) {
            return target.trim()
        }

        val (host, port) = splitAddress(target)
        return "$host:$port"
    }

    /**
     * Nine digits as `XXX XXX XXX`, and anything else untouched.
     *
     * Same rule as `HomeViewModel.IdText` and `DeviceRowViewModel.Format` on the desktop — only a string
     * that is exactly nine ASCII digits is grouped — so a value copied from the desktop's display pastes
     * back in here and still means the same thing.
     */
    public fun formatId(id: String): String =
        if (id.length == 9 && id.all { it in '0'..'9' }) {
            "${id.substring(0, 3)} ${id.substring(3, 6)} ${id.substring(6)}"
        } else {
            id
        }

    /**
     * What to actually dial. The desktop strips spaces before connecting for exactly this reason, so the
     * grouped display form round-trips.
     */
    public fun normalise(typed: String): String = typed.replace(" ", "").trim()

    /**
     * Turns what the user typed into a target, settings and all.
     *
     * Lives here rather than in each app so the two cannot drift: the decision, the port default, the
     * base64 decode and the fallback are one piece of code with one set of tests. It also keeps the awkward
     * shapes out of Swift — a Kotlin `Pair` and a `ByteArray` are both unpleasant across the ObjC bridge,
     * and neither has to cross it now.
     *
     * An empty or unusable server key is not refused: `PeerConnector` logs a warning and verifies nothing,
     * which is the desktop's behaviour too. The settings screen is where that gets said out loud.
     */
    public fun resolve(
        typed: String,
        rendezvousServer: String,
        serverPublicKeyBase64: String,
        forceRelay: Boolean,
    ): Target {
        val target = normalise(typed)
        if (isDirect(target)) {
            val (host, port) = splitAddress(target)
            return Target.ByAddress(host, port)
        }

        val key = try {
            ServerKey.decode(serverPublicKeyBase64)
        } catch (_: IllegalArgumentException) {
            null
        }

        return Target.ById(
            id = target,
            rendezvousServer = rendezvousServer,
            serverPublicKeySpki = key ?: ByteArray(0),
            forceRelay = forceRelay,
        )
    }
}
