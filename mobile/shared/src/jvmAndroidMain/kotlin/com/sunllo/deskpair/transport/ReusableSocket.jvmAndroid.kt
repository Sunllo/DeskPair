package com.sunllo.deskpair.transport

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket

/**
 * java.net.Socket, because it can bind a local port before connecting and ktor's client cannot.
 *
 * Blocking calls on a dispatcher meant for them. This socket exists for hole punching, where there are at
 * most a handful at a time and each is short-lived, so a thread apiece costs nothing worth saving.
 */
internal actual suspend fun connectReusable(
    localPort: Int,
    host: String,
    port: Int,
    timeoutMillis: Long,
): ReusableConnection = withContext(Dispatchers.IO) {
    val socket = Socket()
    try {
        // Before the bind, or it has no effect. SO_REUSEPORT is what actually allows two live sockets on
        // one local port, but it is not in Android's SocketOption set, so this asks for it reflectively and
        // carries on without it — on Linux SO_REUSEADDR is enough for connections that differ in their
        // remote address, which is every case punching produces.
        socket.reuseAddress = true
        tryReusePort(socket)
        socket.bind(InetSocketAddress(localPort))
        socket.connect(InetSocketAddress(host, port), timeoutMillis.toInt())
        // Interactive input: a frame delayed to fill a segment is a cursor that lags.
        socket.tcpNoDelay = true

        ReusableConnection(JavaByteStream(socket), socket.localPort)
    } catch (e: Throwable) {
        runCatching { socket.close() }
        throw e
    }
}

/**
 * Asks for SO_REUSEPORT where the runtime has it.
 *
 * jdk.net.ExtendedSocketOptions carries it on desktop JVMs and Android does not ship that class at all, so
 * the lookup is reflective and its absence is not a failure. Whether punching then works is a property of
 * the platform, which is why it is measured rather than assumed.
 */
private fun tryReusePort(socket: Socket) {
    runCatching {
        val extended = Class.forName("jdk.net.ExtendedSocketOptions")
        @Suppress("UNCHECKED_CAST")
        val option = extended.getField("SO_REUSEPORT").get(null) as java.net.SocketOption<Boolean>
        socket.setOption(option, true)
    }
}

private class JavaByteStream(private val socket: Socket) : ByteStream {
    private val input: InputStream = socket.getInputStream()
    private val output: OutputStream = socket.getOutputStream()

    override suspend fun read(destination: ByteArray, offset: Int, length: Int): Int =
        withContext(Dispatchers.IO) { input.read(destination, offset, length) }

    override suspend fun write(source: ByteArray, offset: Int, length: Int) {
        withContext(Dispatchers.IO) { output.write(source, offset, length) }
    }

    override suspend fun flush() {
        withContext(Dispatchers.IO) { output.flush() }
    }

    override fun close() {
        runCatching { socket.close() }
    }
}
