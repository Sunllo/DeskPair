package com.sunllo.deskpair.android

import android.app.Application
import com.sunllo.deskpair.session.ProtocolVersionException
import com.sunllo.deskpair.session.ProtocolVersionSide
import com.sunllo.deskpair.transport.HostUnreachableException
import com.sunllo.deskpair.transport.UnreachableCause
import android.net.ConnectivityManager
import android.net.Network
import android.util.Log
import android.view.Surface
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.sunllo.deskpair.CanvasState
import com.sunllo.deskpair.ConnectProgress
import com.sunllo.deskpair.CursorSink
import com.sunllo.deskpair.DeskPair
import com.sunllo.deskpair.HostPermissions
import com.sunllo.deskpair.HostPoint
import com.sunllo.deskpair.PointerAction
import com.sunllo.deskpair.PointerButton
import com.sunllo.deskpair.PointerMode
import com.sunllo.deskpair.Quality
import com.sunllo.deskpair.RemoteKey
import com.sunllo.deskpair.RemoteModifier
import com.sunllo.deskpair.RemoteDisplay
import com.sunllo.deskpair.RemoteResolution
import com.sunllo.deskpair.RemoteSession
import com.sunllo.deskpair.Scancodes
import com.sunllo.deskpair.SessionPreferences
import com.sunllo.deskpair.SessionState
import com.sunllo.deskpair.Target
import com.sunllo.deskpair.Touch
import com.sunllo.deskpair.TouchInterpreter
import com.sunllo.deskpair.TouchOutcome
import com.sunllo.deskpair.VideoFormat
import com.sunllo.deskpair.VideoSink
import com.sunllo.deskpair.crypto.HostIdentityChangedException
import com.sunllo.deskpair.session.LoginRefusedException
import com.sunllo.deskpair.store.AppSettings
import com.sunllo.deskpair.store.NetworkDirectoryClient
import com.sunllo.deskpair.store.Targets
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * The three pre-session tabs.
 *
 * The session screen is not one of these: it follows from [UiState], and it takes over the whole window
 * rather than sitting inside the tab bar. Favourites used to be a section on the connect screen; it is a tab
 * of its own because a list someone curates deserves more than the space left over below a form.
 */
enum class Screen { CONNECT, FAVOURITES, SETTINGS }

/** What became of a picture the user offered to the host. */
sealed interface ImageOffer {
    data class Sent(val bytes: Int) : ImageOffer

    /** The host already has this picture — the echo filter recognised it. Not a failure. */
    data object AlreadyThere : ImageOffer

    /** [reason] is empty when there is nothing useful to say beyond "it did not work". */
    data class Failed(val reason: String) : ImageOffer
}

sealed interface UiState {
    /**
     * [dropped] is a session that ended on its own, as against one that never started: the screen says so
     * in the reader's language rather than passing on whatever the socket called it. [overRelay] says the
     * app is coming back on the relay, because the path it was using stopped carrying.
     */
    data class Disconnected(
        val error: String? = null,
        val dropped: Boolean = false,
        val overRelay: Boolean = false,
    ) : UiState

    /** A state rather than a sentence, so the screen can say it in the reader's language. */
    data class Connecting(val progress: ConnectProgress) : UiState

    /** A terminal on the host is open; [SessionViewModel.terminal] is the session. */
    data class Terminal(val hostname: String, val platform: String) : UiState
    data class Connected(
        val hostname: String,
        val platform: String,
        val transport: String,
        val width: Int,
        val height: Int,
        val displays: Int,
        /** Null until the host's first round-trip report arrives. */
        val roundTripMillis: Int? = null,
        /** The session broke. The picture is the last one that arrived. */
        val stalled: Boolean = false,
    ) : UiState {
        /** Falls back to 16:9 when the host reported no display, rather than dividing by zero. */
        val aspectRatio: Float get() = if (width > 0 && height > 0) width.toFloat() / height else 16f / 9f
    }
}

/**
 * Holds the session and turns it into something a screen can render.
 *
 * The decoder is created when the Surface appears and destroyed when it goes, which is not the same
 * lifetime as the session: rotating the device or backgrounding the app takes the surface away while the
 * connection stays up, and frames that arrive in that window are dropped rather than queued.
 */
class SessionViewModel(application: Application) : AndroidViewModel(application) {

    private companion object {
        const val TAG = "DeskPair"

        /** Enough for a handover or a lift; few enough that a host that has gone is left alone. */
        const val MAX_RETRIES = 3

        /**
         * How long the waiting screen spins before it stops merely spinning and says what is likely wrong.
         *
         * Short enough that nobody sits through it wondering; long enough that an ordinary first keyframe
         * over a relay is never called late.
         */
        const val LATE_AFTER_MILLIS = 6_000L
    }

    private val _ui = MutableStateFlow<UiState>(UiState.Disconnected())
    val ui: StateFlow<UiState> = _ui.asStateFlow()

    /** Settings, favourites and secrets. Owned here because connecting needs them and reports back to them. */
    val store = AppStore(application)

    private val client = DeskPair(
        deviceName = android.os.Build.MODEL ?: "Android",
        // The desktop already knows this string and draws an icon for it.
        platform = "Android",
        // Keystore-backed, so a pinned host key survives the app being closed and TOFU means something.
        pinnedKeys = store.pinnedKeys,
    )

    private val _screen = MutableStateFlow(Screen.CONNECT)

    /** Which pre-session tab is showing. The session screen takes over from [ui], not from this. */
    val screen: StateFlow<Screen> = _screen.asStateFlow()

    fun show(screen: Screen) {
        _screen.value = screen
    }

    /** What the host is trusted under, for the settings screen to list and forget. */
    fun trustedHosts() = client.trustedHosts

    private var session: RemoteSession? = null

    /** The host's sound. Created up front so a test can read its counters without a session. */
    private val audio = AndroidAudio().apply {
        onProblem = { reason -> _audioProblem.value = reason }
    }

    private val _audioProblem = MutableStateFlow<String?>(null)
    private val _hasPicture = MutableStateFlow(false)

    /**
     * Whether a frame has ever been drawn on this session.
     *
     * A session reaching "connected" means the two ends have agreed -- on a codec, a size, a route -- not
     * that there is anything to look at yet. Those are seconds apart on a slow link, and on a host that
     * cannot read its own desktop they never converge at all. The screen showed its black backdrop for the
     * whole of that gap, which looks identical to a session that has broken.
     */
    val hasPicture: StateFlow<Boolean> = _hasPicture.asStateFlow()

    private val _pictureIsLate = MutableStateFlow(false)

    /** Set once waiting has gone on long enough to be worth explaining rather than merely animating. */
    val pictureIsLate: StateFlow<Boolean> = _pictureIsLate.asStateFlow()

    private var lateTimer: Job? = null

    /**
     * Back to waiting, and start the clock again.
     *
     * Called when a session opens and whenever the stream restarts underneath it -- a display switch, a
     * resolution change, a codec renegotiation. Each of those blanks the picture for a moment, and each of
     * them used to blank it to black with no explanation.
     */
    private fun expectPicture() {
        _hasPicture.value = false
        _pictureIsLate.value = false
        lateTimer?.cancel()
        lateTimer = viewModelScope.launch {
            delay(LATE_AFTER_MILLIS)
            if (!_hasPicture.value) {
                _pictureIsLate.value = true
            }
        }
    }

    private val _videoProblem = MutableStateFlow<String?>(null)

    /**
     * Parts of the session that quietly do not work, for the screen to say so.
     *
     * Both were write-only fields before: `SurfaceVideoSink.lastError` and the audio thread's `Log.w`. A
     * device with no H.264 decoder showed a black picture under a healthy green signal chip and said
     * nothing, which is the hardest kind of failure to be asked about afterwards.
     */
    val audioProblem: StateFlow<String?> = _audioProblem.asStateFlow()
    val videoProblem: StateFlow<String?> = _videoProblem.asStateFlow()

    private val _permissions = MutableStateFlow(HostPermissions())

    /** What the host currently allows, so the screen can stop offering what it will refuse. */
    val permissions: StateFlow<HostPermissions> = _permissions.asStateFlow()

    private val _display = MutableStateFlow(0)

    /** Which display the host confirmed it is sending, rather than which one was asked for. */
    val display: StateFlow<Int> = _display.asStateFlow()

    private val _displays = MutableStateFlow<List<RemoteDisplay>>(emptyList())

    /** The host's displays as they are now, with the modes each can be switched to. */
    val displays: StateFlow<List<RemoteDisplay>> = _displays.asStateFlow()

    private val _resolutionFailure = MutableStateFlow<String?>(null)

    /** Why the last resolution request was refused, for the screen panel to say so. */
    val resolutionFailure: StateFlow<String?> = _resolutionFailure.asStateFlow()

    private val _hostNotice = MutableStateFlow<String?>(null)

    /** The host's word on why it has no screen to show: asking its person, declined, stopped there. */
    val hostNotice: StateFlow<String?> = _hostNotice.asStateFlow()

    /**
     * Both directions of the clipboard. It is only listened to while a session is up: a client that watched
     * the clipboard all the time would be reading everything the user copies, for nothing.
     */
    private val clipboard = AndroidClipboard(
        context = application,
        onLocalText = { text ->
            val open = session ?: return@AndroidClipboard
            viewModelScope.launch {
                runCatching { open.sendClipboardText(text) }
                    .onFailure { Log.w(TAG, "Could not offer the clipboard to the host", it) }
            }
        },
        onLocalImage = { png -> offerImage(png) },
    )

    @Volatile
    private var sink: SurfaceVideoSink? = null

    // ---- the canvas: where the picture sits and how big it is drawn ----

    private val _canvas = MutableStateFlow(CanvasState.initial())
    val canvas: StateFlow<CanvasState> = _canvas.asStateFlow()

    private val touch = TouchInterpreter()

    private val _pointerMode = MutableStateFlow(PointerMode.TOUCH)
    val pointerMode: StateFlow<PointerMode> = _pointerMode.asStateFlow()

    private val _cursor = MutableStateFlow<RemoteCursorState?>(null)
    val cursor: StateFlow<RemoteCursorState?> = _cursor.asStateFlow()

    private val _showRemoteCursor = MutableStateFlow(true)
    val showRemoteCursor: StateFlow<Boolean> = _showRemoteCursor.asStateFlow()

    private val cursorSink = object : CursorSink {
        override fun onCursorShape(id: Long, hotX: Int, hotY: Int, width: Int, height: Int, bgra: ByteArray) {
            _cursor.value = (_cursor.value ?: RemoteCursorState.empty())
                .withShape(id, hotX, hotY, width, height, bgra)
        }

        override fun onCursorPosition(x: Int, y: Int) {
            // The host's own report, not a guess. In relative mode the host applies acceleration this
            // side cannot model, so a local estimate drifts within seconds.
            touch.cursor = HostPoint(x, y)
            _cursor.value = (_cursor.value ?: RemoteCursorState.empty()).at(x, y)
        }

        override fun onCursorShapeChanged(id: Long) {
            _cursor.value = _cursor.value?.usingShape(id)
        }
    }

    fun setPointerMode(mode: PointerMode) {
        _pointerMode.value = mode
        touch.mode = mode
    }

    fun setShowRemoteCursor(show: Boolean) {
        _showRemoteCursor.value = show
    }

    /** The view showing the picture changed size — rotation, a keyboard, a split screen. */
    fun viewResized(width: Float, height: Float) {
        _canvas.value = _canvas.value.resized(width, height)
    }

    /** Back to the whole desktop, centred. The escape hatch for a canvas panned into nowhere. */
    fun resetCanvas() {
        _canvas.value = _canvas.value.fitted()
    }

    /**
     * Zooms about the middle of the view, for a driven run.
     *
     * A pinch cannot be scripted through `adb shell input`, and what a tap maps to after a zoom is the
     * part of this worth measuring — so the canvas operation is reachable without the fingers.
     */
    fun zoomForTest(factor: Float) {
        val c = _canvas.value
        _canvas.value = c.zoomed(factor, c.viewWidth / 2f, c.viewHeight / 2f)
        Log.i(TAG, "canvas now scale=${_canvas.value.scale} at=${_canvas.value.x},${_canvas.value.y}")
    }

    fun actualSize() {
        _canvas.value = _canvas.value.actualSize()
    }

    /** The host's own resolution, which is what pointer coordinates are in. */
    @Volatile
    private var remoteWidth = 0

    @Volatile
    private var remoteHeight = 0

    private val videoSink = object : VideoSink {
        override val formats get() = SurfaceVideoSink.formats

        override fun onFrame(data: ByteArray, isKeyFrame: Boolean, width: Int, height: Int, format: VideoFormat): Boolean {
            remoteWidth = width
            remoteHeight = height
            _canvas.value = CanvasState.forHost(_canvas.value, width, height)

            // No surface yet. Say the frame was handled: asking the host for a keyframe would not conjure
            // one up, and a burst of refresh requests while the app is backgrounded helps nobody.
            val decoder = sink ?: return true
            val decoded = decoder.decode(data, isKeyFrame, width, height, format)
            if (decoded) {
                // The first one the decoder takes is the moment there is something to look at. A frame it
                // refused is not a picture, so the waiting screen stays up rather than uncovering black.
                _hasPicture.value = true
                _pictureIsLate.value = false
                lateTimer?.cancel()
            }

            return decoded
        }
    }

    /**
     * Connects to whatever the user typed.
     *
     * One entry point, not two, because the home screen has one text box. Which kind of target it is comes
     * from [Targets.isDirect], ported from `PeerConnector.IsDirectTarget` so that a string means the same
     * thing here as it does on the desktop — including the part where a nine-digit id is recognised before
     * anything tries to read it as an address.
     */
    fun connect(typed: String, password: String, remember: Boolean = false) {
        val target = Targets.normalise(typed)
        if (target.isEmpty()) {
            return
        }

        val settings = store.current
        lastTarget = LastTarget(target, password)
        _canReconnect.value = false
        _ui.value = UiState.Connecting(ConnectProgress.CONNECTING)

        viewModelScope.launch {
            try {
                // Which signalling server, resolved once per attempt. A code scanned for this desk wins
                // for this desk; otherwise a configured address wins; otherwise the portal is asked. An
                // address typed into an address bar needs none of this, which is why Targets.resolve
                // still decides what kind of target it is.
                val directory = store.scannedNetwork(target) ?: NetworkDirectoryClient.resolve(settings)
                val opened = client.connect(
                    target = Targets.resolve(
                        typed = target,
                        rendezvousServer = directory.rendezvous,
                        serverPublicKeyBase64 = directory.publicKey,
                        // Either the setting asks for it, or the last attempt proved this network's
                        // punched path does not survive being used.
                        forceRelay = settings.forceRelay || retryOverRelay,
                    ),
                    password = password.ifEmpty { null },
                    sink = videoSink,
                    clipboard = clipboard,
                    // A session that never asked for sound is never sent any, so this is a subscription
                    // rather than a mute button.
                    audio = if (settings.audioEnabled) audio else null,
                    cursor = cursorSink,
                    // What the settings screen has been collecting all along. Until now none of it
                    // reached a session: quality, custom bitrate and frame rate, the cursor option, the
                    // refinement switch and the UDP switch were stored, shown, and read by nobody.
                    preferences = SessionPreferences.from(settings).also { preferences = it },
                    onProgress = { _ui.value = UiState.Connecting(it) },
                    resolutions = store.resolutionsFor(target),
                )

                session = opened
                wantsRetry = false
                _canReconnect.value = false
                retries = 0
                // Whatever path this one took, it worked; the next fresh attempt starts from the settings.
                retryOverRelay = false
                _audioProblem.value = null
                _videoProblem.value = null
                expectPicture()

                // Two more settings that never left the settings screen, and neither of them is the
                // host's business: how fingers are interpreted, and how the picture is framed.
                setPointerMode(
                    if (settings.pointerMode == "mouse") PointerMode.MOUSE else PointerMode.TOUCH,
                )
                _showRemoteCursor.value = settings.showRemoteCursor
                _canvas.value = if (settings.fitToWindow) _canvas.value.fitted() else _canvas.value.actualSize()
                if (remember) {
                    store.rememberPassword(target, password)
                }

                store.rememberConnection(target, opened.host.hostname, opened.host.platform)

                clipboard.start()
                // Whatever is already on the clipboard when the session opens: without this the first copy
                // only reaches the host if the user copies it again after connecting.
                clipboard.currentText()?.let { text ->
                    runCatching { opened.sendClipboardText(text) }
                }

                Log.i(TAG, "reached ${opened.host.hostname} over ${opened.transport}")
                _ui.value = connectedState(opened)
                follow(opened)
            } catch (e: Throwable) {
                Log.w(TAG, "Connection failed", e)
                // A password that turns out to be wrong must not stay remembered, or the app would go on
                // failing silently with a stored value the user cannot see.
                if (e is LoginRefusedException) {
                    store.forgetPassword(target)
                }

                // The key this machine used to present is not the key it presents now. That is either a
                // reinstall or an impersonation, and only the person can tell which -- so they are asked,
                // here, where it happened. There used to be a list of fingerprints in settings for this,
                // which is a strange place to answer a question nobody knew they had been asked.
                if (e is HostIdentityChangedException) {
                    _keyChanged.value = KeyChange(e.hostId, e.pinnedFingerprint, e.offeredFingerprint, password)
                }

                _ui.value = UiState.Disconnected(describe(e))
            }
        }
    }

    /**
     * What went wrong, in the reader's language. The shared module throws typed errors and English
     * sentences; the sentence is for the log, the type is for this. Anything untyped is shown inside one
     * localised frame with the English detail, which is still better than the detail alone.
     */
    private fun describe(e: Throwable): String {
        val app = getApplication<Application>()
        return when (e) {
            is ProtocolVersionException -> when (e.side) {
                ProtocolVersionSide.HOST_TOO_OLD -> app.getString(R.string.session_host_too_old)
                ProtocolVersionSide.CLIENT_TOO_OLD -> app.getString(R.string.session_client_too_old)
            }
            is HostUnreachableException -> when (e.why) {
                UnreachableCause.UNKNOWN_ID -> app.getString(R.string.session_unreachable_unknown_id, e.hostId)
                UnreachableCause.OFFLINE -> app.getString(R.string.session_unreachable_offline, e.hostId)
                UnreachableCause.SERVER_BUSY -> app.getString(R.string.session_unreachable_busy)
                UnreachableCause.NO_RELAY -> app.getString(R.string.session_unreachable_no_relay, e.hostId)
                UnreachableCause.NO_ANSWER -> app.getString(R.string.session_unreachable_no_answer)
            }
            is LoginRefusedException, is HostIdentityChangedException -> e.message ?: e::class.simpleName.orEmpty()
            else -> app.getString(R.string.session_failed_detail, e.message ?: e::class.simpleName.orEmpty())
        }
    }

    /**
     * Offers whatever is on the clipboard now.
     *
     * The change listener only fires while this app has focus, so a copy made in another app never reaches
     * it. Calling this when the session comes back to the foreground closes that gap, and the session's own
     * echo filter makes a repeat offer free.
     */
    fun offerClipboard() {
        if (session == null) {
            return
        }

        clipboard.offerCurrent()
    }

    /**
     * Puts a picture on the host's clipboard.
     *
     * Used by both paths that can produce one: something copied on the phone, and something chosen from
     * the photo picker. [imageSent] is what the screen says afterwards — a picture that arrives with no
     * acknowledgement looks exactly like one that did not.
     */
    fun offerImage(png: ByteArray) {
        val open = session ?: return
        Log.i(TAG, "offering a ${png.size}-byte picture to the host")
        viewModelScope.launch {
            val result = runCatching { open.sendClipboardImage(png) }
            result
                .onSuccess { sent -> _imageSent.value = if (sent) ImageOffer.Sent(png.size) else ImageOffer.AlreadyThere }
                .onFailure {
                    Log.w(TAG, "Could not offer the picture to the host", it)
                    _imageSent.value = ImageOffer.Failed(it.message.orEmpty())
                }
        }
    }

    /** Reads a picture the user chose and offers it. Decoding happens off the main thread. */
    fun offerPickedImage(uri: android.net.Uri) {
        if (session == null) {
            return
        }

        Log.i(TAG, "picked $uri")
        viewModelScope.launch {
            val png = withContext(Dispatchers.IO) { clipboard.readPicked(uri) }
            if (png == null) {
                _imageSent.value = ImageOffer.Failed("")
                return@launch
            }

            offerImage(png)
        }
    }

    private val _imageSent = MutableStateFlow<ImageOffer?>(null)

    /** What became of the last picture offered, for the screen to show and then clear. */
    val imageSent: StateFlow<ImageOffer?> = _imageSent.asStateFlow()

    fun clearImageNotice() {
        _imageSent.value = null
    }

    /**
     * Asks what kind of network this device is on. Worth surfacing in a real settings screen too: it is
     * the difference between a session that runs at link speed and one that goes through a relay.
     */
    fun probeNetwork(rendezvousServer: String) {
        viewModelScope.launch {
            val answer = runCatching { client.describeNetwork(rendezvousServer) }
                .getOrElse { "probe failed: ${it.message}" }
            Log.i(TAG, "network: $answer")
        }
    }

    private fun connectedState(opened: RemoteSession) = UiState.Connected(
        hostname = opened.host.hostname,
        platform = opened.host.platform,
        transport = opened.mediaPath.value ?: opened.transport,
        width = opened.host.displays.firstOrNull()?.width ?: 0,
        height = opened.host.displays.firstOrNull()?.height ?: 0,
        displays = opened.host.displays.size,
    )

    /**
     * Follows a session after it opens.
     *
     * Two things the UI could not know before. The datagram channel is offered seconds after login, so the
     * transport read at connect time is always the TCP one; and a session that drops left the app sitting
     * on a frozen frame with no message and no way back.
     */
    private fun follow(opened: RemoteSession) {
        viewModelScope.launch {
            opened.mediaPath.collect { path ->
                val current = _ui.value
                if (current is UiState.Connected) {
                    _ui.value = current.copy(transport = path ?: opened.transport)
                }
            }
        }

        viewModelScope.launch {
            opened.roundTripMillis.collect { rtt ->
                val current = _ui.value
                if (current is UiState.Connected) {
                    _ui.value = current.copy(roundTripMillis = rtt)
                }
            }
        }

        viewModelScope.launch { opened.permissions.collect { _permissions.value = it } }
        viewModelScope.launch { opened.display.collect { _display.value = it } }
        viewModelScope.launch { opened.resolutionFailure.collect { _resolutionFailure.value = it } }
        viewModelScope.launch { opened.hostNotice.collect { _hostNotice.value = it } }
        viewModelScope.launch {
            opened.displays.collect { now ->
                _displays.value = now
                // The size the picture is framed for follows the display being shown.
                val current = _ui.value
                val shown = now.getOrNull(_display.value)
                if (current is UiState.Connected && shown != null) {
                    _ui.value = current.copy(width = shown.width, height = shown.height, displays = now.size)
                }
            }
        }

        // A stale picture, which recovers on its own. Not the same as the session ending, which is below.
        viewModelScope.launch {
            opened.stalled.collect { stalled ->
                val current = _ui.value
                if (current is UiState.Connected) {
                    _ui.value = current.copy(stalled = stalled)
                }
            }
        }

        viewModelScope.launch {
            opened.state.collect { state ->
                val current = _ui.value
                if (current !is UiState.Connected) {
                    return@collect
                }

                when (state) {
                    SessionState.CONNECTED -> Unit
                    // Both of these end the session. The difference is only whether anyone expected it,
                    // and `failure` is where the host's own reason lands when it gave one — without it a
                    // deliberate disconnect from the other end looked exactly like a severed cable.
                    // Only a failure is worth coming back from. A host that closed the session politely
                    // has said what it wants, and reconnecting over the top of that would be rude.
                    SessionState.FAILED -> {
                        // A punched path that dies is a punched path to stop using.
                        val direct = opened.transport != "RELAY"
                        retryOverRelay = direct
                        wantsRetry = lastTarget != null
                        _canReconnect.value = lastTarget != null

                        // What a person can do something about, rather than what the socket said. The
                        // reason underneath -- "recv failed: Resource temporarily unavailable" -- is a
                        // read that returned nothing on a path that had stopped passing anything, which
                        // is true and is not a sentence anybody should be shown.
                        Log.w(TAG, "session failed: ${opened.failure}")
                        _ui.value = UiState.Disconnected(dropped = true, overRelay = direct)

                        // Straight away, and only for the path that has just proved itself: the retry
                        // that waits for the network to come back cannot help here, because nothing
                        // about the network changed. The punch simply stopped carrying.
                        if (direct && retries < MAX_RETRIES && lastTarget != null) {
                            retries++
                            viewModelScope.launch {
                                delay(600)
                                if (_ui.value is UiState.Disconnected) {
                                    reconnect()
                                }
                            }
                        }
                    }
                    // Politely closed from the other end, so there is nothing here to recover: the offer
                    // goes away with the session, and the desk is still one tap away in the list.
                    SessionState.CLOSED -> {
                        _canReconnect.value = false
                        _ui.value = UiState.Disconnected(opened.failure)
                    }
                }
            }
        }
    }

    /**
     * Reconnects using the last target that worked.
     *
     * A phone loses its network constantly — a lift, a tunnel, a handover between cells — and making the
     * user retype an address every time is the difference between a tool they keep and one they uninstall.
     */
    fun reconnect() {
        val last = lastTarget ?: return
        connect(last.target, last.password)
    }

    /**
     * Whether a session ended for a reason the user did not choose, so the network coming back is worth
     * acting on. Cleared by a deliberate disconnect, because "I pressed stop" is not something to undo.
     */
    private var wantsRetry = false

    /** How many times this drop has been retried, so a host that is simply gone is not chased for ever. */
    private var retries = 0

    /**
     * Comes back by itself when the network does.
     *
     * The comment on [reconnect] has always argued that retyping an address after every lift and tunnel is
     * what makes someone uninstall a tool — and then the app shipped a button. The host gives up on a
     * silent peer after thirty seconds (`ProtocolConstants.SessionReadTimeout`), so this is a fresh
     * session rather than a resumed one; what it saves is the typing, not the state.
     *
     * Bounded at three. A network that flaps, or a host that has genuinely gone, must not turn into an
     * endless reconnect loop chewing the battery in someone's pocket.
     */
    private val networks = application.getSystemService(ConnectivityManager::class.java)

    private val onNetwork = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            if (!wantsRetry || retries >= MAX_RETRIES || _ui.value !is UiState.Disconnected) {
                return
            }

            retries++
            viewModelScope.launch { reconnect() }
        }
    }

    init {
        // Needs ACCESS_NETWORK_STATE, which is why the manifest grew a permission for this.
        runCatching { networks?.registerDefaultNetworkCallback(onNetwork) }
    }

    /**
     * Whether the connect screen should offer to try the last session again.
     *
     * Held in state rather than derived from [lastTarget], which every attempt sets and nothing clears:
     * "Try again" therefore appeared after the first connection of the run and stayed for the rest of it,
     * offering to undo a disconnection the user had just asked for. It follows the same rule the automatic
     * retry does -- only a session that ended in a way nobody chose is worth coming back from.
     *
     * A StateFlow rather than a plain getter for a second reason: a getter over a field Compose is not
     * watching never recomposes, so even the right answer would have reached the screen only by accident.
     */
    /** A machine whose key has changed, waiting for somebody to say whether that was expected. */
    private val _keyChanged = MutableStateFlow<KeyChange?>(null)
    val keyChanged: StateFlow<KeyChange?> = _keyChanged.asStateFlow()

    /** What the prompt needs: which machine, the two fingerprints, and the password to reuse on the retry. */
    data class KeyChange(
        val target: String,
        val was: String,
        val now: String,
        val password: String,
        /** Whether the attempt that met the new key was for a terminal, so the retry is one too. */
        val terminal: Boolean = false,
    )

    /**
     * Accepts the new key and connects again.
     *
     * Forgetting the pin is what lets the next attempt through: the verifier records whatever it is offered
     * when there is nothing on file, which is what happened the first time this machine was ever reached.
     */
    fun trustChangedKey(change: KeyChange) {
        store.pinnedKeys.forget(change.target)
        _keyChanged.value = null
        if (change.terminal) {
            openTerminal(change.target, change.password)
        } else {
            connect(change.target, change.password)
        }
    }

    fun dismissKeyChange() {
        _keyChanged.value = null
    }

    private val _canReconnect = MutableStateFlow(false)
    val canReconnect: StateFlow<Boolean> = _canReconnect.asStateFlow()

    private data class LastTarget(val target: String, val password: String)

    private var lastTarget: LastTarget? = null

    /**
     * Whether the next attempt should go through the relay whatever the settings say.
     *
     * Set when a session dies on a punched path without the host having closed it. A hole punched through
     * two NATs can complete a TCP handshake, carry a login and twenty seconds of video, and then stop
     * passing anything in either direction -- the host sees no acknowledgements and eventually a read
     * timeout, and this end sees a picture that froze. None of that is visible when the connection is
     * made, so it cannot be decided in advance; only the attempt after it can answer. The relay is slower
     * and costs somebody bandwidth, and it works.
     */
    private var retryOverRelay = false

    /**
     * Asks the host for a different picture. An option it may decline, not a command.
     *
     * Restates every option, not just the quality: the host replaces its whole copy of them and re-derives
     * the session's permissions, so anything left out comes back on. Sending three fields here used to
     * re-enable the host's audio capture.
     */
    fun setQuality(quality: Quality) {
        val open = session ?: return
        preferences = preferences.copy(quality = quality)
        val wanted = preferences
        viewModelScope.launch { runCatching { open.setPreferences(wanted) } }
    }

    /** The last complete answer given to the host, so an update can restate it. */
    private var preferences: SessionPreferences = SessionPreferences.defaults()

    fun switchDisplay(index: Int) {
        val open = session ?: return
        viewModelScope.launch { runCatching { open.switchDisplay(index) } }
    }

    /** Asks the host to switch a display to one of its advertised modes, or back to its original. */
    fun setResolution(display: Int, resolution: RemoteResolution?) {
        val open = session ?: return
        viewModelScope.launch { runCatching { open.setResolution(display, resolution) } }
    }

    /** An address for now; a nine-digit id needs a rendezvous server to be configured first. */
    private val _modifiers = MutableStateFlow<Set<RemoteModifier>>(emptySet())

    /**
     * Modifiers the user has latched on the bar. A phone has no way to hold Ctrl and press C at the same
     * time, so the bar holds it instead and the next key carries it.
     */
    val modifiers: StateFlow<Set<RemoteModifier>> = _modifiers.asStateFlow()

    fun toggleModifier(modifier: RemoteModifier) {
        _modifiers.value = _modifiers.value.let { if (modifier in it) it - modifier else it + modifier }
    }

    /** Sends a named key with whatever the bar is holding, then releases the bar. */
    fun pressKey(key: RemoteKey) {
        val session = session ?: return
        val held = _modifiers.value
        viewModelScope.launch {
            runCatching { session.sendKey(key, held) }
            // Latched modifiers apply to one key, like a sticky-keys setting: holding them across every
            // later keystroke is never what someone meant.
            _modifiers.value = emptySet()
        }
    }

    /**
     * Sends what the user typed.
     *
     * With a modifier latched, a single character is a shortcut rather than text: latch Ctrl, type c, and
     * the host receives Ctrl+C. Sending the letter instead would type a c into whatever has focus, which is
     * never what someone holding Ctrl meant.
     */
    fun typeText(text: String) {
        val session = session ?: return
        if (text.isEmpty()) return

        val held = _modifiers.value
        if (held.isNotEmpty() && text.length == 1 && Scancodes.of(text[0]) != null) {
            pressShortcut(text[0])
            return
        }

        viewModelScope.launch { runCatching { session.sendText(text) } }
    }

    /** A character key pressed with modifiers held, such as Ctrl+C. */
    fun pressShortcut(character: Char) {
        val session = session ?: return
        val held = _modifiers.value
        viewModelScope.launch {
            runCatching { session.sendShortcut(character, held) }
            _modifiers.value = emptySet()
        }
    }

    /**
     * True while the app's own typing field has focus. Keys then belong to it, not to the host.
     *
     * Without this the field is unusable: every keystroke is forwarded to the remote machine before the
     * field ever sees it, so nothing can be typed into the app at all with a keyboard attached.
     */
    @Volatile
    var typingFieldFocused: Boolean = false

    /** A hardware key, routed by whether it prints a character or has a name. */
    fun onHardwareKey(event: android.view.KeyEvent): Boolean {
        _terminal.value?.let { return terminalKey(it, event) }

        if (typingFieldFocused) {
            return false
        }

        val session = session ?: return false
        if (event.action != android.view.KeyEvent.ACTION_DOWN) {
            // Only downs are forwarded: the host reproduces a complete press, and forwarding the up as well
            // would type everything twice.
            return event.action == android.view.KeyEvent.ACTION_UP && AndroidKeys.namedKey(event.keyCode) != null
        }

        val modifiers = AndroidKeys.modifiers(event) + _modifiers.value

        AndroidKeys.namedKey(event.keyCode)?.let { key ->
            viewModelScope.launch { runCatching { session.sendKey(key, modifiers) } }
            return true
        }

        AndroidKeys.scancode(event)?.let { scancode ->
            viewModelScope.launch { runCatching { session.sendScancode(scancode, modifiers) } }
            return true
        }

        return false
    }

    fun attachSurface(surface: Surface) {
        sink = SurfaceVideoSink(surface).apply {
            onProblem = { reason -> _videoProblem.value = reason }
        }
        Log.i(TAG, "Surface attached")

        // A fresh decoder can use nothing but a keyframe, and it gets a fresh one every time the canvas
        // resizes the view — resizing a SurfaceView destroys and recreates its surface. Without asking,
        // the picture stays black until the host happens to send the next keyframe on its own schedule.
        refreshVideo()
    }

    fun detachSurface() {
        sink?.close()
        sink = null
        Log.i(TAG, "Surface detached")
    }

    /** How many frames the decoder has actually put on screen. The instrumented test asserts on this. */
    fun renderedFrames(): Int = sink?.renderedFrames ?: 0

    fun framesReceived(): Int = session?.videoFramesReceived ?: 0

    /**
     * Maps a touch to the host's coordinate space. The view is letterboxed, so the scale is the same in both
     * axes and the offset is whatever is left over — using the view's own aspect ratio instead would put the
     * pointer somewhere the user did not touch.
     */
    // ---- gestures ----

    /**
     * The fingers changed. Everything about what a gesture *means* lives in the shared interpreter; this
     * only forwards what it decides, so the two apps cannot drift apart again.
     */
    fun onTouch(pointers: List<Touch>) = apply(touch.update(pointers, _canvas.value))


    fun onDoubleTapDrag(x: Float, y: Float) = apply(touch.doubleTapDragStart(x, y, _canvas.value))


    /** A phone call, a system gesture, the app backgrounded — a held button must not be left held. */
    fun onTouchCancelled() = apply(touch.cancel())

    private fun apply(outcomes: List<TouchOutcome>) {
        if (outcomes.isEmpty()) {
            return
        }

        val open = session
        for (outcome in outcomes) {
            when (outcome) {
                is TouchOutcome.PanCanvas -> _canvas.value = _canvas.value.panned(outcome.dx, outcome.dy)
                is TouchOutcome.ZoomCanvas ->
                    _canvas.value = _canvas.value.zoomed(outcome.factor, outcome.focalX, outcome.focalY)

                is TouchOutcome.Pointer -> open?.let {
                    viewModelScope.launch {
                        runCatching { it.sendPointer(outcome.action, outcome.button, outcome.x, outcome.y) }
                    }
                }

                is TouchOutcome.RelativePointer -> open?.let {
                    viewModelScope.launch {
                        runCatching {
                            it.sendPointer(PointerAction.MOVE_RELATIVE, PointerButton.NONE, outcome.dx, outcome.dy)
                        }
                    }
                }

                is TouchOutcome.Scroll -> open?.let {
                    viewModelScope.launch {
                        runCatching {
                            it.sendPointer(PointerAction.WHEEL, PointerButton.NONE, 0, outcome.ticks)
                        }
                    }
                }
            }
        }
    }

    /** Asks the host for a fresh keyframe — the picture can be wrong without anything being broken. */
    /** The activity is leaving the screen or back on it; see RemoteSession.setBackgrounded. */
    fun setBackgrounded(background: Boolean) {
        session?.setBackgrounded(background)
    }

    fun refreshVideo() {
        val open = session ?: return
        viewModelScope.launch { runCatching { open.refreshVideo() } }
    }

    private val _terminal = MutableStateFlow<com.sunllo.deskpair.terminal.RemoteTerminal?>(null)

    /** The open terminal, if the app is showing one instead of a desktop. */
    val terminal: StateFlow<com.sunllo.deskpair.terminal.RemoteTerminal?> = _terminal.asStateFlow()

    /**
     * Opens a terminal on [typed]. The same way to the host as [connect] -- the same server, the same
     * password rules -- but a terminal connection, which asks for no picture and gets a shell.
     */
    fun openTerminal(typed: String, password: String, remember: Boolean = false) {
        val target = Targets.normalise(typed)
        if (target.isEmpty()) {
            return
        }

        val settings = store.current
        _ui.value = UiState.Connecting(ConnectProgress.CONNECTING)
        viewModelScope.launch {
            try {
                val directory = store.scannedNetwork(target) ?: NetworkDirectoryClient.resolve(settings)
                val opened = client.connectTerminal(
                    target = Targets.resolve(
                        typed = target,
                        rendezvousServer = directory.rendezvous,
                        serverPublicKeyBase64 = directory.publicKey,
                        forceRelay = settings.forceRelay,
                    ),
                    password = password.ifEmpty { null },
                    columns = 80,
                    rows = 24,
                    onProgress = { _ui.value = UiState.Connecting(it) },
                )
                if (remember) {
                    store.rememberPassword(target, password)
                }

                store.rememberConnection(target, opened.host.hostname, opened.host.platform)
                _terminal.value = opened
                _ui.value = UiState.Terminal(opened.host.hostname, opened.host.platform)
            } catch (e: Throwable) {
                Log.w(TAG, "Terminal failed", e)
                if (e is LoginRefusedException) {
                    store.forgetPassword(target)
                }

                if (e is HostIdentityChangedException) {
                    _keyChanged.value = KeyChange(e.hostId, e.pinnedFingerprint, e.offeredFingerprint, password, terminal = true)
                }

                _ui.value = UiState.Disconnected(
                    if (e is com.sunllo.deskpair.terminal.TerminalNotAllowedException) {
                        getApplication<Application>().getString(R.string.terminal_not_allowed)
                    } else {
                        describe(e)
                    },
                )
            }
        }
    }

    fun closeTerminal() {
        _terminal.value?.close()
        _terminal.value = null
        _ui.value = UiState.Disconnected()
    }

    /**
     * A hardware key typed into the terminal: named keys by the shared table, Ctrl and Alt combinations as
     * chords, and anything that prints as the character it prints. Ups are consumed too, so nothing
     * reaches the views underneath.
     */
    private fun terminalKey(terminal: com.sunllo.deskpair.terminal.RemoteTerminal, event: android.view.KeyEvent): Boolean {
        if (event.action != android.view.KeyEvent.ACTION_DOWN) {
            return true
        }

        val shift = event.isShiftPressed
        val alt = event.isAltPressed
        val control = event.isCtrlPressed
        TerminalKeyMap.named(event.keyCode)?.let {
            terminal.key(it, shift, alt, control)
            return true
        }

        if (control || alt) {
            val base = event.getUnicodeChar(0)
            if (base > 0) {
                terminal.chord(base.toChar().toString(), alt = alt, control = control)
                return true
            }
        }

        val printed = event.unicodeChar
        if (printed > 0) {
            terminal.type(String(Character.toChars(printed)))
            return true
        }

        return false
    }

    fun disconnect() {
        wantsRetry = false
        _canReconnect.value = false
        val closing = session
        session = null
        clipboard.stop()
        audio.stop()
        _cursor.value = null
        _ui.value = UiState.Disconnected()
        viewModelScope.launch { runCatching { closing?.close() } }
    }

    /**
     * The last thing this ViewModel does, and the one place `viewModelScope` cannot be used.
     *
     * That scope is registered as a Closeable in the ViewModel's tag bag and is cancelled *before*
     * `onCleared` runs, so the launch that used to be here never executed: the socket stayed open and the
     * host was never told the session had gone. Closing is a suspend function because it sends a goodbye
     * first, so it needs a scope that outlives this object — and nothing else does, by definition.
     */
    override fun onCleared() {
        wantsRetry = false
        runCatching { networks?.unregisterNetworkCallback(onNetwork) }
        _terminal.value?.close()
        _terminal.value = null
        val closing = session
        session = null
        clipboard.stop()
        audio.stop()
        detachSurface()
        closing?.let { open ->
            CoroutineScope(SupervisorJob() + Dispatchers.Default).launch {
                runCatching { open.close() }
            }
        }
    }
}
