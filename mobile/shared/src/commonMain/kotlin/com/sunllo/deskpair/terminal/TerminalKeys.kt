package com.sunllo.deskpair.terminal

/** The keys that are not text. */
public enum class TerminalKey {
    ENTER, TAB, BACKSPACE, ESCAPE, UP, DOWN, RIGHT, LEFT, HOME, END, PAGE_UP, PAGE_DOWN, INSERT, DELETE,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

/**
 * What a key press becomes on the wire, as xterm sends it -- the desktop's `TerminalKeys`, and the same
 * bytes from both phones, because the table lives here and not in either app.
 */
public object TerminalKeys {

    /** A special key; [shift], [alt] and [control] become xterm's modifier parameter (`ESC [1;5A` is Ctrl+Up). */
    public fun key(key: TerminalKey, shift: Boolean = false, alt: Boolean = false, control: Boolean = false, applicationCursorKeys: Boolean = false): ByteArray {
        val mod = 1 + (if (shift) 1 else 0) + (if (alt) 2 else 0) + (if (control) 4 else 0)
        fun cursor(final: Char) = when {
            mod > 1 -> "\u001b[1;$mod$final"
            applicationCursorKeys -> "\u001bO$final"
            else -> "\u001b[$final"
        }
        fun ss3(final: Char) = if (mod > 1) "\u001b[1;$mod$final" else "\u001bO$final"
        fun tilde(n: Int) = if (mod > 1) "\u001b[$n;$mod~" else "\u001b[$n~"

        val sequence = when (key) {
            TerminalKey.ENTER -> if (alt) "\u001b\r" else "\r"
            TerminalKey.TAB -> if (shift) "\u001b[Z" else "\t"
            TerminalKey.BACKSPACE -> when {
                alt -> "\u001b\u007f"
                control -> "\u0008"
                else -> "\u007f"
            }
            TerminalKey.ESCAPE -> "\u001b"
            TerminalKey.UP -> cursor('A')
            TerminalKey.DOWN -> cursor('B')
            TerminalKey.RIGHT -> cursor('C')
            TerminalKey.LEFT -> cursor('D')
            TerminalKey.HOME -> cursor('H')
            TerminalKey.END -> cursor('F')
            TerminalKey.INSERT -> tilde(2)
            TerminalKey.DELETE -> tilde(3)
            TerminalKey.PAGE_UP -> tilde(5)
            TerminalKey.PAGE_DOWN -> tilde(6)
            TerminalKey.F1 -> ss3('P')
            TerminalKey.F2 -> ss3('Q')
            TerminalKey.F3 -> ss3('R')
            TerminalKey.F4 -> ss3('S')
            TerminalKey.F5 -> tilde(15)
            TerminalKey.F6 -> tilde(17)
            TerminalKey.F7 -> tilde(18)
            TerminalKey.F8 -> tilde(19)
            TerminalKey.F9 -> tilde(20)
            TerminalKey.F10 -> tilde(21)
            TerminalKey.F11 -> tilde(23)
            TerminalKey.F12 -> tilde(24)
        }
        return sequence.encodeToByteArray()
    }

    /** Typed text; with [control], a single letter becomes its C0 control (Ctrl+C is 0x03); [alt] prefixes ESC. */
    public fun text(text: String, alt: Boolean = false, control: Boolean = false): ByteArray {
        val bytes = if (control && text.length == 1) {
            controlOf(text[0])?.let { byteArrayOf(it.toByte()) } ?: text.encodeToByteArray()
        } else {
            text.encodeToByteArray()
        }

        return if (alt) byteArrayOf(0x1B) + bytes else bytes
    }

    /** A paste: line endings become CR; in bracketed mode it is framed, and no end marker inside survives. */
    public fun paste(text: String, bracketed: Boolean): ByteArray {
        var body = text.replace("\r\n", "\r").replace('\n', '\r')
        if (!bracketed) {
            return body.encodeToByteArray()
        }

        body = body.replace("\u001b[201~", "")
        return "\u001b[200~$body\u001b[201~".encodeToByteArray()
    }

    /** Whether a paste should be confirmed first: more than one line runs more than one command. */
    public fun isMultiLine(text: String): Boolean = text.trimEnd('\r', '\n').any { it == '\r' || it == '\n' }

    private fun controlOf(c: Char): Int? = when (c) {
        in 'a'..'z' -> c - 'a' + 1
        in 'A'..'Z' -> c - 'A' + 1
        ' ', '@', '2' -> 0
        '[', '3' -> 0x1B
        '\\', '4' -> 0x1C
        ']', '5' -> 0x1D
        '^', '6' -> 0x1E
        '_', '7', '/' -> 0x1F
        '8', '?' -> 0x7F
        else -> null
    }
}

/** The desktop's `TerminalPalette`, as ARGB, so both apps draw the same colours. */
internal object TerminalPalette {
    const val BACKGROUND: Int = 0xFF14171C.toInt()
    const val FOREGROUND: Int = 0xFFD8DEE6.toInt()
    const val CURSOR: Int = 0xFF7FC8F8.toInt()

    private val named = intArrayOf(
        0xFF1C1F24.toInt(), 0xFFE06C75.toInt(), 0xFF98C379.toInt(), 0xFFE5C07B.toInt(),
        0xFF61AFEF.toInt(), 0xFFC678DD.toInt(), 0xFF56B6C2.toInt(), 0xFFABB2BF.toInt(),
        0xFF5C6370.toInt(), 0xFFFF7A85.toInt(), 0xFFB5E090.toInt(), 0xFFFFD88F.toInt(),
        0xFF82C4FF.toInt(), 0xFFDD92F2.toInt(), 0xFF76D4E0.toInt(), 0xFFF0F3F6.toInt(),
    )

    fun indexed(index: Int): Int {
        if (index < 16) {
            return named[maxOf(0, index)]
        }

        if (index < 232) {
            val i = index - 16
            fun level(n: Int) = if (n == 0) 0 else 55 + n * 40
            return argb(level(i / 36), level(i / 6 % 6), level(i % 6))
        }

        val grey = 8 + (minOf(index, 255) - 232) * 10
        return argb(grey, grey, grey)
    }

    /** Foreground and background as drawn, with bold, inverse, faint and invisible applied. */
    fun resolve(colors: CellColors): Pair<Int, Int> {
        val bold = colors.attributes and CellAttribute.BOLD != 0
        var fg = pick(colors.foreground, FOREGROUND, bold)
        var bg = pick(colors.background, BACKGROUND, false)
        if (colors.attributes and CellAttribute.INVERSE != 0) {
            val t = fg
            fg = bg
            bg = t
        }
        if (colors.attributes and CellAttribute.FAINT != 0) {
            fg = blend(fg, bg)
        }
        if (colors.attributes and CellAttribute.INVISIBLE != 0) {
            fg = bg
        }
        return fg to bg
    }

    private fun pick(color: Int, fallback: Int, bright: Boolean): Int = when {
        color in 1..256 -> {
            val index = color - 1
            indexed(if (bright && index < 8) index + 8 else index)
        }
        color and CellColors.RGB_TAG != 0 -> 0xFF000000.toInt() or (color and 0xFFFFFF)
        else -> fallback
    }

    private fun blend(a: Int, b: Int): Int {
        fun mix(shift: Int) = (((a shr shift) and 0xFF) + ((b shr shift) and 0xFF)) / 2
        return argb(mix(16), mix(8), mix(0))
    }

    private fun argb(r: Int, g: Int, b: Int): Int = (0xFF shl 24) or (r shl 16) or (g shl 8) or b
}
