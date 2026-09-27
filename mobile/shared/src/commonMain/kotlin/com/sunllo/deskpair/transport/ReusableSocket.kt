package com.sunllo.deskpair.transport

/**
 * A TCP connection that chose its own local port, and said which one.
 *
 * Hole punching is built on this. Every socket involved on one side shares a local port: the connection
 * that tells the rendezvous server what our public address looks like, and the attempts that open our NAT
 * toward the peer. The NAT then maps them all to the same public port, which is the whole trick — the peer
 * is told a port that our own outgoing packets have already made live.
 */
internal class ReusableConnection(
    val stream: ByteStream,
    /** The port this connection went out from, to be reused by the attempts that follow. */
    val localPort: Int,
) : AutoCloseable {
    override fun close(): Unit = stream.close()
}

/**
 * Connects from [localPort], or from a fresh port when it is 0, with address and port reuse enabled.
 *
 * Reuse is what lets several sockets share one local port at the same time. Without it the second bind
 * fails and the punch never starts. Platforms disagree about which flag does this — SO_REUSEADDR is
 * everywhere, SO_REUSEPORT is not — so each implementation sets what it has and the caller finds out by
 * trying, which is the only honest way to know.
 */
internal expect suspend fun connectReusable(
    localPort: Int,
    host: String,
    port: Int,
    timeoutMillis: Long,
): ReusableConnection

/** A socket refused to do something. Distinct from a protocol failure: nothing was said yet. */
internal class SocketException(message: String) : Exception(message)
