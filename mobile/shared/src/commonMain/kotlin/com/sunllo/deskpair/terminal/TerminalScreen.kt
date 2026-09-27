package com.sunllo.deskpair.terminal

/** Cell attributes, as bit flags; the same bits as the desktop's `CellAttributes`. */
public object CellAttribute {
    public const val BOLD: Int = 1
    public const val FAINT: Int = 2
    public const val ITALIC: Int = 4
    public const val UNDERLINE: Int = 8
    public const val BLINK: Int = 16
    public const val INVERSE: Int = 32
    public const val INVISIBLE: Int = 64
    public const val STRIKE: Int = 128
}

/**
 * How a cell is drawn. A colour is one number: 0 the default, 1..256 a palette entry plus one, and
 * [RGB_TAG] or'd with a 24-bit value for true colour -- the desktop's encoding, so the vectors read the same.
 */
internal data class CellColors(val foreground: Int, val background: Int, val attributes: Int) {
    val erased: CellColors get() = CellColors(DEFAULT, background, 0)

    companion object {
        const val DEFAULT = 0
        const val RGB_TAG = 0x0100_0000
        val PLAIN = CellColors(DEFAULT, DEFAULT, 0)

        fun palette(index: Int): Int = index.coerceIn(0, 255) + 1

        fun rgb(r: Int, g: Int, b: Int): Int = RGB_TAG or ((r and 0xFF) shl 16) or ((g and 0xFF) shl 8) or (b and 0xFF)
    }
}

/** One character position; a wide character is a cell of width 2 followed by a spacer of width 0. */
internal data class TerminalCell(val codePoint: Int, val colors: CellColors, val width: Int) {
    val isSpacer: Boolean get() = width == 0

    val text: String get() = if (width == 0) "" else codePointToString(if (codePoint == 0) ' '.code else codePoint)

    companion object {
        fun space(colors: CellColors) = TerminalCell(' '.code, colors.erased, 1)
    }
}

internal fun codePointToString(cp: Int): String = if (cp < 0x10000) {
    cp.toChar().toString()
} else {
    val v = cp - 0x10000
    charArrayOf((0xD800 + (v shr 10)).toChar(), (0xDC00 + (v and 0x3FF)).toChar()).concatToString()
}

/**
 * The terminal's picture, the desktop's `TerminalScreen` in Kotlin: grid, cursor, scroll region, main and
 * alternate screens, a bounded scrollback, and the modes that change what the next byte means. The same
 * corners are handled the same way -- the pending wrap, the wide character at the right edge, half of a
 * wide character overwritten -- and `tests/fixtures/vt` checks both copies against one set of answers.
 */
internal class TerminalScreen(columns: Int, rows: Int) : VtHandler {

    companion object {
        const val SCROLLBACK_LINES = 10_000
        private const val MAX_DIMENSION = 1000
    }

    var columns: Int = columns.coerceIn(1, MAX_DIMENSION)
        private set
    var rows: Int = rows.coerceIn(1, MAX_DIMENSION)
        private set

    private val parser = VtParser(this)
    private val replies = mutableListOf<ByteArray>()
    val scrollback: ArrayDeque<Array<TerminalCell>> = ArrayDeque()
    private var primary = blank(this.columns, this.rows)
    private var alternate = blank(this.columns, this.rows)
    private var grid = primary
    private var tabStops = tabStops(this.columns)
    private var x = 0
    private var y = 0
    private var wrapPending = false
    private var pen = CellColors.PLAIN
    private var top = 0
    private var bottom = this.rows - 1
    private var originMode = false
    private var g0Graphics = false
    private var g1Graphics = false
    private var shiftOut = false
    private var savedPrimary = Saved()
    private var savedAlternate = Saved()

    private data class Saved(
        val x: Int = 0, val y: Int = 0, val pen: CellColors = CellColors.PLAIN, val originMode: Boolean = false,
        val g0: Boolean = false, val g1: Boolean = false, val shiftOut: Boolean = false, val wrapPending: Boolean = false,
    )

    val cursorColumn: Int get() = x
    val cursorRow: Int get() = y
    var cursorVisible: Boolean = true
        private set
    var autoWrap: Boolean = true
        private set
    var insertMode: Boolean = false
        private set
    var applicationCursorKeys: Boolean = false
        private set
    var applicationKeypad: Boolean = false
        private set
    var bracketedPaste: Boolean = false
        private set
    var newLineMode: Boolean = false
        private set
    val onAlternateScreen: Boolean get() = grid === alternate
    var title: String = ""
        private set

    /** Bumped on every change, so a snapshot knows whether it is stale. */
    var version: Long = 0
        private set

    fun feed(bytes: ByteArray, offset: Int = 0, length: Int = bytes.size - offset) {
        parser.feed(bytes, offset, length)
        version++
    }

    fun feed(text: String) = feed(text.encodeToByteArray())

    fun row(index: Int): Array<TerminalCell> = grid[index]

    fun rowText(index: Int): String = buildString { grid[index].forEach { append(it.text) } }.trimEnd(' ')

    fun takeReplies(): List<ByteArray> {
        if (replies.isEmpty()) {
            return emptyList()
        }

        val taken = replies.toList()
        replies.clear()
        return taken
    }

    fun resize(newColumns: Int, newRows: Int) {
        val c = newColumns.coerceIn(1, MAX_DIMENSION)
        val r = newRows.coerceIn(1, MAX_DIMENSION)
        if (c == columns && r == rows) {
            return
        }

        val alt = onAlternateScreen
        val shift = maxOf(0, y - (r - 1))
        if (!alt) {
            for (i in 0 until shift) {
                pushScrollback(primary[i])
            }
        }

        primary = refit(primary, c, r, if (alt) 0 else shift)
        alternate = refit(alternate, c, r, if (alt) shift else 0)
        grid = if (alt) alternate else primary
        columns = c
        rows = r
        tabStops = tabStops(c)
        top = 0
        bottom = r - 1
        y = (y - shift).coerceIn(0, r - 1)
        x = minOf(x, c - 1)
        wrapPending = false
        version++
    }

    // ---- VtHandler ----

    override fun print(codePoint: Int) {
        var code = codePoint
        if (if (shiftOut) g1Graphics else g0Graphics) {
            code = decGraphics(code)
        }

        val width = CharWidth.of(code)
        if (width <= 0) {
            return
        }

        if (wrapPending || (width == 2 && x == columns - 1 && autoWrap)) {
            if (autoWrap) {
                x = 0
                lineFeed()
            }

            wrapPending = false
        }

        if (width == 2 && x == columns - 1) {
            return
        }

        val line = grid[y]
        if (insertMode) {
            line.copyInto(line, x + width, x, columns - width)
        }

        clearWideAt(line, x)
        line[x] = TerminalCell(code, pen, width)
        if (width == 2) {
            clearWideAt(line, x + 1)
            line[x + 1] = TerminalCell(0, pen, 0)
        }

        if (x + width >= columns) {
            x = columns - 1
            wrapPending = autoWrap
        } else {
            x += width
        }
    }

    override fun execute(control: Int) {
        when (control) {
            0x08 -> {
                if (x > 0) x--
                wrapPending = false
            }
            0x09 -> {
                x = nextTab(x)
                wrapPending = false
            }
            0x0A, 0x0B, 0x0C -> {
                lineFeed()
                if (newLineMode) x = 0
                wrapPending = false
            }
            0x0D -> {
                x = 0
                wrapPending = false
            }
            0x0E -> shiftOut = true
            0x0F -> shiftOut = false
        }
    }

    override fun escDispatch(intermediates: IntArray, final: Int) {
        if (intermediates.size == 1 && (intermediates[0] == '('.code || intermediates[0] == ')'.code)) {
            val graphics = final == '0'.code
            if (intermediates[0] == '('.code) g0Graphics = graphics else g1Graphics = graphics
            return
        }

        if (intermediates.size == 1 && intermediates[0] == '#'.code && final == '8'.code) {
            grid.forEach { it.fill(TerminalCell('E'.code, CellColors.PLAIN, 1)) }
            return
        }

        if (intermediates.isNotEmpty()) {
            return
        }

        when (final.toChar()) {
            '7' -> save()
            '8' -> restore()
            'D' -> lineFeed()
            'E' -> {
                x = 0
                lineFeed()
            }
            'M' -> reverseLineFeed()
            'H' -> tabStops[x] = true
            '=' -> applicationKeypad = true
            '>' -> applicationKeypad = false
            'c' -> reset()
        }

        wrapPending = false
    }

    override fun csiDispatch(parameters: IntArray, intermediates: IntArray, final: Int) {
        val marker = if (intermediates.isNotEmpty()) intermediates[0] else 0
        if (marker == '?'.code) {
            if (final == 'h'.code || final == 'l'.code) {
                parameters.forEach { setPrivateMode(it, final == 'h'.code) }
            }
            return
        }

        if (marker == '>'.code) {
            if (final == 'c'.code) reply("\u001b[>0;10;1c")
            return
        }

        if (marker != 0) {
            return
        }

        val n = param(parameters, 0, 1)
        when (final.toChar()) {
            'A' -> moveTo(x, maxOf(y - n, if (y >= top) top else 0))
            'B', 'e' -> moveTo(x, minOf(y + n, if (y <= bottom) bottom else rows - 1))
            'C', 'a' -> moveTo(x + n, y)
            'D' -> moveTo(x - n, y)
            'E' -> moveTo(0, minOf(y + n, bottom))
            'F' -> moveTo(0, maxOf(y - n, top))
            'G', '`' -> moveTo(n - 1, y)
            'd' -> moveTo(x, (if (originMode) top else 0) + n - 1)
            'H', 'f' -> moveTo(param(parameters, 1, 1) - 1, (if (originMode) top else 0) + param(parameters, 0, 1) - 1)
            'J' -> eraseDisplay(param(parameters, 0, 0))
            'K' -> eraseLine(param(parameters, 0, 0))
            'L' -> insertLines(n)
            'M' -> deleteLines(n)
            '@' -> insertChars(n)
            'P' -> deleteChars(n)
            'X' -> eraseChars(n)
            'S' -> repeat(n) { scrollUp(top, bottom) }
            'T' -> repeat(n) { scrollDown(top, bottom) }
            'g' -> when (param(parameters, 0, 0)) {
                3 -> tabStops.fill(false)
                0 -> tabStops[x] = false
            }
            'r' -> setScrollRegion(param(parameters, 0, 1) - 1, param(parameters, 1, rows) - 1)
            'm' -> selectGraphicRendition(parameters)
            'h', 'l' -> parameters.forEach {
                if (it == 4) insertMode = final == 'h'.code
                if (it == 20) newLineMode = final == 'h'.code
            }
            'n' -> when (param(parameters, 0, 0)) {
                5 -> reply("\u001b[0n")
                6 -> reply("\u001b[${(if (originMode) y - top else y) + 1};${x + 1}R")
            }
            'c' -> if (param(parameters, 0, 0) == 0) reply("\u001b[?1;2c")
            's' -> save()
            'u' -> restore()
        }
    }

    override fun oscDispatch(data: ByteArray) {
        val semi = data.indexOf(';'.code.toByte())
        if (semi <= 0) {
            return
        }

        val command = data.copyOf(semi).decodeToString()
        if (command == "0" || command == "2") {
            title = data.copyOfRange(semi + 1, data.size).decodeToString()
        }
    }

    // ---- behaviour ----

    private fun setPrivateMode(mode: Int, on: Boolean) {
        when (mode) {
            1 -> applicationCursorKeys = on
            6 -> {
                originMode = on
                moveTo(0, if (on) top else 0)
            }
            7 -> {
                autoWrap = on
                if (!on) wrapPending = false
            }
            25 -> cursorVisible = on
            47, 1047 -> switchScreen(on, clearOnEnter = mode == 1047, saveCursor = false)
            1048 -> if (on) save() else restore()
            1049 -> switchScreen(on, clearOnEnter = true, saveCursor = true)
            2004 -> bracketedPaste = on
        }
    }

    private fun switchScreen(toAlternate: Boolean, clearOnEnter: Boolean, saveCursor: Boolean) {
        if (toAlternate == onAlternateScreen) {
            return
        }

        if (toAlternate) {
            if (saveCursor) save()
            grid = alternate
            if (clearOnEnter) alternate.forEach { it.fill(TerminalCell.space(pen)) }
        } else {
            grid = primary
            if (saveCursor) restore()
        }
    }

    private fun save() {
        val s = Saved(x, y, pen, originMode, g0Graphics, g1Graphics, shiftOut, wrapPending)
        if (onAlternateScreen) savedAlternate = s else savedPrimary = s
    }

    private fun restore() {
        val s = if (onAlternateScreen) savedAlternate else savedPrimary
        x = minOf(s.x, columns - 1)
        y = minOf(s.y, rows - 1)
        pen = s.pen
        originMode = s.originMode
        g0Graphics = s.g0
        g1Graphics = s.g1
        shiftOut = s.shiftOut
        wrapPending = s.wrapPending
    }

    private fun reset() {
        primary = blank(columns, rows)
        alternate = blank(columns, rows)
        grid = primary
        scrollback.clear()
        tabStops = tabStops(columns)
        x = 0
        y = 0
        wrapPending = false
        pen = CellColors.PLAIN
        top = 0
        bottom = rows - 1
        originMode = false
        g0Graphics = false
        g1Graphics = false
        shiftOut = false
        savedPrimary = Saved()
        savedAlternate = Saved()
        autoWrap = true
        cursorVisible = true
        insertMode = false
        applicationCursorKeys = false
        applicationKeypad = false
        bracketedPaste = false
        newLineMode = false
    }

    private fun moveTo(nx: Int, ny: Int) {
        val minY = if (originMode) top else 0
        val maxY = if (originMode) bottom else rows - 1
        x = nx.coerceIn(0, columns - 1)
        y = ny.coerceIn(minY, maxY)
        wrapPending = false
    }

    private fun lineFeed() {
        if (y == bottom) {
            scrollUp(top, bottom)
        } else if (y < rows - 1) {
            y++
        }
    }

    private fun reverseLineFeed() {
        if (y == top) {
            scrollDown(top, bottom)
        } else if (y > 0) {
            y--
        }
    }

    private fun scrollUp(from: Int, to: Int) {
        var leaving = grid[from]
        if (from == 0 && to == rows - 1 && !onAlternateScreen) {
            pushScrollback(leaving)
            leaving = Array(columns) { TerminalCell.space(pen) }
        }

        for (r in from until to) {
            grid[r] = grid[r + 1]
        }

        leaving.fill(TerminalCell.space(pen))
        grid[to] = leaving
    }

    private fun scrollDown(from: Int, to: Int) {
        val leaving = grid[to]
        for (r in to downTo from + 1) {
            grid[r] = grid[r - 1]
        }

        leaving.fill(TerminalCell.space(pen))
        grid[from] = leaving
    }

    private fun pushScrollback(line: Array<TerminalCell>) {
        scrollback.addLast(line)
        while (scrollback.size > SCROLLBACK_LINES) {
            scrollback.removeFirst()
        }
    }

    private fun setScrollRegion(t: Int, b: Int) {
        val bb = minOf(b, rows - 1)
        if (t < 0 || t >= bb) {
            return
        }

        top = t
        bottom = bb
        moveTo(0, if (originMode) top else 0)
    }

    private fun insertLines(n: Int) {
        if (y < top || y > bottom) return
        repeat(minOf(n, bottom - y + 1)) { scrollDown(y, bottom) }
        x = 0
        wrapPending = false
    }

    private fun deleteLines(n: Int) {
        if (y < top || y > bottom) return
        repeat(minOf(n, bottom - y + 1)) {
            val leaving = grid[y]
            for (r in y until bottom) {
                grid[r] = grid[r + 1]
            }
            leaving.fill(TerminalCell.space(pen))
            grid[bottom] = leaving
        }
        x = 0
        wrapPending = false
    }

    private fun insertChars(count: Int) {
        val line = grid[y]
        val n = minOf(count, columns - x)
        line.copyInto(line, x + n, x, columns - n)
        line.fill(TerminalCell.space(pen), x, x + n)
        wrapPending = false
    }

    private fun deleteChars(count: Int) {
        val line = grid[y]
        val n = minOf(count, columns - x)
        line.copyInto(line, x, x + n, columns)
        line.fill(TerminalCell.space(pen), columns - n, columns)
        wrapPending = false
    }

    private fun eraseChars(count: Int) {
        grid[y].fill(TerminalCell.space(pen), x, x + minOf(count, columns - x))
        wrapPending = false
    }

    private fun eraseLine(mode: Int) {
        val (from, to) = when (mode) {
            1 -> 0 to x + 1
            2 -> 0 to columns
            else -> x to columns
        }
        grid[y].fill(TerminalCell.space(pen), from, to)
        wrapPending = false
    }

    private fun eraseDisplay(mode: Int) {
        when (mode) {
            0 -> {
                eraseLine(0)
                for (r in y + 1 until rows) grid[r].fill(TerminalCell.space(pen))
            }
            1 -> {
                eraseLine(1)
                for (r in 0 until y) grid[r].fill(TerminalCell.space(pen))
            }
            2 -> grid.forEach { it.fill(TerminalCell.space(pen)) }
            3 -> scrollback.clear()
        }
        wrapPending = false
    }

    private fun selectGraphicRendition(p: IntArray) {
        if (p.isEmpty()) {
            pen = CellColors.PLAIN
            return
        }

        var fg = pen.foreground
        var bg = pen.background
        var a = pen.attributes
        var i = 0
        while (i < p.size) {
            when (val code = p[i]) {
                0 -> {
                    fg = CellColors.DEFAULT
                    bg = CellColors.DEFAULT
                    a = 0
                }
                1 -> a = a or CellAttribute.BOLD
                2 -> a = a or CellAttribute.FAINT
                3 -> a = a or CellAttribute.ITALIC
                4 -> a = a or CellAttribute.UNDERLINE
                5, 6 -> a = a or CellAttribute.BLINK
                7 -> a = a or CellAttribute.INVERSE
                8 -> a = a or CellAttribute.INVISIBLE
                9 -> a = a or CellAttribute.STRIKE
                21, 22 -> a = a and (CellAttribute.BOLD or CellAttribute.FAINT).inv()
                23 -> a = a and CellAttribute.ITALIC.inv()
                24 -> a = a and CellAttribute.UNDERLINE.inv()
                25 -> a = a and CellAttribute.BLINK.inv()
                27 -> a = a and CellAttribute.INVERSE.inv()
                28 -> a = a and CellAttribute.INVISIBLE.inv()
                29 -> a = a and CellAttribute.STRIKE.inv()
                in 30..37 -> fg = CellColors.palette(code - 30)
                39 -> fg = CellColors.DEFAULT
                in 40..47 -> bg = CellColors.palette(code - 40)
                49 -> bg = CellColors.DEFAULT
                in 90..97 -> fg = CellColors.palette(code - 90 + 8)
                in 100..107 -> bg = CellColors.palette(code - 100 + 8)
                38, 48 -> {
                    val color: Int
                    if (i + 2 < p.size && p[i + 1] == 5) {
                        color = CellColors.palette(p[i + 2])
                        i += 2
                    } else if (i + 4 < p.size && p[i + 1] == 2) {
                        color = CellColors.rgb(p[i + 2], p[i + 3], p[i + 4])
                        i += 4
                    } else {
                        i = p.size
                        continue
                    }

                    if (code == 38) fg = color else bg = color
                }
            }
            i++
        }

        pen = CellColors(fg, bg, a)
    }

    private fun reply(text: String) {
        replies.add(text.encodeToByteArray())
    }

    private fun nextTab(from: Int): Int {
        for (c in from + 1 until columns) {
            if (tabStops[c]) return c
        }
        return columns - 1
    }

    private fun clearWideAt(line: Array<TerminalCell>, at: Int) {
        if (at < 0 || at >= columns) return
        if (line[at].width == 2 && at + 1 < columns) {
            line[at + 1] = TerminalCell.space(pen)
        } else if (line[at].isSpacer && at > 0) {
            line[at - 1] = TerminalCell.space(pen)
        }
    }

    private fun param(p: IntArray, index: Int, fallback: Int) = if (index < p.size && p[index] != 0) p[index] else fallback

    private fun blank(c: Int, r: Int): Array<Array<TerminalCell>> = Array(r) { Array(c) { TerminalCell.space(CellColors.PLAIN) } }

    private fun refit(g: Array<Array<TerminalCell>>, c: Int, r: Int, dropTop: Int): Array<Array<TerminalCell>> {
        val fitted = blank(c, r)
        var row = 0
        while (row < r && row + dropTop < g.size) {
            val from = g[row + dropTop]
            from.copyInto(fitted[row], 0, 0, minOf(c, from.size))
            if (c < from.size && fitted[row][c - 1].width == 2) {
                fitted[row][c - 1] = TerminalCell.space(CellColors.PLAIN)
            }
            row++
        }
        return fitted
    }

    private fun tabStops(c: Int) = BooleanArray(c) { it >= 8 && it % 8 == 0 }

    private fun decGraphics(code: Int): Int = when (code.toChar()) {
        '`' -> '◆'.code; 'a' -> '▒'.code; 'f' -> '°'.code; 'g' -> '±'.code
        'j' -> '┘'.code; 'k' -> '┐'.code; 'l' -> '┌'.code; 'm' -> '└'.code
        'n' -> '┼'.code; 'o' -> '⎺'.code; 'p' -> '⎻'.code; 'q' -> '─'.code
        'r' -> '⎼'.code; 's' -> '⎽'.code; 't' -> '├'.code; 'u' -> '┤'.code
        'v' -> '┴'.code; 'w' -> '┬'.code; 'x' -> '│'.code; 'y' -> '≤'.code
        'z' -> '≥'.code; '{' -> 'π'.code; '|' -> '≠'.code; '}' -> '£'.code; '~' -> '·'.code
        else -> code
    }
}
