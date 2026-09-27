package com.sunllo.deskpair.android

import android.content.ClipboardManager
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sunllo.deskpair.terminal.RemoteTerminal
import com.sunllo.deskpair.terminal.TerminalKey
import com.sunllo.deskpair.terminal.TerminalKeys
import com.sunllo.deskpair.terminal.TerminalPhase
import com.sunllo.deskpair.terminal.TerminalSnapshot

/** Android key codes for the keys the terminal names. */
internal object TerminalKeyMap {
    fun named(keyCode: Int): TerminalKey? = when (keyCode) {
        android.view.KeyEvent.KEYCODE_ENTER, android.view.KeyEvent.KEYCODE_NUMPAD_ENTER -> TerminalKey.ENTER
        android.view.KeyEvent.KEYCODE_TAB -> TerminalKey.TAB
        android.view.KeyEvent.KEYCODE_DEL -> TerminalKey.BACKSPACE
        android.view.KeyEvent.KEYCODE_FORWARD_DEL -> TerminalKey.DELETE
        android.view.KeyEvent.KEYCODE_ESCAPE -> TerminalKey.ESCAPE
        android.view.KeyEvent.KEYCODE_DPAD_UP -> TerminalKey.UP
        android.view.KeyEvent.KEYCODE_DPAD_DOWN -> TerminalKey.DOWN
        android.view.KeyEvent.KEYCODE_DPAD_LEFT -> TerminalKey.LEFT
        android.view.KeyEvent.KEYCODE_DPAD_RIGHT -> TerminalKey.RIGHT
        android.view.KeyEvent.KEYCODE_MOVE_HOME -> TerminalKey.HOME
        android.view.KeyEvent.KEYCODE_MOVE_END -> TerminalKey.END
        android.view.KeyEvent.KEYCODE_PAGE_UP -> TerminalKey.PAGE_UP
        android.view.KeyEvent.KEYCODE_PAGE_DOWN -> TerminalKey.PAGE_DOWN
        android.view.KeyEvent.KEYCODE_INSERT -> TerminalKey.INSERT
        in android.view.KeyEvent.KEYCODE_F1..android.view.KeyEvent.KEYCODE_F12 ->
            TerminalKey.entries[TerminalKey.F1.ordinal + (keyCode - android.view.KeyEvent.KEYCODE_F1)]
        else -> null
    }
}

private val TerminalFont = FontFamily.Monospace

/**
 * A terminal on another computer, full screen.
 *
 * Drawn from the shared snapshot: each run placed at its column times the cell width, so the grid holds
 * whatever the font does with a wide character. The soft keyboard types through an invisible text field
 * that only ever holds one placeholder character -- what is added after it is typed, a deletion of it is a
 * backspace -- and a row of keys supplies what a phone keyboard lacks. Ctrl and Alt on that row are
 * sticky: tap Ctrl, then C, is Ctrl+C.
 */
@Composable
fun TerminalView(model: SessionViewModel, terminal: RemoteTerminal) {
    val snapshot by terminal.snapshot.collectAsStateWithLifecycle()
    val status by terminal.status.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val keyboard = LocalSoftwareKeyboardController.current
    val focus = remember { FocusRequester() }
    val measurer = rememberTextMeasurer(cacheSize = 256)
    var control by remember { mutableStateOf(false) }
    var alt by remember { mutableStateOf(false) }
    var pendingPaste by remember { mutableStateOf<String?>(null) }
    var field by remember { mutableStateOf(TextFieldValue(" ", TextRange(1))) }
    var dragCarry by remember { mutableStateOf(0f) }

    val fontSize = 13.sp
    val cell = remember(measurer) {
        val size = measurer.measure("M", TextStyle(fontFamily = TerminalFont, fontSize = fontSize)).size
        Size(size.width.toFloat(), size.height.toFloat())
    }

    BackHandler { model.closeTerminal() }

    fun typed(text: String) {
        for (ch in text) {
            when {
                ch == '\n' -> terminal.key(TerminalKey.ENTER)
                control || alt -> {
                    terminal.chord(ch.toString(), alt = alt, control = control)
                    control = false
                    alt = false
                }
                else -> terminal.type(ch.toString())
            }
        }
    }

    fun paste() {
        val text = context.getSystemService(ClipboardManager::class.java)?.primaryClip
            ?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(context)?.toString().orEmpty()
        if (text.isEmpty()) return
        if (TerminalKeys.isMultiLine(text)) pendingPaste = text else terminal.paste(text)
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(snapshot.background))
            .statusBarsPadding()
            .navigationBarsPadding()
            .imePadding(),
    ) {
        // Whose shell this is: the one thing about a terminal on somebody else's computer that must never be ambiguous.
        Row(
            modifier = Modifier.fillMaxWidth().background(Color(0xFF1B1F26)).padding(horizontal = 12.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = listOf(status.identity, terminal.host.hostname).filter { it.isNotEmpty() }.joinToString("@"),
                color = Color(0xFFE8C06A),
                style = TextStyle(fontFamily = TerminalFont, fontWeight = FontWeight.SemiBold, fontSize = 14.sp),
                modifier = Modifier.weight(1f),
            )
            TextButton(onClick = { model.closeTerminal() }) {
                Text(stringResource(R.string.terminal_close), color = Color(0xFFD8DEE6))
            }
        }

        val notice = when (status.phase) {
            TerminalPhase.OPENING -> stringResource(R.string.terminal_opening)
            TerminalPhase.ENDED -> if (status.message.isEmpty() || status.message == "the shell exited") {
                stringResource(R.string.terminal_ended, status.exitCode)
            } else {
                stringResource(R.string.terminal_ended_reason, status.message)
            }
            TerminalPhase.FAILED -> stringResource(R.string.terminal_failed, status.message)
            TerminalPhase.CLOSED -> stringResource(R.string.terminal_connection_lost)
            TerminalPhase.OPEN -> null
        }
        if (notice != null) {
            Row(
                modifier = Modifier.fillMaxWidth().background(Color(0xFF22262D)).padding(horizontal = 12.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(notice, color = Color(0xFFD8DEE6), modifier = Modifier.weight(1f))
                if (status.phase == TerminalPhase.ENDED || status.phase == TerminalPhase.FAILED) {
                    TextButton(onClick = { terminal.openNewShell() }) {
                        Text(stringResource(R.string.terminal_new_shell))
                    }
                }
            }
        }

        Box(modifier = Modifier.weight(1f).fillMaxWidth().padding(4.dp)) {
            Canvas(
                modifier = Modifier
                    .fillMaxSize()
                    // The grid that fits. A size the screen already has is ignored by the session, and the
                    // host hears of a new one only once it has settled.
                    .onSizeChanged { size ->
                        terminal.resize(
                            (size.width / cell.width).toInt().coerceAtLeast(2),
                            (size.height / cell.height).toInt().coerceAtLeast(1),
                        )
                    }
                    .pointerInput(Unit) {
                        detectTapGestures(onTap = {
                            focus.requestFocus()
                            keyboard?.show()
                        })
                    }
                    .pointerInput(cell) {
                        detectVerticalDragGestures { _, dy ->
                            dragCarry += dy
                            val lines = (dragCarry / cell.height).toInt()
                            if (lines != 0) {
                                dragCarry -= lines * cell.height
                                terminal.scroll(lines)
                            }
                        }
                    },
            ) {
                drawSnapshot(snapshot, cell, measurer, fontSize)
            }

            // The soft keyboard's way in. Never visible; always holding one placeholder character.
            BasicTextField(
                value = field,
                onValueChange = { next ->
                    when {
                        next.text.isEmpty() -> terminal.key(TerminalKey.BACKSPACE, alt = alt, control = control)
                        next.text.length > 1 -> typed(next.text.substring(1))
                    }
                    field = TextFieldValue(" ", TextRange(1))
                },
                keyboardOptions = KeyboardOptions(
                    autoCorrectEnabled = false,
                    keyboardType = KeyboardType.Password,
                    imeAction = ImeAction.None,
                ),
                modifier = Modifier.size(1.dp).focusRequester(focus),
            )
        }

        // What a phone keyboard lacks. Ctrl and Alt stay down for the next key, then let go.
        Row(
            modifier = Modifier.fillMaxWidth().background(Color(0xFF1B1F26)).horizontalScroll(rememberScrollState()).padding(4.dp),
        ) {
            KeyButton("Esc") { terminal.key(TerminalKey.ESCAPE) }
            KeyButton("Tab") { terminal.key(TerminalKey.TAB) }
            KeyButton("Ctrl", latched = control) { control = !control }
            KeyButton("Alt", latched = alt) { alt = !alt }
            KeyButton("←") { terminal.key(TerminalKey.LEFT, control = control, alt = alt); control = false; alt = false }
            KeyButton("↑") { terminal.key(TerminalKey.UP, control = control, alt = alt); control = false; alt = false }
            KeyButton("↓") { terminal.key(TerminalKey.DOWN, control = control, alt = alt); control = false; alt = false }
            KeyButton("→") { terminal.key(TerminalKey.RIGHT, control = control, alt = alt); control = false; alt = false }
            KeyButton("Home") { terminal.key(TerminalKey.HOME) }
            KeyButton("End") { terminal.key(TerminalKey.END) }
            KeyButton("PgUp") { terminal.key(TerminalKey.PAGE_UP) }
            KeyButton("PgDn") { terminal.key(TerminalKey.PAGE_DOWN) }
            listOf("|", "~", "/", "-", "_", "\\").forEach { symbol -> KeyButton(symbol) { typed(symbol) } }
            KeyButton(stringResource(R.string.terminal_paste)) { paste() }
            Spacer(Modifier.size(4.dp))
        }
    }

    LaunchedEffect(Unit) {
        focus.requestFocus()
        keyboard?.show()
    }

    pendingPaste?.let { text ->
        val lines = text.trimEnd('\r', '\n').split('\n').size
        AlertDialog(
            onDismissRequest = { pendingPaste = null },
            text = { Text(stringResource(R.string.terminal_paste_confirm, lines)) },
            confirmButton = {
                TextButton(onClick = {
                    terminal.paste(text)
                    pendingPaste = null
                }) { Text(stringResource(R.string.terminal_paste)) }
            },
            dismissButton = {
                TextButton(onClick = { pendingPaste = null }) { Text(stringResource(android.R.string.cancel)) }
            },
        )
    }
}

@Composable
private fun KeyButton(label: String, latched: Boolean = false, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .padding(horizontal = 2.dp)
            .background(if (latched) Color(0xFF3A6EA5) else Color(0xFF2A3038), shape = androidx.compose.foundation.shape.RoundedCornerShape(6.dp))
            .pointerInput(label) { detectTapGestures(onTap = { onClick() }) }
            .padding(horizontal = 12.dp)
            .height(36.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = Color(0xFFD8DEE6), style = TextStyle(fontFamily = TerminalFont, fontSize = 14.sp))
    }
}

private fun androidx.compose.ui.graphics.drawscope.DrawScope.drawSnapshot(
    snapshot: TerminalSnapshot,
    cell: Size,
    measurer: androidx.compose.ui.text.TextMeasurer,
    fontSize: androidx.compose.ui.unit.TextUnit,
) {
    snapshot.lines.forEachIndexed { row, line ->
        val y = row * cell.height
        for (run in line.runs) {
            val x = run.column * cell.width
            if (run.background != snapshot.background) {
                drawRect(Color(run.background), Offset(x, y), Size(run.cells * cell.width, cell.height))
            }

            if (run.text.isNotBlank() || run.underline || run.strike) {
                drawText(
                    textMeasurer = measurer,
                    text = run.text,
                    topLeft = Offset(x, y),
                    style = TextStyle(
                        color = Color(run.foreground),
                        fontFamily = TerminalFont,
                        fontSize = fontSize,
                        fontWeight = if (run.bold) FontWeight.Bold else FontWeight.Normal,
                        fontStyle = if (run.italic) FontStyle.Italic else FontStyle.Normal,
                        textDecoration = when {
                            run.underline && run.strike -> TextDecoration.combine(listOf(TextDecoration.Underline, TextDecoration.LineThrough))
                            run.underline -> TextDecoration.Underline
                            run.strike -> TextDecoration.LineThrough
                            else -> null
                        },
                    ),
                    softWrap = false,
                )
            }
        }
    }

    if (snapshot.cursorVisible) {
        drawRect(
            Color(snapshot.cursorColor).copy(alpha = 0.6f),
            Offset(snapshot.cursorColumn * cell.width, snapshot.cursorRow * cell.height),
            Size(cell.width, cell.height),
        )
    }
}
