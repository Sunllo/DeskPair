package com.sunllo.deskpair.terminal

import com.sunllo.deskpair.RemoteHost
import com.sunllo.deskpair.session.ControllerSession
import com.sunllo.deskpair.transport.PeerConnection
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import okio.ByteString.Companion.toByteString
import sunllo.messages.Message
import sunllo.messages.TerminalAck
import sunllo.messages.TerminalAction
import sunllo.messages.TerminalClose
import sunllo.messages.TerminalInput
import sunllo.messages.TerminalOpen
import sunllo.messages.TerminalResize
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/** A stretch of one row drawn the same way, starting at [column] and taking [cells] cells. Colours are ARGB. */
public data class TerminalRun(
    val column: Int,
    val cells: Int,
    val text: String,
    val foreground: Int,
    val background: Int,
    val bold: Boolean,
    val italic: Boolean,
    val underline: Boolean,
    val strike: Boolean,
)

public data class TerminalLine(val runs: List<TerminalRun>)

/**
 * What the screen shows, ready to draw: resolved colours, and runs that never mix a wide character with
 * narrow ones -- a wide character is a run of its own, two cells wide -- so a renderer that places each run
 * at `column × cell width` keeps the grid whatever its font does with CJK.
 */
public data class TerminalSnapshot(
    val columns: Int,
    val rows: Int,
    val lines: List<TerminalLine>,
    val cursorColumn: Int,
    val cursorRow: Int,
    /** False while scrolled back into the history, or when the program hid it. */
    val cursorVisible: Boolean,
    val scrolledBack: Int,
    val scrollbackLines: Int,
    val title: String,
    val background: Int = TerminalPalette.BACKGROUND,
    val cursorColor: Int = TerminalPalette.CURSOR,
)

public enum class TerminalPhase { OPENING, OPEN, ENDED, FAILED, CLOSED }

/** Where the shell is. [identity] is the account it runs as, which the screen must always show. */
public data class TerminalStatus(
    val phase: TerminalPhase,
    val identity: String = "",
    val shell: String = "",
    val exitCode: Int = 0,
    val message: String = "",
)

/**
 * The host does not give a terminal to this viewer: switched off in its settings, not granted when it was
 * approved, or a DeskPair too old to have one. Raised before anything is sent, because asking anyway is a
 * scope violation that ends the whole connection.
 */
public class TerminalNotAllowedException : Exception("This computer does not allow a terminal.")

/**
 * A terminal on another computer: the phone's half of the desktop's terminal window. One shell at a time;
 * a new one can be opened while the connection lasts, and none outlives it.
 *
 * Everything touching the screen runs on one confined dispatcher -- the read loop, typing, resizing,
 * scrolling -- so the screen needs no lock. The apps call plain functions and watch two flows.
 */
@OptIn(ExperimentalCoroutinesApi::class)
public class RemoteTerminal internal constructor(
    private val connection: PeerConnection,
    private val session: ControllerSession,
    columns: Int,
    rows: Int,
) {
    private val confined = Dispatchers.Default.limitedParallelism(1)
    private val scope = CoroutineScope(kotlinx.coroutines.SupervisorJob() + confined + kotlinx.coroutines.CoroutineExceptionHandler { _, _ -> })
    private val screen = TerminalScreen(columns, rows)
    private var terminalId = 0
    private var scrollOffset = 0
    private var publishQueued = false
    private var resizeJob: Job? = null
    private var closing = false

    public val host: RemoteHost = session.peer.let {
        RemoteHost(hostname = it?.hostname.orEmpty(), username = it?.username.orEmpty(), platform = it?.platform.orEmpty(), displays = emptyList())
    }

    public val transport: String = connection.kind.name

    private val _snapshot = MutableStateFlow(build())
    public val snapshot: StateFlow<TerminalSnapshot> = _snapshot.asStateFlow()

    private val _status = MutableStateFlow(TerminalStatus(TerminalPhase.OPENING))
    public val status: StateFlow<TerminalStatus> = _status.asStateFlow()

    internal fun start() {
        scope.launch {
            launch {
                while (isActive) {
                    delay(5.seconds)
                    runCatching { session.sendHeartbeat() }.onFailure { return@launch }
                }
            }
            openShell()
            try {
                pump()
                end(TerminalPhase.CLOSED, "")
            } catch (e: Throwable) {
                if (closing || e is kotlinx.coroutines.CancellationException) {
                    end(TerminalPhase.CLOSED, "")
                } else {
                    end(TerminalPhase.CLOSED, e.message ?: "the connection was lost")
                }
            }
        }
    }

    private fun end(phase: TerminalPhase, message: String) {
        val now = _status.value
        // A shell that already ended keeps its ending; the connection closing afterwards is not news about it.
        if (now.phase == TerminalPhase.ENDED || now.phase == TerminalPhase.FAILED) {
            if (phase == TerminalPhase.CLOSED) {
                _status.value = now.copy(phase = TerminalPhase.CLOSED)
            }
            return
        }
        _status.value = now.copy(phase = phase, message = message)
    }

    private suspend fun openShell() {
        terminalId++
        screen.feed("\u001bc")
        scrollOffset = 0
        _status.value = TerminalStatus(TerminalPhase.OPENING)
        publish()
        session.send(Message(terminal_action = TerminalAction(open_ = TerminalOpen(id = terminalId, columns = screen.columns, rows = screen.rows))))
    }

    private suspend fun pump() {
        while (true) {
            val message = session.receive() ?: return

            message.misc?.close_reason?.let { reason ->
                end(TerminalPhase.CLOSED, reason.reason)
                return
            }

            message.test_delay?.let { session.echo(it) }

            val response = message.terminal_response ?: continue
            response.opened?.takeIf { it.id == terminalId }?.let {
                _status.value = TerminalStatus(TerminalPhase.OPEN, identity = it.identity, shell = it.shell)
            }

            response.output?.takeIf { it.id == terminalId }?.let { output ->
                val bytes = output.data_.toByteArray()
                screen.feed(bytes)
                // Consumed: refill the host's credit, or the shell's output stops at the window.
                session.send(Message(terminal_action = TerminalAction(ack = TerminalAck(id = output.id, bytes = bytes.size))))
                screen.takeReplies().forEach { send(it) }
                publish()
            }

            response.exit?.takeIf { it.id == terminalId }?.let { exit ->
                _status.value = _status.value.copy(phase = TerminalPhase.ENDED, exitCode = exit.code, message = exit.reason)
            }

            response.error?.takeIf { it.id == terminalId }?.let { error ->
                _status.value = _status.value.copy(phase = TerminalPhase.FAILED, message = error.message)
            }
        }
    }

    /** One snapshot per frame at most, however many output messages arrive in it. */
    private fun publish() {
        if (publishQueued) {
            return
        }

        publishQueued = true
        scope.launch {
            delay(16.milliseconds)
            publishQueued = false
            _snapshot.value = build()
        }
    }

    private suspend fun send(bytes: ByteArray) {
        if (_status.value.phase != TerminalPhase.OPEN || bytes.isEmpty()) {
            return
        }

        runCatching {
            session.send(Message(terminal_action = TerminalAction(input = TerminalInput(id = terminalId, data_ = bytes.toByteString()))))
        }
    }

    /** Typed text, exactly as typed. Back to the bottom of the history first, as a terminal does. */
    public fun type(text: String) {
        input { text.encodeToByteArray() }
    }

    /** A special key. */
    public fun key(key: TerminalKey, shift: Boolean = false, alt: Boolean = false, control: Boolean = false) {
        input { TerminalKeys.key(key, shift, alt, control, screen.applicationCursorKeys) }
    }

    /** A character with Ctrl or Alt held (Ctrl+C is SIGINT at the other end). */
    public fun chord(character: String, alt: Boolean = false, control: Boolean = false) {
        input { TerminalKeys.text(character, alt, control) }
    }

    /** A paste. The app asks first when [TerminalKeys.isMultiLine] says it runs more than one command. */
    public fun paste(text: String) {
        input { TerminalKeys.paste(text, screen.bracketedPaste) }
    }

    private fun input(bytes: () -> ByteArray) {
        scope.launch {
            if (scrollOffset != 0) {
                scrollOffset = 0
                publish()
            }
            send(bytes())
        }
    }

    /** The grid that fits the view. The screen follows at once; the host once the size has settled. */
    public fun resize(columns: Int, rows: Int) {
        scope.launch {
            if (columns == screen.columns && rows == screen.rows) {
                return@launch
            }

            screen.resize(columns, rows)
            publish()
            resizeJob?.cancel()
            resizeJob = scope.launch {
                delay(150.milliseconds)
                runCatching {
                    session.send(Message(terminal_action = TerminalAction(resize = TerminalResize(id = terminalId, columns = screen.columns, rows = screen.rows))))
                }
            }
        }
    }

    /** Moves the view into the history by [lines] (positive is back in time). */
    public fun scroll(lines: Int) {
        scope.launch {
            scrollOffset = (scrollOffset + lines).coerceIn(0, screen.scrollback.size)
            publish()
        }
    }

    public fun openNewShell() {
        scope.launch {
            if (_status.value.phase == TerminalPhase.ENDED || _status.value.phase == TerminalPhase.FAILED) {
                openShell()
            }
        }
    }

    /** Ends the shell and the connection. */
    public fun close() {
        closing = true
        val id = terminalId
        scope.launch {
            runCatching {
                session.send(Message(terminal_action = TerminalAction(close = TerminalClose(id = id))))
                session.send(Message(misc = sunllo.messages.Misc(close_reason = sunllo.messages.CloseReason(reason = "closed by user"))))
            }
            connection.close()
            end(TerminalPhase.CLOSED, "")
            scope.cancel()
        }
    }

    private fun build(): TerminalSnapshot {
        val back = screen.scrollback.size
        val lines = ArrayList<TerminalLine>(screen.rows)
        for (row in 0 until screen.rows) {
            val absolute = back - scrollOffset + row
            val cells = when {
                absolute < 0 -> null
                absolute < back -> screen.scrollback[absolute]
                absolute - back < screen.rows -> screen.row(absolute - back)
                else -> null
            }
            lines.add(TerminalLine(if (cells == null) emptyList() else runs(cells)))
        }

        return TerminalSnapshot(
            columns = screen.columns,
            rows = screen.rows,
            lines = lines,
            cursorColumn = screen.cursorColumn,
            cursorRow = screen.cursorRow,
            cursorVisible = scrollOffset == 0 && screen.cursorVisible,
            scrolledBack = scrollOffset,
            scrollbackLines = back,
            title = screen.title,
        )
    }

    private fun runs(cells: Array<TerminalCell>): List<TerminalRun> {
        val runs = ArrayList<TerminalRun>()
        var start = -1
        var text = StringBuilder()
        var style: CellColors? = null

        fun flush(end: Int) {
            val s = style ?: return
            if (start < 0) return
            val (fg, bg) = TerminalPalette.resolve(s)
            val body = text.toString()
            val decorated = s.attributes and (CellAttribute.UNDERLINE or CellAttribute.STRIKE) != 0
            // Blank stretches on the default background draw nothing and are left out.
            if (body.isNotBlank() || bg != TerminalPalette.BACKGROUND || decorated) {
                runs.add(
                    TerminalRun(
                        column = start,
                        cells = end - start,
                        text = body,
                        foreground = fg,
                        background = bg,
                        bold = s.attributes and CellAttribute.BOLD != 0,
                        italic = s.attributes and CellAttribute.ITALIC != 0,
                        underline = s.attributes and CellAttribute.UNDERLINE != 0,
                        strike = s.attributes and CellAttribute.STRIKE != 0,
                    ),
                )
            }
            start = -1
            text = StringBuilder()
            style = null
        }

        var column = 0
        while (column < cells.size) {
            val cell = cells[column]
            if (cell.isSpacer) {
                column++
                continue
            }

            if (cell.width == 2) {
                flush(column)
                start = column
                style = cell.colors
                text.append(cell.text)
                flush(column + 2)
                column += 2
                continue
            }

            if (style != cell.colors) {
                flush(column)
                start = column
                style = cell.colors
            }
            text.append(cell.text)
            column++
        }

        flush(cells.size)
        return runs
    }
}
