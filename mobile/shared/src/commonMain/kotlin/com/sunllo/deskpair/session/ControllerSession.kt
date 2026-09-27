package com.sunllo.deskpair.session

import com.sunllo.deskpair.Quality
import com.sunllo.deskpair.SessionPreferences
import com.sunllo.deskpair.crypto.Crypto
import com.sunllo.deskpair.crypto.Handshake
import com.sunllo.deskpair.crypto.HostIdentityVerifier
import com.sunllo.deskpair.crypto.PasswordProof
import com.sunllo.deskpair.crypto.SessionKeys
import com.sunllo.deskpair.framing.FramedStream
import com.sunllo.deskpair.framing.ProtocolException
import com.sunllo.deskpair.transport.PeerConnection
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import okio.ByteString.Companion.toByteString
import sunllo.messages.AuthChallenge
import sunllo.messages.BoolOption
import sunllo.messages.LoginRequest
import sunllo.messages.LoginResponse
import sunllo.messages.MediaCapabilities
import sunllo.messages.Message
import sunllo.messages.Misc
import sunllo.messages.PeerInfo
import sunllo.messages.SessionOptions
import sunllo.messages.SupportedDecoding
import sunllo.messages.VideoAck
import sunllo.messages.VideoCodec
import sunllo.rendezvous.ConnType
import kotlin.random.Random
import kotlin.time.Duration.Companion.seconds

/**
 * A controller session: connect, prove the host is who it says it is, log in, then read what it sends.
 *
 * This is the phone's half of `DeskPair.Core.Session.Controller.ControllerSession`, and deliberately
 * only its half. Nothing here captures a screen or injects input; the phone drives a PC and is never driven.
 */
internal class ControllerSession(
    private val connection: PeerConnection,
    private val identity: ControllerIdentity,
    private val capabilities: ClientCapabilities = ClientCapabilities(),
    /** A remote-control session, or a terminal one; the host scopes what the session may do by it. */
    private val connType: ConnType = ConnType.CONN_REMOTE,
) {

    private companion object {
        val HELLO_TIMEOUT = 10.seconds
        val LOGIN_TIMEOUT = 60.seconds

        /** Long enough for someone to walk to the host machine and click Accept. */
        val APPROVAL_TIMEOUT = 40.seconds
    }

    private val stream = FramedStream(
        socket = connection.stream,
        maxFrameBytes = FramedStream.MAX_PEER_FRAME_BYTES,
    )

    var peer: PeerInfo? = null
        private set

    var challenge: AuthChallenge? = null
        private set

    var keys: SessionKeys? = null
        private set

    /**
     * ControllerHello goes out in the clear; HostHello comes back signed over a transcript that binds the
     * host's long-term identity to this session's ephemeral keys and nonces. Verifying it is what makes a
     * replay or an impersonation fail, so the signature check is not optional and there is no way past it.
     */
    suspend fun handshake(verifier: HostIdentityVerifier) {
        val ephemeral = Crypto.generateEcdhKeyPair()
        val nonce = Crypto.randomBytes(Handshake.NONCE_BYTES)

        // protocol_version stays at 1 so a protocol-1 host answers with its hello -- which this side then
        // refuses with a message naming the host -- instead of closing the socket without a word. The range
        // this client actually speaks is min/max.
        stream.send(
            Message(
                controller_hello = sunllo.messages.ControllerHello(
                    protocol_version = LEGACY_HELLO_VERSION,
                    min_protocol_version = MIN_PROTOCOL_VERSION,
                    max_protocol_version = PROTOCOL_VERSION,
                    ephemeral_pk = ephemeral.publicKeySpki.toByteString(),
                    nonce = nonce.toByteString(),
                    version = identity.version,
                ),
            ).encode(),
        )

        val hello = expect(HELLO_TIMEOUT, "HostHello") { it.host_hello }

        // No common version: the host says which side has to update, in a hello with no signature. Which
        // side is read off the range it sent, so the app can say it in the reader's language.
        if (hello.refusal.isNotEmpty()) {
            val side = if (hello.min_protocol_version > PROTOCOL_VERSION) ProtocolVersionSide.CLIENT_TOO_OLD else ProtocolVersionSide.HOST_TOO_OLD
            throw ProtocolVersionException(hello.refusal, side)
        }
        if (hello.protocol_version < MIN_PROTOCOL_VERSION) {
            // A protocol-1 host accepted our hello as one of its own and answered with 1.
            throw ProtocolVersionException(
                "This computer's DeskPair speaks protocol ${hello.protocol_version} and this app needs " +
                    "$MIN_PROTOCOL_VERSION or newer. Update DeskPair on the computer you are connecting to.",
                ProtocolVersionSide.HOST_TOO_OLD,
            )
        }
        if (hello.protocol_version > PROTOCOL_VERSION) {
            throw ProtocolVersionException(
                "This computer's DeskPair chose protocol ${hello.protocol_version}, which this app does not " +
                    "speak (up to $PROTOCOL_VERSION). Update DeskPair on this device.",
                ProtocolVersionSide.CLIENT_TOO_OLD,
            )
        }
        if (hello.nonce.size != Handshake.NONCE_BYTES) {
            throw ProtocolException("The host nonce is ${hello.nonce.size} bytes, expected ${Handshake.NONCE_BYTES}.")
        }
        if (hello.id.isEmpty() || hello.identity_pk.size == 0) {
            throw ProtocolException("The host hello carries no identity.")
        }

        // Who the host claims to be, checked before anything it signed is trusted.
        verifier.verify(hello.id, hello.identity_pk.toByteArray())

        val transcript = Handshake.transcript(
            hostId = hello.id,
            identityPk = hello.identity_pk.toByteArray(),
            hostEphemeralPk = hello.ephemeral_pk.toByteArray(),
            hostNonce = hello.nonce.toByteArray(),
            controllerEphemeralPk = ephemeral.publicKeySpki,
            controllerNonce = nonce,
        )

        if (!Crypto.ecdsaVerify(hello.identity_pk.toByteArray(), transcript, hello.signature.toByteArray())) {
            throw ProtocolException("The host signature does not verify; this is not the machine it claims to be.")
        }

        val derived = Handshake.derive(
            ownPrivateKey = ephemeral.privateKey,
            peerEphemeralSpki = hello.ephemeral_pk.toByteArray(),
            controllerNonce = nonce,
            hostNonce = hello.nonce.toByteArray(),
        )
        stream.enableEncryption(derived)
        keys = derived

        // The host sends this unprompted, already encrypted.
        challenge = expect(HELLO_TIMEOUT, "AuthChallenge") { it.auth_challenge }
    }

    /**
     * Logs in. [password] is null when the host is in click-to-approve mode, where the proof is empty and
     * the person at the host decides instead — which is why this can wait a good deal longer than it sends.
     */
    suspend fun login(password: String?, onWaitingForApproval: () -> Unit = {}): PeerInfo {
        val challenge = checkNotNull(challenge) { "Log in only after the handshake." }

        val proof = if (password.isNullOrEmpty()) {
            ByteArray(0)
        } else {
            // The host names the derivation; the ceiling on rounds is this side's, because a challenge
            // arrives before anything about the host is trusted.
            if (challenge.kdf_iterations > PasswordProof.MAX_ITERATIONS) {
                throw ProtocolException(
                    "The host asks for ${challenge.kdf_iterations} password rounds; this app does at most ${PasswordProof.MAX_ITERATIONS}.",
                )
            }
            PasswordProof.computeProof(
                password,
                challenge.salt.toByteArray(),
                challenge.challenge.toByteArray(),
                challenge.kdf,
                challenge.kdf_iterations,
            )
        }

        stream.send(
            Message(
                login_request = LoginRequest(
                    password_proof = proof.toByteString(),
                    my_id = identity.id,
                    my_name = identity.name,
                    my_platform = identity.platform,
                    version = identity.version,
                    conn_type = connType,
                    options = capabilities.toSessionOptions(),
                    session_id = Random.nextLong(),
                    media = MediaCapabilities(udp = capabilities.udpMedia),
                ),
            ).encode(),
        )

        var wait = LOGIN_TIMEOUT
        while (true) {
            val response = expect(wait, "LoginResponse") { it.login_response }

            // The host repeats this while someone decides, so it is a state change rather than an answer.
            if (response.waiting_for_approval && response.peer_info == null && response.error == null) {
                onWaitingForApproval()
                wait = APPROVAL_TIMEOUT
                continue
            }

            response.error?.let { throw LoginRefusedException(it.message, it.code.name) }

            val info = response.peer_info
                ?: throw ProtocolException("The host accepted the login but sent no peer information.")
            peer = info
            return info
        }
    }

    /** One message, or null at end of stream. Frames the caller does not care about still pass through. */
    suspend fun receive(): Message? = stream.receive()?.let { Message.ADAPTER.decode(it) }

    suspend fun send(message: Message) = stream.send(message.encode())

    suspend fun sendHeartbeat() = stream.sendHeartbeat()

    /**
     * Acknowledges a video frame. The host uses these for flow control and for its own round-trip estimate,
     * so a viewer that never acks gets a stream that stalls rather than one that runs fast.
     */
    suspend fun acknowledgeVideo(display: Int, sequence: Int) =
        send(Message(misc = Misc(video_ack = VideoAck(display = display, seq = sequence))))

    /** Echoes the host's keepalive unchanged. Not echoing degrades the host's bitrate control. */
    suspend fun echo(delay: sunllo.messages.TestDelay) = send(Message(test_delay = delay))

    private suspend fun <T : Any> expect(timeout: kotlin.time.Duration, what: String, select: (Message) -> T?): T {
        val message = try {
            withTimeout(timeout) { receive() }
        } catch (e: TimeoutCancellationException) {
            throw ProtocolException("The host did not send $what within $timeout.")
        } ?: throw ProtocolException("The host closed the connection before sending $what.")

        return select(message)
            ?: throw ProtocolException("Expected $what but the host sent ${message.describe()}.")
    }
}

/** The newest protocol this app speaks; 2 added version negotiation, relay tickets and PBKDF2. */
internal const val PROTOCOL_VERSION = 2

/** The oldest it still speaks. Protocol 1 is refused: it cannot use a relay any more. */
internal const val MIN_PROTOCOL_VERSION = 2

/** What ControllerHello.protocol_version carries; see [ControllerSession.handshake]. */
internal const val LEGACY_HELLO_VERSION = 1

/** Which side of a version mismatch has to update. */
enum class ProtocolVersionSide { HOST_TOO_OLD, CLIENT_TOO_OLD }

/**
 * The two sides share no protocol version. [side] says which one has to update, for the app to say in the
 * reader's language; [message] is the English sentence, kept for logs.
 */
class ProtocolVersionException(message: String, val side: ProtocolVersionSide) : Exception(message)

/** Who this client says it is. None of it is trusted by the host; it is for the connection list. */
internal data class ControllerIdentity(
    val id: String,
    val name: String,
    val platform: String,
    val version: String,
)

/**
 * What this client can handle, which the host takes as a veto rather than a preference: a stream has one
 * encoder and every subscriber reads it, so one viewer that speaks only H.264 holds the whole stream there.
 *
 * H.264 is the honest default for a phone. Android's MediaCodec and iOS's VideoToolbox both have hardware
 * H.264 on every device that matters, which cannot be said of AV1 or VP9.
 */
internal data class ClientCapabilities(
    val h264: Boolean = true,
    val h265: Boolean = false,
    val vp8: Boolean = false,
    val vp9: Boolean = false,
    val av1: Boolean = false,
    /** Lossless tile refinement sharpens static text. Not yet implemented here, so not claimed. */
    val losslessTiles: Boolean = false,
    /**
     * The UDP media channel.
     *
     * On by default now that there is something to receive it with. Video over datagrams avoids TCP's
     * head-of-line blocking, which on a phone's link is the difference between a session that feels
     * immediate and one that feels broken; and a host whose offer cannot be taken up simply keeps sending
     * video on the TCP session, which is where it started.
     */
    val udpMedia: Boolean = true,
    val disableAudio: Boolean = true,

    /**
     * Whether to tell the host not to send its clipboard.
     *
     * Declined by default, and turned on only when the app supplied somewhere to put the text. A client
     * that asks for content it will drop costs the host a subscription and the user their privacy, and one
     * that forgets to ask simply never receives anything — which looks exactly like a broken feature.
     */
    val disableClipboard: Boolean = true,

    /**
     * What the user asked the picture to look like.
     *
     * Part of the capabilities rather than a separate thing sent alongside them, because the host does not
     * merge options: whatever is sent replaces its whole copy, so there has to be exactly one place that
     * knows the complete answer.
     */
    val preferences: SessionPreferences = SessionPreferences(
        quality = Quality.BALANCED,
        customBitrateKbps = 0,
        customFps = 0,
        showRemoteCursor = true,
        losslessRefinement = true,
        udpMedia = true,
        audioEnabled = false,
        viewOnly = false,
        lockAfterSessionEnd = false,
    ),
) {
    /**
     * Every option, every time.
     *
     * Not a patch. `OptionsHandler` on the host assigns the incoming message over its stored copy and then
     * re-derives the session's permissions from it, so anything omitted here is not "left alone" — it is
     * reset to "not set", which for audio and clipboard means the permission comes back. This used to send
     * three fields and silently re-enabled the host's audio capture every time the quality changed.
     */
    fun toSessionOptions(): SessionOptions = SessionOptions(
        image_quality = preferences.quality.toWire(),
        // Zero unless the user chose Custom, matching the desktop's own BuildOptions: the host reads these
        // only in that mode, and sending stale numbers the rest of the time invites a bug later.
        custom_bitrate_kbps = if (preferences.quality == Quality.CUSTOM) preferences.customBitrateKbps else 0,
        custom_fps = if (preferences.quality == Quality.CUSTOM) preferences.customFps else 0,
        show_remote_cursor = preferences.showRemoteCursor.asOption(),
        lossless_refinement = preferences.losslessRefinement.asOption(),
        disable_keyboard = preferences.viewOnly.asOption(),
        lock_after_session_end = preferences.lockAfterSessionEnd.asOption(),
        // Tri-state on the wire: "not set" means the host keeps its own default, which is not the same as
        // asking for it to be on.
        disable_audio = disableAudio.asOption(),
        disable_clipboard = disableClipboard.asOption(),
        supported_decoding = SupportedDecoding(
            h264 = h264,
            h265 = h265,
            vp8 = vp8,
            vp9 = vp9,
            av1 = av1,
            prefer = VideoCodec.VC_H264,
            tiles = losslessTiles,
        ),
    )
}

private fun Boolean.asOption(): BoolOption = if (this) BoolOption.BO_YES else BoolOption.BO_NO

/** The host said no. Distinct from a protocol failure: nothing is broken, the credentials are wrong. */
/**
 * The host said no.
 *
 * Public because the two reasons a connection fails call for different things from the user: a refused
 * login means asking for the password again and forgetting any stored one, where a dropped socket means
 * offering to retry. A caller that can only see "something failed" has to guess.
 */
public class LoginRefusedException(message: String, public val code: String) : Exception(message)

/** Names the message kind for an error, without dumping its contents into a log. */
private fun Message.describe(): String = when {
    host_hello != null -> "HostHello"
    auth_challenge != null -> "AuthChallenge"
    login_response != null -> "LoginResponse"
    video_frame != null -> "VideoFrame"
    misc != null -> "Misc"
    test_delay != null -> "TestDelay"
    else -> "an unexpected message"
}
