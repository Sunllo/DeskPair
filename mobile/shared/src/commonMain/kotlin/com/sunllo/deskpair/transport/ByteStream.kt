package com.sunllo.deskpair.transport

import io.ktor.network.sockets.Socket
import io.ktor.network.sockets.openReadChannel
import io.ktor.network.sockets.openWriteChannel
import io.ktor.utils.io.ByteReadChannel
import io.ktor.utils.io.ByteWriteChannel
import io.ktor.utils.io.readAvailable
import io.ktor.utils.io.writeFully

/**
 * The three things a framed stream needs from a socket.
 *
 * Small on purpose. Ordinary connections come from ktor, which handles addresses, selectors and dual-stack
 * fallback well. Hole punching cannot: it has to connect *from* a chosen local port, with the port reuse
 * that allows, and ktor's client offers no way to bind one. Rather than take ktor apart, the two kinds of
 * socket meet at this interface, and everything above it — framing, ciphers, the session — is written once.
 */
internal interface ByteStream : AutoCloseable {

    /** Reads what is available; returns -1 at end of stream, never 0 unless [length] is 0. */
    suspend fun read(destination: ByteArray, offset: Int, length: Int): Int

    suspend fun write(source: ByteArray, offset: Int, length: Int)

    suspend fun flush()
}

/** A ktor socket seen through [ByteStream]. */
internal class KtorByteStream(private val socket: Socket) : ByteStream {
    private val input: ByteReadChannel = socket.openReadChannel()
    private val output: ByteWriteChannel = socket.openWriteChannel()

    override suspend fun read(destination: ByteArray, offset: Int, length: Int): Int =
        input.readAvailable(destination, offset, length)

    override suspend fun write(source: ByteArray, offset: Int, length: Int): Unit =
        output.writeFully(source, offset, length)

    override suspend fun flush(): Unit = output.flush()

    override fun close(): Unit = socket.close()
}
