package com.sunllo.deskpair.android

import android.os.Bundle
import androidx.appcompat.app.AppCompatActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sunllo.deskpair.ConnectProgress
import com.sunllo.deskpair.android.ui.BrandMark
import com.sunllo.deskpair.android.ui.DeskPairTheme
import com.sunllo.deskpair.store.ConnectLink
import com.sunllo.deskpair.store.Targets

/**
 * The whole Android app so far: somewhere to type a host, and somewhere to show what it sends back.
 *
 * Deliberately thin. Everything that could be got wrong about the protocol lives in the shared module and is
 * tested against vectors from the desktop; this file only has to put a Surface on screen and hand it over.
 */
class MainActivity : AppCompatActivity() {

    private val model: SessionViewModel by viewModels()

    /**
     * Hardware keys go to the host, not to the phone.
     *
     * Intercepted here rather than on a focused view because the picture is a SurfaceView with nothing
     * focusable in it: without this, a keystroke on an attached keyboard would be swallowed by the system or
     * do nothing at all. Back is deliberately left alone, so there is always a way out.
     */
    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        if (event.keyCode == android.view.KeyEvent.KEYCODE_BACK) {
            return super.dispatchKeyEvent(event)
        }

        return model.onHardwareKey(event) || super.dispatchKeyEvent(event)
    }

    override fun onResume() {
        super.onResume()
        model.setBackgrounded(false)
        // Android only lets the focused app read the clipboard, so a copy made elsewhere never reached the
        // change listener. Coming back to the front is the first moment it can be read at all.
        model.offerClipboard()
    }

    /**
     * Lets a script drive a run, the way the iOS app is driven by environment variables.
     *
     * An emulator can be tapped with `adb shell input`, but only at coordinates someone measured, and those
     * move whenever the layout does. Naming the target in the intent instead keeps the verification about
     * the client rather than about where a button happened to be.
     *
     *   adb shell am start -n com.sunllo.deskpair/.android.MainActivity \
     *     -e host 10.0.2.2:21118 -e password xxxxxx -e clip "text to offer the host"
     *
     * `-e terminal 1` opens a terminal instead of the desktop; `adb shell input text` then types into it.
     */
    private fun connectIfAutomated() {
        // Measuring the network needs no session, so it is checked first and on its own.
        intent?.getStringExtra("natprobe")?.takeIf { it.isNotEmpty() }?.let { server ->
            model.probeNetwork(server)
        }

        // The server and key are settings now, not connection arguments, so a scripted run writes them the
        // same way the settings screen does. That keeps the automation on the path a real user takes.
        val server = intent?.getStringExtra("server").orEmpty()
        val key = intent?.getStringExtra("key").orEmpty()
        if (server.isNotEmpty() || key.isNotEmpty()) {
            model.store.update {
                it.copy(
                    rendezvousServer = server.ifEmpty { it.rendezvousServer },
                    serverPublicKeyBase64 = key.ifEmpty { it.serverPublicKeyBase64 },
                )
            }
        }

        val target = intent?.getStringExtra("id")?.takeIf { it.isNotEmpty() }
            ?: intent?.getStringExtra("host")?.takeIf { it.isNotEmpty() }
            ?: return

        if (intent.getStringExtra("terminal").isNullOrEmpty()) {
            model.connect(target, intent.getStringExtra("password").orEmpty())
        } else {
            model.openTerminal(target, intent.getStringExtra("password").orEmpty())
            return
        }

        // Pinching cannot be scripted through `adb shell input`, and the mapping after a zoom is exactly
        // what is worth checking. This applies the same canvas operation a pinch would.
        intent.getStringExtra("zoom")?.toFloatOrNull()?.let { factor ->
            window.decorView.postDelayed({ model.zoomForTest(factor) }, 6_000)
        }

        intent.getStringExtra("clip")?.takeIf { it.isNotEmpty() }?.let { text ->
            val after = intent.getStringExtra("delay")?.toLongOrNull() ?: 8
            window.decorView.postDelayed({
                // Through the clipboard, not straight to the session: the listener is the part that only a
                // real copy exercises.
                val manager = getSystemService(android.content.ClipboardManager::class.java)
                manager?.setPrimaryClip(android.content.ClipData.newPlainText("DeskPair", text))
            }, after * 1000)
        }
    }

    /**
     * Writes anything the settings debounce is still holding.
     *
     * A change made a moment before the app is backgrounded would otherwise be lost if the process is
     * killed while the five hundred milliseconds are still running.
     */
    override fun onPause() {
        super.onPause()
        model.setBackgrounded(true)
        model.store.flush()

        // A phone call, a swipe to the home screen, the notification shade: the gesture loop is not
        // always torn down, so its own cancel path may never run, and the host is left with a mouse
        // button held down. Cancelling here is idempotent — it does nothing when nothing is held.
        model.onTouchCancelled()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Draw behind the status and navigation bars. The remote picture wants every pixel, and the screens
        // in front of it now use Scaffold, which is handed the insets and pads its own content. Called with
        // no arguments the system bars are transparent and their icons follow the system's light or dark
        // setting — the same thing DeskPairTheme follows, so the two never disagree.
        enableEdgeToEdge()

        connectIfAutomated()
        setContent {
            DeskPairTheme {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    DeskPairApp(model)
                }
            }
        }
    }
}

@Composable
private fun DeskPairApp(model: SessionViewModel) {
    val state by model.ui.collectAsStateWithLifecycle()
    val screen by model.screen.collectAsStateWithLifecycle()
    val settings by model.store.settings.collectAsStateWithLifecycle()
    val context = androidx.compose.ui.platform.LocalContext.current

    when (val current = state) {
        is UiState.Connecting -> Connecting(stringResource(progressLabel(current.progress)))
        is UiState.Connected -> RemoteScreen(model, current)
        is UiState.Terminal -> {
            val terminal by model.terminal.collectAsStateWithLifecycle()
            terminal?.let { TerminalView(model, it) }
        }

        is UiState.Disconnected -> PreSession(
            model = model,
            settings = settings,
            screen = screen,
            // A session that ended on its own is said in the reader's language; only a failure that
            // arrived with words of its own passes them on.
            error = when {
                current.dropped && current.overRelay -> stringResource(R.string.session_dropped_retrying)
                current.dropped -> stringResource(R.string.session_dropped)
                else -> current.error
            },
            onScan = { done -> scanCode(context, model, done) },
        )
    }
}

/**
 * Scans a pairing code and applies what it carries.
 *
 * The server and key go straight into settings — that is the whole reason the code carries them, and
 * asking the user to confirm a public key they cannot read would be theatre. The one-time password is put
 * aside for the connection the caller then starts, rather than stored: it is good for one connection and
 * the host replaces it the moment it is used.
 */
private fun scanCode(
    context: android.content.Context,
    model: SessionViewModel,
    done: (target: String?, message: String?) -> Unit,
) {
    QrScanner.scan(context) { event ->
        when (event) {
            QrScanner.Event.Cancelled -> done(null, null)
            QrScanner.Event.Unavailable -> done(null, context.getString(R.string.scan_unavailable))
            is QrScanner.Event.Read -> when (val scanned = event.scanned) {
                is ConnectLink.Scanned.Understood -> {
                    val link = scanned.link

                    // For this desk only, never into the settings: see AppStore.rememberScannedNetwork.
                    model.store.rememberScannedNetwork(link.id, link.rendezvousServer, link.serverPublicKeyBase64)

                    // Put aside for the next connection to this desk, not stored: it is good for one
                    // connection and the host replaces it the moment it is used.
                    if (link.password.isNotEmpty()) {
                        model.store.rememberScannedPassword(link.id, link.password)
                    }

                    val named = link.deviceName.ifEmpty { Targets.formatId(link.id) }
                    done(link.id, context.getString(R.string.scan_applied, named))
                }

                is ConnectLink.Scanned.Rejected -> done(
                    null,
                    when (scanned.problem) {
                        ConnectLink.Problem.NOT_A_DESKPAIR_CODE ->
                            context.getString(R.string.scan_not_deskpair)
                        ConnectLink.Problem.NO_VERSION -> context.getString(R.string.scan_no_version)
                        ConnectLink.Problem.NEWER_VERSION ->
                            context.getString(R.string.scan_newer_version, scanned.version)
                        ConnectLink.Problem.NO_ID -> context.getString(R.string.scan_no_id)
                        ConnectLink.Problem.DAMAGED_KEY -> context.getString(R.string.scan_damaged_key)
                    },
                )
            }
        }
    }
}

@Composable
private fun progressLabel(progress: ConnectProgress): Int = when (progress) {
    ConnectProgress.LOOKING_UP -> R.string.progress_looking_up
    ConnectProgress.MEASURING_NETWORK -> R.string.progress_measuring
    ConnectProgress.PUNCHING -> R.string.progress_punching
    ConnectProgress.CONNECTING -> R.string.progress_connecting
    ConnectProgress.WAITING_FOR_APPROVAL -> R.string.progress_waiting
}

/**
 * What the user looks at for as long as the connection takes, which on a bad network is a while.
 *
 * It used to be the word "Connecting" alone in the middle of an empty screen, which reads as a stall rather
 * than as progress. The mark says whose app this is, and the bar says something is still happening; the
 * sentence underneath is the only part that changes as the stages go by.
 */
@Composable
private fun Connecting(message: String) {
    Column(
        modifier = Modifier.fillMaxSize().padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        BrandMark(size = 72.dp)
        Spacer(Modifier.height(28.dp))
        Text(
            text = message,
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
        Spacer(Modifier.height(20.dp))
        LinearProgressIndicator(modifier = Modifier.width(140.dp))
    }
}
