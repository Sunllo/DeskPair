package com.sunllo.deskpair

import com.sunllo.deskpair.audio.AudioSink
import com.sunllo.deskpair.clipboard.ClipboardBridge
import com.sunllo.deskpair.clipboard.ClipboardSync
import com.sunllo.deskpair.media.MediaPath
import com.sunllo.deskpair.media.UdpMediaChannel
import com.sunllo.deskpair.crypto.EphemeralPinnedKeyStore
import com.sunllo.deskpair.crypto.PinnedKeyStore
import com.sunllo.deskpair.crypto.TofuIdentityVerifier
import com.sunllo.deskpair.session.ClientCapabilities
import com.sunllo.deskpair.session.ControllerIdentity
import com.sunllo.deskpair.session.ControllerSession
import com.sunllo.deskpair.transport.NatTypeDetector
import com.sunllo.deskpair.transport.PeerConnection
import com.sunllo.deskpair.transport.TransportKind
import com.sunllo.deskpair.transport.PeerConnector
import com.sunllo.deskpair.transport.Ports
import com.sunllo.deskpair.transport.RelayClient
import com.sunllo.deskpair.transport.RendezvousClient
import com.sunllo.deskpair.transport.RendezvousResult
import com.sunllo.deskpair.transport.formatIp
import com.sunllo.deskpair.transport.splitHostPort
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import sunllo.messages.KeyEvent
import sunllo.messages.KeyboardMode
import sunllo.messages.Message
import sunllo.messages.MouseEvent
import kotlin.coroutines.cancellation.CancellationException
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeMark
import kotlin.time.TimeSource

/**
 * The whole client, as the UI sees it.
 *
 * Everything below this is internal: framing, crypto, rendezvous, the message pump. A native UI written in
 * Compose or SwiftUI should be able to drive a remote desktop without ever learning what a FramedStream is,
 * and should not be able to reach past this to something that lets it get the protocol wrong.
 *
 * Every suspend function here is annotated @Throws. Without it Kotlin/Native has no NSError to hand back
 * and terminates the process instead, so on iOS a dropped connection or a wrong password would crash the
 * app rather than surface as a Swift error the UI could show. Android never sees this: JVM exceptions
 * propagate the ordinary way with or without the annotation.
 */
public class RemoteSession internal constructor(
    private val connection: PeerConnection,
    private val session: ControllerSession,
    private val sink: VideoSink,
    private val clipboard: ClipboardBridge?,
    private val audio: AudioSink?,
    private val cursor: CursorSink?,
    private val scope: CoroutineScope,
    capabilities: ClientCapabilities,
    private val memory: ResolutionMemory? = null,
) {

    /**
     * The options this session was opened with, restated in full by [setPreferences].
     *
     * Held because the host does not merge them: every update has to say everything, so the last complete
     * answer has to live somewhere.
     */
    private var capabilities: ClientCapabilities = capabilities

    private val clipboardSync = ClipboardSync()

    /** The UDP media channel, once the host has offered one and it has been accepted. */
    private var media: UdpMediaChannel? = null

    private val _mediaPath = MutableStateFlow<String?>(null)

    /**
     * How video is arriving: null while it is still on the TCP session, otherwise the kind of UDP path in
     * use. Worth showing — a relayed media path and a direct one behave differently enough that a user
     * watching a session stutter deserves to know which they have.
     *
     * Observable rather than read once. The host offers its datagram channel seconds after the session is
     * up, so anything that samples this at connect time learns only that the answer had not arrived yet.
     */
    public val mediaPath: StateFlow<String?> = _mediaPath.asStateFlow()

    /**
     * The same value, readable without a flow.
     *
     * Swift sees a StateFlow without its element type, so `.value` there is untyped; this is the honest
     * way to ask "what is it now" from a language the generic does not survive into.
     */
    public val currentMediaPath: String? get() = _mediaPath.value

    private val _roundTripMillis = MutableStateFlow<Int?>(null)

    /**
     * The host's own round-trip estimate, in milliseconds.
     *
     * It comes from the TestDelay messages this client already echoes, so it costs nothing extra and it is
     * the same figure the host uses to size its bitrate. Null until the first one arrives.
     */
    public val roundTripMillis: StateFlow<Int?> = _roundTripMillis.asStateFlow()

    private val _state = MutableStateFlow(SessionState.CONNECTED)

    /** What the session is doing, for the UI to show without asking. */
    public val state: StateFlow<SessionState> = _state.asStateFlow()

    /** Who answered: hostname, platform, and how many displays it has. */
    public val host: RemoteHost = session.peer.let {
        RemoteHost(
            hostname = it?.hostname.orEmpty(),
            username = it?.username.orEmpty(),
            platform = it?.platform.orEmpty(),
            displays = it?.displays?.map(::remoteDisplay) ?: emptyList(),
        )
    }

    private val _displays = MutableStateFlow(host.displays)

    /**
     * The host's displays as they are now. [RemoteHost.displays] is what they were at login; this follows
     * every change of mode the host announces, whoever asked for it.
     */
    public val displays: StateFlow<List<RemoteDisplay>> = _displays.asStateFlow()

    private val _resolutionFailure = MutableStateFlow<String?>(null)

    /** Why the last resolution request was refused, until the next one is made or succeeds. */
    public val resolutionFailure: StateFlow<String?> = _resolutionFailure.asStateFlow()

    private val _hostNotice = MutableStateFlow<String?>(null)

    /**
     * The host's own word on why its displays are what they are, or null when it has none.
     *
     * A Wayland desktop shares its screen only once the person sitting at it agrees, so the host says that
     * it is asking, that the person declined or did not answer, that there is nothing to ask through, or
     * that sharing was stopped at that machine. It comes in the host's words, and with every display change
     * the host announces, so it clears itself when the screen is shared. Without it the phone waited for a
     * picture and then blamed a Windows lock screen.
     */
    public val hostNotice: StateFlow<String?> = _hostNotice.asStateFlow()

    /** What this session last asked for, so a confirmation can be told from somebody else's change. */
    private var pendingResolution: Pair<Int, RemoteResolution?>? = null

    /** How the host was reached. A relayed session is slower and the UI should be honest about it. */
    public val transport: String = connection.kind.name

    public var videoFramesReceived: Int = 0
        private set

    private val _permissions = MutableStateFlow(HostPermissions())

    /**
     * What the host is currently willing to let this session do.
     *
     * The host states all five right after login and again whenever one changes, and until now the phone
     * ignored every one of them — so a host that had turned the keyboard off still got keystrokes, and the
     * user still saw a keyboard that did nothing. The desktop controller has always tracked these.
     *
     * Optimistic before the first message arrives: the host's own answers land within milliseconds of
     * login, and starting from "everything is refused" would flash a disabled interface at every user.
     */
    public val permissions: StateFlow<HostPermissions> = _permissions.asStateFlow()

    private val _stalled = MutableStateFlow(false)

    /**
     * Whether the picture on screen is older than the host's own heartbeat interval.
     *
     * Distinct from [state], which says whether the session is alive. A stalled session is still
     * connected — the last frame is simply stale — and it usually recovers on its own. Both apps used to
     * conflate the two: `SessionState.FAILED` was shown as "the picture has stalled", so a session that
     * had actually ended looked like one that was merely slow, and on iOS the warning then never went
     * away because nothing ever set it back.
     *
     * The threshold matches the desktop's: `ProtocolConstants.SessionStalledAfter`, six seconds, which is
     * one second longer than the heartbeat the host sends when it has nothing else to say.
     */
    public val stalled: StateFlow<Boolean> = _stalled.asStateFlow()

    private val _display = MutableStateFlow(0)

    /** Which display the host is actually sending, as it last confirmed. */
    public val display: StateFlow<Int> = _display.asStateFlow()

    /** When something last arrived, for [stalled]. Monotonic: this measures a gap, not a time of day. */
    private var lastArrival: TimeMark = TimeSource.Monotonic.markNow()

    private var loop: Job? = null

    /**
     * Set the moment a close is asked for, so the read that then fails is recognised as the consequence of
     * closing rather than as the session breaking.
     */
    private var closing = false

    internal fun start() {
        loop = scope.launch {
            // Nothing arrives for five seconds on an idle desktop, and the host drops a silent peer, so the
            // keepalive runs whatever else is happening.
            launch { heartbeat() }
            launch { watchForStall() }
            launch { reapplyRemembered() }
            try {
                pump()
                _state.value = SessionState.CLOSED
            } catch (e: Throwable) {
                // Closing a session pulls the socket out from under a read that is already waiting on it,
                // and the exception that follows is not news.
                if (closing || e is kotlinx.coroutines.CancellationException) {
                    _state.value = SessionState.CLOSED
                    return@launch
                }

                // Nor is a network that went away. This used to rethrow, three lines below a comment
                // explaining that rethrowing hands an unhandled exception to whatever runs this scope —
                // which on Android is the process-wide handler. A phone losing Wi-Fi in a lift is the
                // ordinary case, not an exceptional one, and it took the app down with it. The state and
                // the reason are the whole of what a caller can act on; there is nobody above this to
                // catch anything.
                _state.value = SessionState.FAILED
                failure = e.message ?: e::class.simpleName
            }
        }
    }

    /** Why the session ended, when it ended badly. */
    public var failure: String? = null
        private set

    private suspend fun pump() {
        while (true) {
            val message = session.receive() ?: return

            lastArrival = TimeSource.Monotonic.markNow()
            _stalled.value = false

            // The host saying why it is going. Without this the phone learns of a deliberate disconnect
            // the same way it learns of a severed cable — as a socket that stopped — and both apps then
            // showed the connect screen with nothing on it.
            message.misc?.close_reason?.let { reason ->
                failure = reason.reason.takeIf { it.isNotEmpty() }
                return
            }

            message.video_frame?.let { video ->
                val encoded = video.frame ?: return@let
                videoFramesReceived++

                // Acknowledged before decoding, on purpose: the ack measures the network, and delaying it
                // until after a slow decode would have the host throttle a link that is perfectly fine.
                session.acknowledgeVideo(video.display, encoded.seq)

                // The host sends only what this session said it decodes, so an unknown number is a host
                // newer than this build; asking it for a keyframe would only bring another of the same.
                val format = VideoFormat.fromWire(video.codec.value) ?: return@let
                val accepted = sink.onFrame(
                    data = encoded.data_.toByteArray(),
                    isKeyFrame = encoded.key,
                    width = video.width,
                    height = video.height,
                    format = format,
                )

                // A decoder that cannot use what it was given needs a fresh start, not the next inter frame.
                if (!accepted) {
                    session.send(
                        Message(misc = sunllo.messages.Misc(refresh_video = sunllo.messages.RefreshVideo(display = video.display))),
                    )
                }
            }

            message.clipboard?.let { incoming ->
                val bridge = clipboard ?: return@let
                // The host's own hash, not one recomputed here: comparing what it sent is what makes an
                // echo recognisable on both sides.
                val hash = incoming.content_hash.toByteArray()
                    .takeIf { it.isNotEmpty() } ?: ClipboardSync.hash(incoming)

                if (clipboardSync.shouldApply(hash)) {
                    ClipboardSync.textFromWire(incoming)?.let { text ->
                        clipboardSync.markApplied(hash)
                        bridge.onRemoteText(text)
                    }
                }
            }

            message.audio_frame?.let { frame ->
                audio?.onFrame(frame.opus.toByteArray(), frame.pts_ms)
            }

            message.misc?.permission_info?.let { info ->
                _permissions.value = _permissions.value.with(info.permission, info.enabled)
            }

            // The host confirming which display it switched to. Ignored before, so the picker could sit on
            // a number the host had declined and every keyframe request went to display zero regardless.
            message.misc?.switch_display?.let { chosen ->
                _display.value = chosen.display
                // A new display is a new sequence space for the UDP assembler.
                media?.expectStream(chosen.display)
            }

            // A display changed mode -- at this session's request, another viewer's, or not at all because
            // the request was refused. A refusal only ever comes to the one who asked.
            message.misc?.displays_changed?.let { changed ->
                // Every announcement carries the host's current notice, a refusal included.
                _hostNotice.value = changed.notice.ifEmpty { null }
                if (changed.failure.isNotEmpty()) {
                    pendingResolution = null
                    _resolutionFailure.value = changed.failure
                } else {
                    val now = changed.displays.map(::remoteDisplay)
                    _displays.value = now
                    _display.value = changed.current_display
                    _resolutionFailure.value = null
                    rememberIfConfirmed(now, changed.changed)
                }
            }

            message.misc?.audio_format?.let { format ->
                audio?.onFormat(format.sample_rate, format.channels)
            }

            message.misc?.media_offer?.let { offer -> acceptMediaOffer(offer) }
            message.misc?.media_close?.let {
                media?.close("the host closed the media channel")
                media = null
                _mediaPath.value = null
            }

            message.cursor_data?.let { shape ->
                cursor?.onCursorShape(
                    id = shape.id.toLong(),
                    hotX = shape.hotx,
                    hotY = shape.hoty,
                    width = shape.width,
                    height = shape.height,
                    bgra = shape.bgra.toByteArray(),
                )
            }

            message.cursor_position?.let { at -> cursor?.onCursorPosition(at.x, at.y) }
            message.cursor_id?.let { which -> cursor?.onCursorShapeChanged(which.id.toLong()) }

            // Not echoing this degrades the host's round-trip estimate, and with it its bitrate control.
            message.test_delay?.let {
                if (it.last_delay_ms > 0) {
                    _roundTripMillis.value = it.last_delay_ms
                }
                session.echo(it)
            }
        }
    }

    /**
     * Takes the host up on a UDP channel for video.
     *
     * The answer carries no candidates of our own, and that is not an omission. A phone behind a NAT has no
     * address worth announcing — the one that works is whatever its own outgoing probe looks like from the
     * far side, and the host learns exactly that from the Bind packets this channel starts sending. Offering
     * a private 10.x address instead would only give the host something to fail at.
     */
    private suspend fun acceptMediaOffer(offer: sunllo.messages.MediaChannelOffer) {
        if (media != null) {
            return
        }

        val keys = session.keys ?: return
        val channel = UdpMediaChannel(
            channelId = offer.channel_id,
            keys = keys,
            nowMillis = ::monotonicMillis,
            onFrame = { frame ->
                VideoFormat.fromWire(frame.codec)?.let { format ->
                    sink.onFrame(frame.data, frame.keyFrame, frame.width, frame.height, format)
                }
                videoFramesReceived++
            },
            onPathChosen = { path ->
                _mediaPath.value = when (path.kind) {
                    MediaPath.LOCAL -> "UDP_LOCAL"
                    MediaPath.REFLEXIVE -> "UDP_REFLEXIVE"
                    else -> "UDP_RELAY"
                }
                scope.launch {
                    runCatching {
                        session.send(
                            Message(
                                misc = sunllo.messages.Misc(
                                    media_ready = sunllo.messages.MediaChannelReady(channel_id = offer.channel_id),
                                ),
                            ),
                        )
                    }
                }
            },
            onClosed = {
                media = null
                _mediaPath.value = null
            },
        )

        try {
            channel.open()
        } catch (e: Throwable) {
            if (e is CancellationException) throw e
            // No UDP here. Declining is the whole fallback: the host keeps sending video over TCP, which
            // is what it was already doing.
            session.send(
                Message(
                    misc = sunllo.messages.Misc(
                        media_answer = sunllo.messages.MediaChannelAnswer(
                            channel_id = offer.channel_id,
                            accept = false,
                        ),
                    ),
                ),
            )
            return
        }

        // A relayed session takes a relayed picture.
        //
        // The channel picks the shortest path it can verify, and left to itself it will pick a reflexive
        // one over the relay every time: fewer hops, nobody else's bandwidth. That is right when the
        // session itself found its way straight there. It is wrong here, and silently: this session is on
        // the relay *because* the direct path did not hold, and a picture sent down that same path stops
        // arriving while the session stays up -- a black screen with a healthy connection behind it, for
        // as long as it takes the host's liveness timer to notice.
        //
        // So when the session is relayed, so is the video. The one case that loses something -- a relayed
        // session whose reflexive media path would have worked -- is a case where the punch had already
        // been tried and refused.
        val relayed = connection.kind == TransportKind.RELAY
        for (candidate in offer.candidates) {
            val address = candidate.addr ?: continue
            if (relayed && candidate.kind.value != MediaPath.RELAY) {
                continue
            }

            channel.addRemote(candidate.kind.value, formatIp(address.ip.toByteArray()), address.port)
        }

        if (offer.relay_server.isNotEmpty() && offer.relay_token.size == 16) {
            val (host, port) = splitHostPort(offer.relay_server, Ports.RELAY)
            channel.useRelay(host, port, offer.relay_token.toByteArray(), offer.relay_ticket?.encode())
        }

        media = channel
        session.send(
            Message(
                misc = sunllo.messages.Misc(
                    media_answer = sunllo.messages.MediaChannelAnswer(
                        channel_id = offer.channel_id,
                        accept = true,
                    ),
                ),
            ),
        )
    }

    /**
     * Flips [stalled] when nothing has arrived for a while, and back the moment something does.
     *
     * Polled rather than driven by a timer per message: the check is once a second and costs nothing,
     * whereas rescheduling a timeout on every frame would run sixty times a second to answer a question
     * nobody asks that often.
     */
    private suspend fun watchForStall() {
        while (scope.isActive) {
            delay(STALL_CHECK.inWholeMilliseconds)
            if (closing) {
                return
            }

            _stalled.value = lastArrival.elapsedNow() >= STALLED_AFTER
        }
    }

    private suspend fun heartbeat() {
        while (scope.isActive) {
            delay(HEARTBEAT.inWholeMilliseconds)
            runCatching { session.sendHeartbeat() }.onFailure { return }
        }
    }

    /**
     * Moves or clicks. The mask encoding is the protocol's: the low three bits are the action and the
     * button flags sit above them.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun sendPointer(action: PointerAction, button: PointerButton, x: Int, y: Int) {
        if (!canSendInput) {
            return
        }

        val mask = action.code or (button.flag shl 3)
        // Off the caller's dispatcher deliberately: a UI framework calls this from its main thread, and a
        // library that only works when the caller remembers to switch is a trap rather than an API.
        withContext(Dispatchers.Default) {
            // The display the picture is of: the host offsets by its origin, so a tap on the second
            // monitor lands on the second monitor.
            session.send(Message(mouse_event = MouseEvent(mask = mask, x = x, y = y, display = _display.value)))
        }
    }

    /**
     * Types text, reproduced on the host's own layout. This is what a soft keyboard produces, and it is the
     * right path for anything a person is writing rather than commanding.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun sendText(text: String) {
        if (!canSendInput) {
            return
        }

        withContext(Dispatchers.Default) {
            session.send(Message(key_event = KeyEvent(seq = text, mode = KeyboardMode.KM_TRANSLATE)))
        }
    }

    /**
     * Tells the host what this session should look like now.
     *
     * Sent as options rather than as commands, because that is what they are — the host decides what it
     * can actually deliver, and a phone on a train asking for "best" does not make the link wider.
     *
     * Every field goes every time. `OptionsHandler` on the host assigns the incoming message over its
     * stored copy and re-derives the session's permissions from it, so a partial update does not leave the
     * rest alone: it resets it. The version of this that sent only the three quality fields turned the
     * host's audio capture back on every time the picture was adjusted.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun setPreferences(preferences: SessionPreferences): Unit = withContext(Dispatchers.Default) {
        capabilities = capabilities.copy(preferences = preferences)
        session.send(Message(misc = sunllo.messages.Misc(options = capabilities.toSessionOptions())))
    }

    /**
     * Asks the host to send a fresh keyframe.
     *
     * The picture can be wrong without anything being broken — a frame lost on the UDP path, a decoder that
     * came back from the background mid-stream. The session already asks for this on its own when a decoder
     * rejects a frame; this is the same request with a person behind it.
     */
    /**
     * Tells the session the app is leaving the screen, or back on it. The OS drops packets while it winds
     * the app down; counting those as loss had the host judge the link congested and the picture came back
     * blurry. See [UdpMediaChannel.backgrounded].
     */
    public fun setBackgrounded(background: Boolean) {
        media?.backgrounded = background
    }

    @Throws(CancellationException::class, Throwable::class)
    public suspend fun refreshVideo(display: Int = _display.value): Unit = withContext(Dispatchers.Default) {
        session.send(
            Message(misc = sunllo.messages.Misc(refresh_video = sunllo.messages.RefreshVideo(display = display))),
        )
    }

    /**
     * Shows a different one of the host's displays.
     *
     * The host restarts its stream on the new display, so the next frame is a keyframe and the picture
     * dimensions change with it — which is why [RemoteHost.displays] carries each one's size.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun switchDisplay(index: Int): Unit = withContext(Dispatchers.Default) {
        session.send(
            Message(
                misc = sunllo.messages.Misc(
                    switch_display = sunllo.messages.SwitchDisplay(display = index),
                ),
            ),
        )
    }

    /**
     * Asks the host to switch [display] to one of the modes it advertised, or back to its original when
     * [resolution] is null. The answer arrives as a change of [displays], or as [resolutionFailure].
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun setResolution(display: Int, resolution: RemoteResolution?): Unit = withContext(Dispatchers.Default) {
        pendingResolution = display to resolution
        _resolutionFailure.value = null
        session.send(
            Message(
                misc = sunllo.messages.Misc(
                    display_resolution = sunllo.messages.DisplayResolution(
                        display = display,
                        resolution = resolution?.toWire(),
                    ),
                ),
            ),
        )
    }

    /** The mode this viewer chose last time, asked for once after login if the host still offers it. */
    private suspend fun reapplyRemembered() {
        val memory = memory ?: return
        val index = _display.value
        val display = _displays.value.getOrNull(index) ?: return
        val wanted = Resolutions.reapplyWanted(display, memory.remembered(display.name)) ?: return
        runCatching { setResolution(index, wanted) }
    }

    /** A confirmed change of this session's own asking is kept for next time; "original" forgets it. */
    private fun rememberIfConfirmed(now: List<RemoteDisplay>, changed: Int) {
        val (index, wanted) = pendingResolution ?: return
        if (index != changed) {
            return
        }

        val display = now.getOrNull(index) ?: return
        if (wanted != null && !wanted.matches(display)) {
            return // somebody else's change landed in between; it is not what this viewer asked for
        }

        pendingResolution = null
        memory?.remember(display.name, wanted)
    }

    /**
     * Offers this device's clipboard text to the host.
     *
     * Call it whenever the local clipboard changes; sending the same content twice, or sending back what
     * the host just gave us, is filtered here rather than by the caller. Returns whether anything went —
     * false means the content was recognised as an echo, which is the ordinary case and not an error.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun sendClipboardText(text: String): Boolean =
        sendClipboard(text = text, png = null)

    /**
     * Offers a PNG to the host's clipboard, so it can be pasted there.
     *
     * The bytes go as they are — the phone has already encoded them and re-encoding would only lose
     * something. One way only: nothing sends the host's pictures back to the phone.
     *
     * Throws when the image is larger than [ClipboardSync.MAX_BYTES]. That is deliberate: a picture that
     * silently fails to arrive is worse than one the app can explain, and the caller is the only thing
     * that knows how to make it smaller.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun sendClipboardImage(png: ByteArray): Boolean {
        require(png.size <= ClipboardSync.MAX_BYTES) {
            "That picture is ${png.size} bytes; the most a clipboard can carry is ${ClipboardSync.MAX_BYTES}."
        }

        return sendClipboard(text = null, png = png)
    }

    private suspend fun sendClipboard(text: String?, png: ByteArray?): Boolean =
        withContext(Dispatchers.Default) {
            val wire = ClipboardSync.toWire(text, png) ?: return@withContext false
            val hash = wire.content_hash.toByteArray()
            if (!clipboardSync.shouldSend(hash)) {
                return@withContext false
            }

            clipboardSync.markSent(hash)
            session.send(Message(clipboard = wire))
            true
        }

    /**
     * A named key, optionally with modifiers held. [pressed] null means a complete press; true and false
     * send the halves separately, which is what a modifier bar needs to latch one down.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun sendKey(
        key: RemoteKey,
        modifiers: Set<RemoteModifier> = emptySet(),
        pressed: Boolean? = null,
    ) {
        if (!canSendInput) {
            return
        }

        withContext(Dispatchers.Default) {
            withModifiersHeld(modifiers) {
                session.send(
                    Message(
                        key_event = KeyEvent(
                            // Two keys are commands the host performs rather than keys it presses, and it
                            // acts on the down edge, so a bare press would be ignored entirely.
                            down = pressed ?: true,
                            press = pressed == null && !key.isCommand(),
                            control_key = key.toWire(),
                            modifiers = modifiers.map { it.toWire() },
                        ),
                    ),
                )

                if (pressed == null && !key.isCommand()) {
                    return@withModifiersHeld
                }

                if (pressed == null) {
                    // A command key still needs its up edge so the host's own state stays clean.
                    session.send(
                        Message(
                            key_event = KeyEvent(
                                down = false,
                                control_key = key.toWire(),
                                modifiers = modifiers.map { it.toWire() },
                            ),
                        ),
                    )
                }
            }
        }
    }

    /**
     * A key identified by where it sits rather than by what it prints.
     *
     * Shortcuts have to travel this way. A host on a Dvorak or AZERTY layout is told "the key in the C
     * position" and reproduces the shortcut its own user would type; sending the character C instead would
     * land on whichever key prints a C there, which is a different key entirely.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun sendScancode(
        scancode: Int,
        modifiers: Set<RemoteModifier> = emptySet(),
        pressed: Boolean? = null,
    ) {
        if (!canSendInput) {
            return
        }

        withContext(Dispatchers.Default) {
            withModifiersHeld(modifiers) {
                session.send(
                    Message(
                        key_event = KeyEvent(
                            down = pressed ?: false,
                            press = pressed == null,
                            chr = scancode,
                            modifiers = modifiers.map { it.toWire() },
                            mode = KeyboardMode.KM_MAP,
                        ),
                    ),
                )
            }
        }
    }

    /** Convenience for the common case: a shortcut on a character key, such as Ctrl+C. */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun sendShortcut(character: Char, modifiers: Set<RemoteModifier>) {
        if (!canSendInput) {
            return
        }

        val scancode = Scancodes.of(character)
            ?: throw IllegalArgumentException("'$character' is not on a key this client can name positionally.")
        sendScancode(scancode, modifiers)
    }

    /**
     * Presses the modifiers, runs [body], releases them.
     *
     * The protocol carries a modifiers list on every key event and the host ignores it: it takes modifier
     * state from real key presses, because that is what a physical keyboard produces. A phone has no
     * physical Ctrl, so the client presses and releases one around the key — otherwise Cmd+S is simply S,
     * silently, with nothing to say why.
     *
     * The list is still filled in on the wire, so a host that does read it agrees with what it sees.
     */
    private suspend fun withModifiersHeld(modifiers: Set<RemoteModifier>, body: suspend () -> Unit) {
        modifiers.forEach { session.send(modifierEvent(it, down = true)) }
        try {
            body()
        } finally {
            // Released even if sending the key failed, so a stuck Ctrl does not outlive one mistake.
            modifiers.toList().reversed().forEach { runCatching { session.send(modifierEvent(it, down = false)) } }
        }
    }

    private fun modifierEvent(modifier: RemoteModifier, down: Boolean) =
        Message(key_event = KeyEvent(down = down, control_key = modifier.toWire()))

    @Throws(CancellationException::class, Throwable::class)
    public suspend fun close(reason: String = "closed by user"): Unit = withContext(Dispatchers.Default) {
        closing = true
        media?.close("the session ended")
        media = null
        runCatching {
            session.send(Message(misc = sunllo.messages.Misc(close_reason = sunllo.messages.CloseReason(reason = reason))))
        }
        loop?.cancel()
        connection.close()
        _state.value = SessionState.CLOSED
    }

    /**
     * Whether input may leave this device at all.
     *
     * Two reasons it may not: the user asked for a view-only session, or the host withdrew the keyboard
     * permission. Checked here as well as in the interface, because a screen that has not caught up yet
     * must not be able to drive someone else's machine.
     */
    private val canSendInput: Boolean
        get() = !capabilities.preferences.viewOnly && _permissions.value.keyboard

    private companion object {
        val HEARTBEAT = 5.seconds

        /** Matches the desktop's `ProtocolConstants.SessionStalledAfter`. */
        val STALLED_AFTER = 6.seconds
        val STALL_CHECK = 1.seconds
    }
}

public enum class SessionState { CONNECTED, CLOSED, FAILED }

/** How much bandwidth the host should spend on the picture. */
public enum class Quality {
    LOW,
    BALANCED,
    BEST,
    CUSTOM,
    ;

    internal fun toWire(): sunllo.messages.ImageQuality = when (this) {
        LOW -> sunllo.messages.ImageQuality.IQ_LOW
        BALANCED -> sunllo.messages.ImageQuality.IQ_BALANCED
        BEST -> sunllo.messages.ImageQuality.IQ_BEST
        CUSTOM -> sunllo.messages.ImageQuality.IQ_CUSTOM
    }
}

/**
 * What connecting is doing, for a UI to say so in its own language.
 *
 * A state rather than a sentence. The library has no business deciding what language the user reads, and
 * a message built here would be English wherever it was shown.
 */
public enum class ConnectProgress {
    /** Asking the rendezvous server where the host is. */
    LOOKING_UP,

    /** Measuring this network, to decide between a direct path and a relay. */
    MEASURING_NETWORK,

    /** Opening a hole through both NATs. */
    PUNCHING,

    /** Opening the connection and proving who the host is. */
    CONNECTING,

    /** Someone at the host has to accept before this goes any further. */
    WAITING_FOR_APPROVAL,
}

public data class RemoteHost(
    val hostname: String,
    val username: String,
    val platform: String,
    val displays: List<RemoteDisplay>,
)

/**
 * One of the host's displays. [modes] is what it can be switched to (empty when the host cannot change
 * it from here) and [original] the mode it was in before any viewer changed it, once somebody has.
 */
public data class RemoteDisplay(
    val width: Int,
    val height: Int,
    val x: Int,
    val y: Int,
    val name: String = "",
    val scale: Double = 0.0,
    val modes: List<RemoteResolution> = emptyList(),
    val original: RemoteResolution? = null,
)

internal fun remoteDisplay(d: sunllo.messages.DisplayInfo): RemoteDisplay = RemoteDisplay(
    width = d.width,
    height = d.height,
    x = d.x,
    y = d.y,
    name = d.name,
    scale = d.scale,
    modes = d.modes.map(RemoteResolution::fromWire),
    original = d.original?.let(RemoteResolution::fromWire),
)

/**
 * Where decoded video goes. The platform implements this over MediaCodec or VideoToolbox and renders
 * straight to its own surface; nothing here ever sees a pixel.
 *
 * Returning false means "I could not use that" and makes the session ask the host for a keyframe. A
 * pipelined hardware decoder that has simply not produced output yet should return true: that is normal and
 * asking for a keyframe every time would make it worse.
 */
public interface VideoSink {
    /**
     * What this device can decode, which the session tells the host before any picture is sent. The host
     * picks one that it can encode too (H.264 first, which a phone decodes in hardware), so a format named
     * here has to be one that really decodes: a VP9 stream to a phone that cannot read it is a black screen.
     */
    public val formats: Set<VideoFormat> get() = setOf(VideoFormat.H264)

    /** [format] is the one the host chose, and it can change between keyframes when the audience changes. */
    public fun onFrame(data: ByteArray, isKeyFrame: Boolean, width: Int, height: Int, format: VideoFormat): Boolean
}

/** The protocol's mouse actions, named rather than numbered. */
public enum class PointerAction(internal val code: Int) {
    MOVE(0),
    DOWN(1),
    UP(2),
    WHEEL(3),
    TRACKPAD(4),
    MOVE_RELATIVE(5),
}

public enum class PointerButton(internal val flag: Int) {
    NONE(0),
    LEFT(0x01),
    RIGHT(0x02),
    MIDDLE(0x04),
    BACK(0x08),
    FORWARD(0x10),
}

/** Which machine to reach, and how. */
public sealed interface Target {
    /**
     * A nine-digit id, resolved through the rendezvous server. The usual case for a phone.
     *
     * [forceRelay] skips the punch. It costs latency and someone's bandwidth, and it is the right answer on
     * a network where punching is known not to work — but as a default it would give up a direct path that
     * is usually available, so it is off.
     */
    public data class ById(
        val id: String,
        val rendezvousServer: String,
        val serverPublicKeySpki: ByteArray,
        val forceRelay: Boolean = false,
    ) : Target {
        override fun equals(other: Any?): Boolean = this === other
        override fun hashCode(): Int = id.hashCode()
    }

    /** An address the user typed. Only useful on the same network, and pinned on first use. */
    public data class ByAddress(val host: String, val port: Int = Ports.DIRECT_ACCESS) : Target
}

/**
 * Opens sessions. One of these lives for as long as the app does; it holds the pinned host keys and the
 * client's own identity.
 */
public class DeskPair(
    private val deviceName: String,
    private val platform: String,
    private val version: String = "0.1.0",
    /**
     * Where pinned host keys live. **Deliberately has no default.** It used to default to
     * [EphemeralPinnedKeyStore], and both apps quietly took it, so every launch was a first use and
     * trust-on-first-use was not protecting anything. A test that genuinely wants to forget can still say
     * so — it just has to say so.
     */
    private val pinnedKeys: PinnedKeyStore,
) {

    /**
     * The pinned host keys, so a settings screen can list what is trusted and forget a machine that was
     * legitimately reinstalled. The desktop's security tab does exactly this over `App.KnownHosts`.
     */
    public val trustedHosts: PinnedKeyStore get() = pinnedKeys

    /**
     * What kind of network this device is on, as the rendezvous server sees it.
     *
     * "Asymmetric" means a direct connection can usually be punched through and a session will be as fast
     * as the link allows. "Symmetric" means every connection has to go through a relay — still works,
     * still encrypted end to end, but slower and through someone else's machine. Worth showing rather than
     * hiding, because it explains a difference the user would otherwise just feel.
     *
     * Returns "unknown" when the probe could not complete, which is its own answer: not a claim that the
     * network is bad, only that nothing was learned.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun describeNetwork(rendezvousServer: String): String = withContext(Dispatchers.Default) {
        val (host, port) = splitHostPort(rendezvousServer, Ports.RENDEZVOUS)
        val detector = NatTypeDetector(host, port)
        val type = detector.detect(monotonicMillis())
        "${type.name} from local port ${detector.probedLocalPort}"
    }

    /**
     * Opens a session. Runs on a background dispatcher whatever the caller is on: connecting touches
     * sockets, and Android throws NetworkOnMainThreadException rather than merely being slow.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun connect(
        target: Target,
        password: String?,
        sink: VideoSink,
        clipboard: ClipboardBridge? = null,
        audio: AudioSink? = null,
        cursor: CursorSink? = null,
        preferences: SessionPreferences = SessionPreferences.defaults(),
        onProgress: (ConnectProgress) -> Unit = {},
        /** Where this viewer's choice of resolution for the host's displays is kept; null keeps nothing. */
        resolutions: ResolutionMemory? = null,
    ): RemoteSession = withContext(Dispatchers.Default) {
        val identity = ControllerIdentity(
            id = "",
            name = deviceName,
            platform = platform,
            version = version,
        )

        val (connection, verifier) = when (target) {
            is Target.ByAddress -> {
                onProgress(ConnectProgress.CONNECTING)
                val connection = PeerConnector().connectDirect(target.host, target.port)
                // No third party vouches for an address, so the key is pinned the first time and compared
                // every time after. That is weaker than an id, and the UI should say so. The pin is filed
                // under the address dialled, matching what the desktop writes into known_hosts.json.
                connection to TofuIdentityVerifier(pinnedKeys, "${target.host}:${target.port}")
            }

            is Target.ById -> {
                // Punching first, relay behind it. The connector decides which, because the choice depends
                // on what the NAT turns out to be and the UI has nothing useful to add.
                val located = PeerConnector().connectById(
                    hostId = target.id,
                    rendezvousServer = target.rendezvousServer,
                    serverPublicKeySpki = target.serverPublicKeySpki,
                    clientVersion = version,
                    nowMillis = monotonicMillis(),
                    forceRelay = target.forceRelay,
                    onProgress = onProgress,
                )
                located.connection to located.verifier
            }
        }

        // The host subscribes a session to its clipboard only if that session asked for it, so asking has
        // to follow from having somewhere to put the text.
        val formats = sink.formats
        val capabilities = ClientCapabilities(
            h264 = VideoFormat.H264 in formats,
            h265 = VideoFormat.H265 in formats,
            vp8 = VideoFormat.VP8 in formats,
            vp9 = VideoFormat.VP9 in formats,
            av1 = VideoFormat.AV1 in formats,
            disableClipboard = clipboard == null,
            // Two reasons to decline, and both have to hold: no sink to play it, or the user said no. The
            // sink is built from the same setting, so these agree — but a host should be told the second
            // reason rather than inferring it from the first.
            disableAudio = audio == null || !preferences.audioEnabled,
            udpMedia = preferences.udpMedia,
            preferences = preferences,
        )
        val session = ControllerSession(connection, identity, capabilities)
        session.handshake(verifier)
        session.login(password) { onProgress(ConnectProgress.WAITING_FOR_APPROVAL) }

        // A net, not a channel. The read loop reports through `state` and `failure`, and the heartbeat and
        // media coroutines catch their own failures, so nothing is expected to reach this. But a root
        // SupervisorJob without a handler routes whatever does escape to the platform's default
        // uncaught-exception handler, and on Android that ends the process — so a bug anywhere in here
        // would present as the app vanishing rather than as a session ending. There is deliberately
        // nothing to report from here: this layer has no logger, and inventing one for a case that should
        // not happen would be the wrong shape.
        val scope = CoroutineScope(
            SupervisorJob() + Dispatchers.Default + CoroutineExceptionHandler { _, _ -> },
        )
        RemoteSession(connection, session, sink, clipboard, audio, cursor, scope, capabilities, resolutions).also { it.start() }
    }

    /**
     * Opens a terminal: a terminal connection to [target] and a shell of [columns] by [rows] on it.
     *
     * Throws [com.sunllo.deskpair.terminal.TerminalNotAllowedException] when the host did not grant a
     * terminal -- switched off in its settings, not ticked when it was approved, or too old to have one --
     * before anything is asked of it: asking anyway is a scope violation that ends the connection.
     */
    @Throws(CancellationException::class, Throwable::class)
    public suspend fun connectTerminal(
        target: Target,
        password: String?,
        columns: Int,
        rows: Int,
        onProgress: (ConnectProgress) -> Unit = {},
    ): com.sunllo.deskpair.terminal.RemoteTerminal = withContext(Dispatchers.Default) {
        val identity = ControllerIdentity(id = "", name = deviceName, platform = platform, version = version)
        val connType = sunllo.rendezvous.ConnType.CONN_TERMINAL
        val (connection, verifier) = when (target) {
            is Target.ByAddress -> {
                onProgress(ConnectProgress.CONNECTING)
                PeerConnector().connectDirect(target.host, target.port) to TofuIdentityVerifier(pinnedKeys, "${target.host}:${target.port}")
            }

            is Target.ById -> {
                val located = PeerConnector().connectById(
                    hostId = target.id,
                    rendezvousServer = target.rendezvousServer,
                    serverPublicKeySpki = target.serverPublicKeySpki,
                    clientVersion = version,
                    nowMillis = monotonicMillis(),
                    forceRelay = target.forceRelay,
                    onProgress = onProgress,
                    connType = connType,
                )
                located.connection to located.verifier
            }
        }

        // No picture, no sound, no clipboard: a terminal connection asks for none of them.
        val capabilities = ClientCapabilities(udpMedia = false, disableAudio = true, disableClipboard = true)
        val session = ControllerSession(connection, identity, capabilities, connType)
        try {
            session.handshake(verifier)
            val info = session.login(password) { onProgress(ConnectProgress.WAITING_FOR_APPROVAL) }
            if (sunllo.messages.Permission.PERM_TERMINAL !in info.granted) {
                throw com.sunllo.deskpair.terminal.TerminalNotAllowedException()
            }
        } catch (e: Throwable) {
            connection.close()
            throw e
        }

        com.sunllo.deskpair.terminal.RemoteTerminal(connection, session, columns, rows).also { it.start() }
    }
}

/**
 * A monotonic millisecond reading, for cache ages rather than for telling the time.
 *
 * Deliberately not a wall clock: the NAT cache only has to know how long ago something happened, and a
 * wall clock can step backwards over a time sync and make "ten minutes ago" look like the future.
 */
private val started = kotlin.time.TimeSource.Monotonic.markNow()

internal fun monotonicMillis(): Long = started.elapsedNow().inWholeMilliseconds
