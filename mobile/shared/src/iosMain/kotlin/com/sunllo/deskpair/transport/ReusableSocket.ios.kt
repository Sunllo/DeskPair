package com.sunllo.deskpair.transport

import kotlinx.cinterop.ByteVar
import kotlinx.cinterop.CPointer
import kotlinx.cinterop.ExperimentalForeignApi
import kotlinx.cinterop.IntVar
import kotlinx.cinterop.addressOf
import kotlinx.cinterop.alloc
import kotlinx.cinterop.allocArrayOf
import kotlinx.cinterop.convert
import kotlinx.cinterop.memScoped
import kotlinx.cinterop.pointed
import kotlinx.cinterop.ptr
import kotlinx.cinterop.reinterpret
import kotlinx.cinterop.sizeOf
import kotlinx.cinterop.toKString
import kotlinx.cinterop.usePinned
import kotlinx.cinterop.value
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.DelicateCoroutinesApi
import kotlinx.coroutines.newFixedThreadPoolContext
import kotlinx.coroutines.withContext
import platform.posix.AF_INET
import platform.posix.AF_UNSPEC
import platform.posix.EINTR
import platform.posix.IPPROTO_TCP
import platform.posix.SOCK_STREAM
import platform.posix.SOL_SOCKET
import platform.posix.SO_RCVTIMEO
import platform.posix.SO_REUSEADDR
import platform.posix.SO_REUSEPORT
import platform.posix.SO_SNDTIMEO
import platform.posix.TCP_NODELAY
import platform.posix.addrinfo
import platform.posix.bind
import platform.posix.close
import platform.posix.connect
import platform.posix.errno
import platform.posix.freeaddrinfo
import platform.posix.getaddrinfo
import platform.posix.getsockname
import platform.posix.memset
import platform.posix.recv
import platform.posix.send
import platform.posix.setsockopt
import platform.posix.sockaddr
import platform.posix.sockaddr_in
import platform.posix.socket
import platform.posix.socklen_tVar
import platform.posix.strerror
import platform.posix.timeval

/**
 * A POSIX socket, because Kotlin/Native has no high-level one that can bind a local port before connecting.
 *
 * iOS was the platform the plan expected trouble from, and this is where it shows: everything here is a
 * system call, including the name resolution, and the whole thing runs on a dispatcher that tolerates
 * blocking. A punch makes a handful of these and each lives for seconds, so the cost is not worth avoiding.
 *
 * SO_REUSEPORT is set alongside SO_REUSEADDR. Darwin has both, and it is SO_REUSEPORT that allows two live
 * sockets on one local port — which is exactly what punching does while the announce connection is still
 * open.
 */
@OptIn(ExperimentalForeignApi::class)
internal actual suspend fun connectReusable(
    localPort: Int,
    host: String,
    port: Int,
    timeoutMillis: Long,
): ReusableConnection = withContext(blockingSockets) {
    val fd = socket(AF_INET, SOCK_STREAM, 0)
    if (fd < 0) {
        throw SocketException("socket() failed: ${lastError()}")
    }

    try {
        memScoped {
            val on = alloc<IntVar>()
            on.value = 1
            setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, on.ptr, sizeOf<IntVar>().convert())
            setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, on.ptr, sizeOf<IntVar>().convert())
            setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, on.ptr, sizeOf<IntVar>().convert())

            // A blocking connect with no deadline can sit for a minute or more; punching gives up in
            // seconds and tries again, so the socket has to as well.
            val timeout = alloc<timeval>()
            timeout.tv_sec = (timeoutMillis / 1000).convert()
            timeout.tv_usec = ((timeoutMillis % 1000) * 1000).convert()
            setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, timeout.ptr, sizeOf<timeval>().convert())
            setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, timeout.ptr, sizeOf<timeval>().convert())

            val local = alloc<sockaddr_in>()
            memset(local.ptr, 0, sizeOf<sockaddr_in>().convert())
            local.sin_family = AF_INET.convert()
            local.sin_port = networkOrder(localPort)
            local.sin_addr.s_addr = 0u // INADDR_ANY
            if (bind(fd, local.ptr.reinterpret(), sizeOf<sockaddr_in>().convert()) != 0) {
                throw SocketException("bind to port $localPort failed: ${lastError()}")
            }

            val remote = resolve(host, port)
            if (connect(fd, remote.ptr.reinterpret(), sizeOf<sockaddr_in>().convert()) != 0) {
                throw SocketException("connect to $host:$port failed: ${lastError()}")
            }

            // And then take the deadline off again, because it was only ever about the connect.
            //
            // A socket option outlives the call it was set for. Left in place, the punch's 400 ms became
            // the read deadline for the whole session that followed: the first time the host had nothing
            // to send for 400 ms -- an idle desktop, or the encoder taking a moment to ask for its next
            // frame -- recv() returned EAGAIN, which this stream reports as a failure, and the session
            // died about a second after it opened. The host meanwhile saw a healthy connection and went
            // on writing video into it until its own timeout noticed, half a minute later.
            //
            // That is why punched sessions here showed a picture and then dropped while relayed ones did
            // not: the relay path is ktor's socket and never had this option set. Every caller that needs
            // a read deadline already wraps its read in withTimeout, which is where a deadline belongs --
            // it bounds the wait without destroying the socket.
            val forever = alloc<timeval>()
            forever.tv_sec = 0
            forever.tv_usec = 0
            setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, forever.ptr, sizeOf<timeval>().convert())
            setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, forever.ptr, sizeOf<timeval>().convert())
        }

        ReusableConnection(PosixByteStream(fd), localPortOf(fd))
    } catch (e: Throwable) {
        close(fd)
        throw e
    }
}

/** Resolves a name or literal to one IPv4 address. IPv6 punching is a separate problem and not this one. */
@OptIn(ExperimentalForeignApi::class)
private fun kotlinx.cinterop.MemScope.resolve(host: String, port: Int): sockaddr_in {
    val hints = alloc<addrinfo>()
    memset(hints.ptr, 0, sizeOf<addrinfo>().convert())
    hints.ai_family = AF_UNSPEC
    hints.ai_socktype = SOCK_STREAM

    val result = alloc<kotlinx.cinterop.CPointerVar<addrinfo>>()
    if (getaddrinfo(host, port.toString(), hints.ptr, result.ptr) != 0 || result.value == null) {
        throw SocketException("cannot resolve $host")
    }

    try {
        var entry: CPointer<addrinfo>? = result.value
        while (entry != null) {
            val info = entry.pointed
            if (info.ai_family == AF_INET && info.ai_addr != null) {
                val out = alloc<sockaddr_in>()
                memset(out.ptr, 0, sizeOf<sockaddr_in>().convert())
                val source = info.ai_addr!!.reinterpret<sockaddr_in>().pointed
                out.sin_family = source.sin_family
                out.sin_port = source.sin_port
                out.sin_addr.s_addr = source.sin_addr.s_addr
                return out
            }
            entry = info.ai_next
        }
    } finally {
        freeaddrinfo(result.value)
    }

    throw SocketException("$host has no IPv4 address")
}

@OptIn(ExperimentalForeignApi::class)
private fun localPortOf(fd: Int): Int = memScoped {
    val address = alloc<sockaddr_in>()
    val length = alloc<socklen_tVar>()
    length.value = sizeOf<sockaddr_in>().convert()
    if (getsockname(fd, address.ptr.reinterpret<sockaddr>(), length.ptr) != 0) {
        return@memScoped 0
    }

    // The same byte swap in the other direction; it is its own inverse.
    networkOrder(address.sin_port.toInt()).toInt() and 0xFFFF
}

/**
 * Somewhere to block.
 *
 * Every call here is a blocking system call, and this version of the coroutines library keeps Dispatchers.IO
 * internal on native, so the threads have to be asked for directly. Two is enough: a punch has one announce
 * connection and one attempt in flight at a time.
 */
@OptIn(DelicateCoroutinesApi::class)
private val blockingSockets: CoroutineDispatcher = newFixedThreadPoolContext(2, "deskpair-socket")

/**
 * Host order to network order for a port.
 *
 * htons is a macro on Darwin and so is not in the posix package at all. Every platform this runs on is
 * little-endian, and the swap is its own inverse, which is why one function serves both directions.
 */
private fun networkOrder(port: Int): UShort =
    (((port and 0xFF) shl 8) or ((port shr 8) and 0xFF)).toUShort()

@OptIn(ExperimentalForeignApi::class)
private fun lastError(): String = strerror(errno)?.toKString() ?: "errno $errno"

@OptIn(ExperimentalForeignApi::class)
private class PosixByteStream(private val fd: Int) : ByteStream {

    override suspend fun read(destination: ByteArray, offset: Int, length: Int): Int =
        withContext(blockingSockets) {
            if (length == 0) {
                return@withContext 0
            }

            destination.usePinned { pinned ->
                // A signal landing mid-call is not the connection failing. A blocking read can be
                // interrupted at any point, and the only correct answer is to ask again.
                var n = recv(fd, pinned.addressOf(offset), length.convert(), 0)
                while (n < 0 && errno == EINTR) {
                    n = recv(fd, pinned.addressOf(offset), length.convert(), 0)
                }

                when {
                    n > 0 -> n.toInt()
                    n == 0L -> -1
                    else -> throw SocketException("recv failed: ${lastError()}")
                }
            }
        }

    override suspend fun write(source: ByteArray, offset: Int, length: Int) {
        withContext(blockingSockets) {
            var written = 0
            while (written < length) {
                val n = source.usePinned { pinned ->
                    send(fd, pinned.addressOf(offset + written), (length - written).convert(), 0)
                }
                if (n <= 0) {
                    if (n < 0 && errno == EINTR) {
                        continue
                    }

                    throw SocketException("send failed: ${lastError()}")
                }
                written += n.toInt()
            }
        }
    }

    /** Nothing buffers here; send() has already handed the bytes to the kernel. */
    override suspend fun flush() = Unit

    override fun close() {
        close(fd)
    }
}
