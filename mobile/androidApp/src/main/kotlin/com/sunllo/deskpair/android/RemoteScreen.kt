package com.sunllo.deskpair.android

import android.graphics.Matrix
import android.graphics.SurfaceTexture
import android.view.Surface
import android.view.TextureView
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.union
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.AspectRatio
import androidx.compose.material.icons.outlined.ContentPaste
import androidx.compose.material.icons.outlined.Fullscreen
import androidx.compose.material.icons.outlined.HighQuality
import androidx.compose.material.icons.outlined.Image
import androidx.compose.material.icons.outlined.Keyboard
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Monitor
import androidx.compose.material.icons.outlined.Mouse
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.TouchApp
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sunllo.deskpair.HostPermissions
import com.sunllo.deskpair.PointerMode
import com.sunllo.deskpair.Quality
import com.sunllo.deskpair.RemoteKey
import com.sunllo.deskpair.android.ui.ActionRow
import com.sunllo.deskpair.android.ui.OptionRow
import com.sunllo.deskpair.android.ui.RowDivider
import com.sunllo.deskpair.android.ui.ToggleRow
import com.sunllo.deskpair.android.ui.ValueRow
import kotlin.math.max
import kotlin.math.roundToInt

/**
 * The session: a desktop, the fingers driving it, and as little else on screen as possible.
 *
 * What is not the picture collapses. With every panel closed there is the video, a signal chip in one
 * corner, the way out in the other, and a bar of four tabs — and nothing else. That is the whole idea, and
 * it is why tapping an open tab closes it rather than doing nothing.
 */
@Composable
fun RemoteScreen(model: SessionViewModel, state: UiState.Connected) {
    val canvas by model.canvas.collectAsStateWithLifecycle()
    val cursor by model.cursor.collectAsStateWithLifecycle()
    val showCursor by model.showRemoteCursor.collectAsStateWithLifecycle()
    val mode by model.pointerMode.collectAsStateWithLifecycle()
    val modifiers by model.modifiers.collectAsStateWithLifecycle()
    val imageOffer by model.imageSent.collectAsStateWithLifecycle()
    val audioProblem by model.audioProblem.collectAsStateWithLifecycle()
    val videoProblem by model.videoProblem.collectAsStateWithLifecycle()
    val permissions by model.permissions.collectAsStateWithLifecycle()
    val hasPicture by model.hasPicture.collectAsStateWithLifecycle()
    val pictureIsLate by model.pictureIsLate.collectAsStateWithLifecycle()
    val hostNotice by model.hostNotice.collectAsStateWithLifecycle()
    val displays by model.displays.collectAsStateWithLifecycle()

    var panel by remember { mutableStateOf(SessionPanel.NONE) }
    var signalOpen by remember { mutableStateOf(false) }
    var gesturesOpen by remember { mutableStateOf(false) }

    // Declared here rather than beside the row that uses it. A panel's content leaves composition as soon
    // as the panel closes, and an activity-result launcher unregisters when its composable is disposed —
    // so a launcher created in there is gone before the picker can answer.
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        uri?.let(model::offerPickedImage)
    }

    // Watching a remote desktop is watching, not using: nothing is touched for minutes at a time and the
    // phone locks itself in the middle of it. Held for as long as this screen is up and released with it,
    // rather than set once on the window, so leaving the session always gives the timer back.
    val view = LocalView.current
    DisposableEffect(view) {
        view.keepScreenOn = true
        onDispose { view.keepScreenOn = false }
    }

    // A modifier that is held keeps its bar whichever panel is open: a bar that vanished mid-latch is how a
    // user ends up wondering why everything types as Ctrl+letter.
    val keysShowing = panel == SessionPanel.KEYBOARD || modifiers.isNotEmpty()

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black)
            .clipToBounds()
            .remoteTouch(model),
    ) {
        // A TextureView, not a SurfaceView.
        //
        // A SurfaceView is its own window: it ignores a Compose clip (it does not get contained, it gets
        // hidden), and resizing it destroys and recreates its surface — so panning and zooming by
        // changing its bounds rebuilt the decoder on every gesture frame and left the picture black until
        // the next keyframe happened along. A TextureView composites in the view hierarchy and takes a
        // transform matrix, which is exactly what a pan and a zoom are. MediaCodec still renders into it
        // directly; the frame never becomes a bitmap the CPU has to carry.
        AndroidView(
            factory = { context ->
                TextureView(context).apply {
                    surfaceTextureListener = object : TextureView.SurfaceTextureListener {
                        override fun onSurfaceTextureAvailable(texture: SurfaceTexture, w: Int, h: Int) {
                            model.attachSurface(Surface(texture))
                        }

                        override fun onSurfaceTextureSizeChanged(texture: SurfaceTexture, w: Int, h: Int) = Unit

                        override fun onSurfaceTextureDestroyed(texture: SurfaceTexture): Boolean {
                            model.detachSurface()
                            return true
                        }

                        override fun onSurfaceTextureUpdated(texture: SurfaceTexture) = Unit
                    }
                }
            },
            update = { view ->
                // The buffer is the host's own resolution, so the picture is never resampled twice; the
                // matrix then places that buffer wherever the canvas says, scaled however it says.
                if (canvas.hostWidth > 0 && canvas.hostHeight > 0) {
                    view.surfaceTexture?.setDefaultBufferSize(canvas.hostWidth, canvas.hostHeight)
                }

                if (canvas.viewWidth > 0f && canvas.viewHeight > 0f) {
                    val matrix = Matrix()
                    matrix.setScale(
                        canvas.hostWidth * canvas.scale / canvas.viewWidth,
                        canvas.hostHeight * canvas.scale / canvas.viewHeight,
                    )
                    matrix.postTranslate(canvas.x, canvas.y)
                    view.setTransform(matrix)
                    view.invalidate()
                }
            },
            modifier = Modifier.fillMaxSize(),
        )

        if (showCursor) {
            RemoteCursor(cursor, canvas)
        }

        // Over both, until there is a picture underneath worth uncovering -- or, when the host has no screen to
        // share and has said why, its reason in place of a picture, a stale one included: sharing stopped at
        // that machine leaves the last frame behind.
        val notice = hostNotice?.takeIf { displays.isEmpty() }
        if (notice != null) {
            HostNotice(hostname = state.hostname, notice = notice)
        } else if (!hasPicture) {
            WaitingForPicture(hostname = state.hostname, late = pictureIsLate)
        }

        Column(modifier = Modifier.align(Alignment.TopStart)) {
            SignalChip(
                hostname = state.hostname,
                platform = state.platform,
                transport = state.transport,
                roundTripMillis = state.roundTripMillis,
                stalled = state.stalled,
                expanded = signalOpen,
                onToggle = { signalOpen = !signalOpen },
            )

            // Only when there is something to say. A part of the session that quietly does not work is
            // indistinguishable from a host that is simply not doing that thing.
            // The host can withdraw the keyboard at any point, and used to do it invisibly: the phone
            // went on showing a keyboard and a pointer that did nothing.
            if (!permissions.keyboard) {
                Problem(stringResource(R.string.session_input_refused))
            }
            videoProblem?.let { Problem(stringResource(R.string.video_unavailable, it)) }
            audioProblem?.let { Problem(stringResource(R.string.audio_unavailable, it)) }
        }

        DisconnectButton(onClick = model::disconnect, modifier = Modifier.align(Alignment.TopEnd))

        if (state.stalled) {
            StalledNotice(model, modifier = Modifier.align(Alignment.Center))
        }

        // The whole bottom stack rides above the soft keyboard, not just the field inside it. Putting the
        // inset on the key bar alone lifted that one piece into the middle of the picture and left the tab
        // bar underneath the keyboard, which is worse than the problem it was fixing.
        //
        // One union rather than imePadding() and navigationBarsPadding() stacked: the IME inset already
        // reaches past the gesture bar, so applying both counts that strip twice and leaves the bar
        // floating above the keyboard instead of resting on it.
        Column(
            modifier = Modifier
                .align(Alignment.BottomStart)
                .fillMaxWidth()
                .windowInsetsPadding(WindowInsets.ime.union(WindowInsets.navigationBars)),
        ) {
            KeyBar(
                model = model,
                pinned = panel == SessionPanel.KEYBOARD,
                onPinnedChange = { on -> panel = if (on) SessionPanel.KEYBOARD else SessionPanel.NONE },
                visible = keysShowing,
            )

            SessionBar(
                // The keys are their own bar above this one, so that tab opens no panel of its own.
                panel = if (panel == SessionPanel.KEYBOARD) SessionPanel.NONE else panel,
                onPanel = { panel = it },
            ) {
                SessionPanelCard {
                    when (panel) {
                        SessionPanel.ACTIONS -> ActionsPanel(
                            model = model,
                            permissions = permissions,
                            onPickImage = {
                                picker.launch(
                                    PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly),
                                )
                            },
                        )

                        SessionPanel.INPUT -> InputPanel(
                            mode = mode,
                            showCursor = showCursor,
                            gesturesOpen = gesturesOpen,
                            onModeChange = model::setPointerMode,
                            onCursorChange = model::setShowRemoteCursor,
                            onGesturesToggle = { gesturesOpen = !gesturesOpen },
                        )

                        SessionPanel.SCREEN -> ScreenPanel(model, state)

                        else -> Unit
                    }
                }
            }
        }

        imageOffer?.let { offer ->
            // A picture that arrives with no acknowledgement looks exactly like one that did not, and the
            // next step is on the other machine — so the message says what to do there.
            LaunchedEffect(offer) {
                kotlinx.coroutines.delay(4_000)
                model.clearImageNotice()
            }

            Text(
                text = when (offer) {
                    is ImageOffer.Sent -> stringResource(R.string.clipboard_image_sent)
                    ImageOffer.AlreadyThere -> stringResource(R.string.clipboard_image_known)
                    is ImageOffer.Failed -> offer.reason.ifEmpty { stringResource(R.string.clipboard_image_failed) }
                },
                color = Color.White,
                style = MaterialTheme.typography.labelLarge,
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .statusBarsPadding()
                    .padding(top = 56.dp, start = 16.dp, end = 16.dp)
                    .background(Color(0xCC101010), RoundedCornerShape(10.dp))
                    .padding(horizontal = 12.dp, vertical = 8.dp),
            )
        }
    }
}

/** Something about the session that is not working, over a picture that may be black. */
@Composable
private fun Problem(text: String) {
    Text(
        text = text,
        color = Color.White,
        style = MaterialTheme.typography.labelSmall,
        modifier = Modifier
            .padding(start = 8.dp, end = 8.dp, bottom = 4.dp)
            .background(Color(0xCC101010), RoundedCornerShape(8.dp))
            .padding(horizontal = 8.dp, vertical = 4.dp),
    )
}

/** What to do to the remote machine — kept apart from how the picture looks. */
@Composable
private fun ActionsPanel(
    model: SessionViewModel,
    permissions: HostPermissions,
    onPickImage: () -> Unit,
) {
    ActionRow(
        icon = Icons.Outlined.Keyboard,
        label = stringResource(R.string.session_ctrl_alt_del),
        enabled = permissions.keyboard,
    ) {
        model.pressKey(RemoteKey.CTRL_ALT_DELETE)
    }
    RowDivider()
    ActionRow(
        icon = Icons.Outlined.Lock,
        label = stringResource(R.string.session_lock),
        enabled = permissions.keyboard,
    ) {
        model.pressKey(RemoteKey.LOCK_SCREEN)
    }
    RowDivider()
    ActionRow(Icons.Outlined.Refresh, stringResource(R.string.session_refresh)) { model.refreshVideo() }
    RowDivider()
    ActionRow(
        icon = Icons.Outlined.ContentPaste,
        label = stringResource(R.string.clipboard_send),
        enabled = permissions.clipboard,
    ) {
        model.offerClipboard()
    }
    RowDivider()
    ActionRow(
        icon = Icons.Outlined.Image,
        label = stringResource(R.string.clipboard_send_image),
        enabled = permissions.clipboard,
        onClick = onPickImage,
    )
}

/**
 * What the fingers do, and the switch that changes it.
 *
 * The mode is a segmented control rather than two radio buttons in a row: there are exactly two, they are
 * exclusive, and the pair reads as one control instead of as two half-made ones. The gesture table is worth
 * keeping and worth folding away — it answers a question people ask once.
 */
@Composable
private fun InputPanel(
    mode: PointerMode,
    showCursor: Boolean,
    gesturesOpen: Boolean,
    onModeChange: (PointerMode) -> Unit,
    onCursorChange: (Boolean) -> Unit,
    onGesturesToggle: () -> Unit,
) {
    SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
        SegmentedButton(
            selected = mode == PointerMode.TOUCH,
            onClick = { onModeChange(PointerMode.TOUCH) },
            shape = SegmentedButtonDefaults.itemShape(index = 0, count = 2),
            icon = {},
        ) {
            Text(stringResource(R.string.mode_touch))
        }
        SegmentedButton(
            selected = mode == PointerMode.MOUSE,
            onClick = { onModeChange(PointerMode.MOUSE) },
            shape = SegmentedButtonDefaults.itemShape(index = 1, count = 2),
            icon = {},
        ) {
            Text(stringResource(R.string.mode_mouse))
        }
    }

    RowDivider(startIndent = 0.dp)
    ToggleRow(
        icon = Icons.Outlined.Mouse,
        label = stringResource(R.string.session_remote_cursor),
        checked = showCursor,
        onChange = onCursorChange,
    )
    RowDivider()
    ValueRow(
        icon = Icons.Outlined.TouchApp,
        label = stringResource(R.string.session_gestures),
        value = "",
        onClick = onGesturesToggle,
    )

    if (gesturesOpen) {
        GestureTable(mode)
    }
}

/** The six gestures and what each one does, which changes with the mode. */
@Composable
private fun GestureTable(mode: PointerMode) {
    val rows = listOf(
        R.string.gesture_tap to
            if (mode == PointerMode.TOUCH) R.string.gesture_tap_touch else R.string.gesture_tap_mouse,
        R.string.gesture_two_tap to R.string.gesture_two_tap_action,
        R.string.gesture_drag to
            if (mode == PointerMode.TOUCH) R.string.gesture_drag_touch else R.string.gesture_drag_mouse,
        R.string.gesture_two_drag to R.string.gesture_two_drag_action,
        R.string.gesture_pinch to R.string.gesture_pinch_action,
        R.string.gesture_three to R.string.gesture_three_action,
    )

    Column(modifier = Modifier.padding(start = 56.dp, end = 16.dp, bottom = 12.dp)) {
        for ((gesture, action) in rows) {
            Row(modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp)) {
                Text(
                    text = stringResource(gesture),
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.bodySmall,
                )
                Text(
                    text = stringResource(action),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

/**
 * How the picture looks — kept apart from what the remote machine is told to do.
 *
 * Quality shows its current value and opens to the three choices, which is the shape the reference uses and
 * the shape the settings screen now uses. It was a dialog with all three radio buttons always visible.
 */
@Composable
private fun ScreenPanel(model: SessionViewModel, state: UiState.Connected) {
    var quality by remember { mutableStateOf(Quality.BALANCED) }
    var open by remember { mutableStateOf(false) }

    val label = when (quality) {
        Quality.LOW -> R.string.quality_low
        Quality.BEST -> R.string.quality_best
        Quality.CUSTOM -> R.string.settings_quality_custom
        else -> R.string.quality_balanced
    }

    ValueRow(
        icon = Icons.Outlined.HighQuality,
        label = stringResource(R.string.session_quality),
        value = stringResource(label),
        onClick = { open = !open },
    )

    if (open) {
        // Custom is the bitrate and frame rate from Settings, not something edited here: a numeric field
        // over the video would be the wrong place to type one, and the stored pair already reaches every
        // new session. Offering the mode makes Quality.CUSTOM reachable at all, which it was not.
        for (option in listOf(Quality.LOW, Quality.BALANCED, Quality.BEST, Quality.CUSTOM)) {
            OptionRow(
                label = stringResource(
                    when (option) {
                        Quality.LOW -> R.string.quality_low
                        Quality.BEST -> R.string.quality_best
                        Quality.CUSTOM -> R.string.settings_quality_custom
                        else -> R.string.quality_balanced
                    },
                ),
                selected = quality == option,
                onClick = {
                    quality = option
                    model.setQuality(option)
                    open = false
                },
            )
        }
    }

    RowDivider()
    ActionRow(Icons.Outlined.Fullscreen, stringResource(R.string.session_fit)) { model.resetCanvas() }
    RowDivider()
    ActionRow(Icons.Outlined.AspectRatio, stringResource(R.string.session_original)) { model.actualSize() }

    // The host's screen resolution. Only where the host offers modes and lets this viewer use the
    // keyboard: somebody who may only watch does not get to resize the other person's desktop.
    val displays by model.displays.collectAsStateWithLifecycle()
    val current by model.display.collectAsStateWithLifecycle()
    val permissions by model.permissions.collectAsStateWithLifecycle()
    val resolutionFailure by model.resolutionFailure.collectAsStateWithLifecycle()
    val shown = displays.getOrNull(current)
    if (shown != null && shown.modes.isNotEmpty() && permissions.keyboard) {
        var resolutionOpen by remember { mutableStateOf(false) }
        val selected = com.sunllo.deskpair.Resolutions.selectedChoice(shown)
        val originalLabel = shown.original?.let { "${stringResource(R.string.session_resolution_original)} (${it.label})" }
            ?: stringResource(R.string.session_resolution_original)
        RowDivider()
        ValueRow(
            icon = Icons.Outlined.Monitor,
            label = stringResource(R.string.session_resolution),
            value = if (selected == 0) originalLabel else shown.modes[selected - 1].label,
            onClick = { resolutionOpen = !resolutionOpen },
        )

        if (resolutionOpen) {
            OptionRow(
                label = originalLabel,
                selected = selected == 0,
                onClick = {
                    model.setResolution(current, null)
                    resolutionOpen = false
                },
            )
            shown.modes.forEachIndexed { index, mode ->
                OptionRow(
                    label = mode.label,
                    selected = selected == index + 1,
                    onClick = {
                        model.setResolution(current, mode)
                        resolutionOpen = false
                    },
                )
            }
        }

        resolutionFailure?.let { Problem(stringResource(R.string.session_resolution_failed, it)) }
    }

    // Only where there is a choice to make. One screen needs no switcher.
    if (displays.size > 1 || state.displays > 1) {
        for (index in 0 until maxOf(displays.size, state.displays)) {
            RowDivider()
            OptionRow(
                label = stringResource(R.string.display_label, index + 1),
                selected = index == current,
                onClick = { model.switchDisplay(index) },
            )
        }
    }
}

/**
 * The host's own pointer, drawn over the picture.
 *
 * Without it, touch mode is aiming blind: the finger covers the target. The hotspot matters — an I-beam
 * points from its middle, an arrow from its tip — and a minimum size keeps it grabbable when zoomed out.
 */
@Composable
private fun RemoteCursor(cursor: RemoteCursorState?, canvas: com.sunllo.deskpair.CanvasState) {
    val shape = cursor?.shape ?: return
    val at = canvas.toView(cursor.x, cursor.y)

    val drawnWidth = max(shape.bitmap.width * canvas.scale, MINIMUM_CURSOR_POINTS)
    val ratio = drawnWidth / shape.bitmap.width
    val drawnHeight = shape.bitmap.height * ratio

    // Fills the box and draws at absolute offsets: the canvas already knows where the pointer is.
    Canvas(modifier = Modifier.fillMaxSize()) {
        drawImage(
            image = shape.bitmap.asImageBitmap(),
            dstOffset = IntOffset(
                (at.x - shape.hotX * ratio).roundToInt(),
                (at.y - shape.hotY * ratio).roundToInt(),
            ),
            dstSize = androidx.compose.ui.unit.IntSize(drawnWidth.roundToInt(), drawnHeight.roundToInt()),
        )
    }
}

private const val MINIMUM_CURSOR_POINTS = 18f

/**
 * The screen between agreeing and seeing.
 *
 * A session reaching "connected" means the two ends have settled on a codec, a size and a route. It does
 * not mean a frame has arrived, and on a slow link those are seconds apart -- seconds during which the
 * screen was its own black backdrop, which is exactly what a broken session looks like. Worse, the same
 * black is what a host that cannot read its own desktop shows for ever.
 *
 * So this sits over the canvas until the first frame is decoded, and after a few seconds it stops merely
 * spinning and says what is most likely wrong. The picture, when it appears, is one that has already been
 * negotiated and decoded -- never a flicker of black on the way to it.
 */
@Composable
private fun WaitingForPicture(hostname: String, late: Boolean) {
    Column(
        modifier = Modifier.fillMaxSize().background(Color.Black).padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        CircularProgressIndicator(color = Color.White)
        Spacer(modifier = Modifier.height(20.dp))
        Text(
            text = stringResource(R.string.session_waiting_for_picture, hostname),
            color = Color.White,
            style = MaterialTheme.typography.bodyMedium,
            textAlign = TextAlign.Center,
        )

        if (late) {
            Spacer(modifier = Modifier.height(12.dp))
            Text(
                text = stringResource(R.string.session_picture_late),
                color = Color.White.copy(alpha = 0.7f),
                style = MaterialTheme.typography.bodySmall,
                textAlign = TextAlign.Center,
                modifier = Modifier.widthIn(max = 320.dp),
            )
        }
    }
}

/**
 * What the host said about its screen, when it has none to show.
 *
 * No spinner: the host is not necessarily about to send anything. It may be asking the person at that
 * computer, but it may equally have been refused there, and only the host's words tell which. They are
 * shown as the host sent them, as the desktop viewer shows them.
 */
@Composable
private fun HostNotice(hostname: String, notice: String) {
    Column(
        modifier = Modifier.fillMaxSize().background(Color.Black).padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text(
            text = hostname,
            color = Color.White.copy(alpha = 0.7f),
            style = MaterialTheme.typography.bodySmall,
            textAlign = TextAlign.Center,
        )
        Spacer(modifier = Modifier.height(12.dp))
        Text(
            text = notice,
            color = Color.White,
            style = MaterialTheme.typography.bodyMedium,
            textAlign = TextAlign.Center,
            modifier = Modifier.widthIn(max = 360.dp),
        )
    }
}

@Composable
private fun StalledNotice(model: SessionViewModel, modifier: Modifier) {
    Column(
        modifier = modifier
            .padding(24.dp)
            .background(Color(0xCC101010), RoundedCornerShape(14.dp))
            .padding(16.dp),
    ) {
        Text(stringResource(R.string.session_stalled), color = Color.White)
        Text(
            text = stringResource(R.string.session_stalled_hint),
            color = Color.White,
            style = MaterialTheme.typography.labelSmall,
        )
        TextButton(onClick = model::reconnect) {
            Text(stringResource(R.string.connect_retry))
        }
    }
}
