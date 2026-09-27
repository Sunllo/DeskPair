package com.sunllo.deskpair.terminal

/** What the parser tells the screen. Every byte the shell writes ends up in exactly one of these. */
internal interface VtHandler {
    /** A printable character, already decoded from UTF-8, as a code point. */
    fun print(codePoint: Int)

    /** A C0 control: BEL, BS, HT, LF, VT, FF, CR, SO, SI and the rest. */
    fun execute(control: Int)

    /** CSI: `ESC [ params intermediates final`. Private markers arrive first in [intermediates]. */
    fun csiDispatch(parameters: IntArray, intermediates: IntArray, final: Int)

    fun escDispatch(intermediates: IntArray, final: Int)

    /** OSC: the bytes between `ESC ]` and BEL or ST. */
    fun oscDispatch(data: ByteArray)
}

/**
 * Paul Williams's DEC state machine with a UTF-8 decoder in front, byte for byte the desktop's
 * `DeskPair.Core.Terminal.VtParser`. Two copies of one parser only stay one parser if the same vectors hold
 * both to it: `tests/fixtures/vt` is read by this module's tests as well as the desktop's.
 */
internal class VtParser(private val handler: VtHandler) {

    private enum class State {
        GROUND, ESCAPE, ESCAPE_INTERMEDIATE, CSI_ENTRY, CSI_PARAM, CSI_INTERMEDIATE, CSI_IGNORE,
        DCS_ENTRY, DCS_PASSTHROUGH, OSC_STRING, SOS_PM_APC_STRING,
    }

    private companion object {
        const val MAX_PARAMETERS = 32
        const val MAX_INTERMEDIATES = 4
        const val MAX_OSC = 4096
        const val REPLACEMENT = 0xFFFD
    }

    private val params = IntArray(MAX_PARAMETERS)
    private val intermediates = IntArray(MAX_INTERMEDIATES)
    private val osc = ByteArray(MAX_OSC)
    private val utf8 = IntArray(4)
    private var state = State.GROUND
    private var paramCount = 0
    private var paramStarted = false
    private var pendingEmpty = false
    private var intermediateCount = 0
    private var intermediateOverflow = false
    private var oscLength = 0
    private var utf8Length = 0
    private var utf8Expected = 0
    private var stringEscape = false

    fun feed(bytes: ByteArray, offset: Int = 0, length: Int = bytes.size - offset) {
        for (i in offset until offset + length) {
            step(bytes[i].toInt() and 0xFF)
        }
    }

    private fun inString() = state == State.OSC_STRING || state == State.DCS_PASSTHROUGH || state == State.SOS_PM_APC_STRING

    private fun step(b: Int) {
        if (stringEscape) {
            stringEscape = false
            if (b == '\\'.code) {
                endString(abandon = false)
                state = State.GROUND
                return
            }

            endString(abandon = true)
            state = State.GROUND
            enter(State.ESCAPE)
            step(b)
            return
        }

        if (state != State.GROUND && utf8Length > 0) {
            utf8Length = 0
        }

        when (b) {
            0x1B -> {
                if (inString()) {
                    stringEscape = true
                    return
                }

                enter(State.ESCAPE)
                return
            }
            0x18, 0x1A -> {
                if (inString()) {
                    endString(abandon = true)
                }

                handler.execute(b)
                state = State.GROUND
                return
            }
        }

        when (state) {
            State.GROUND -> ground(b)
            State.ESCAPE -> when {
                isControl(b) -> handler.execute(b)
                b in 0x20..0x2F -> {
                    collect(b)
                    state = State.ESCAPE_INTERMEDIATE
                }
                b == '['.code -> enter(State.CSI_ENTRY)
                b == ']'.code -> enter(State.OSC_STRING)
                b == 'P'.code -> enter(State.DCS_ENTRY)
                b == 'X'.code || b == '^'.code || b == '_'.code -> enter(State.SOS_PM_APC_STRING)
                b in 0x30..0x7E -> {
                    handler.escDispatch(intermediates.copyOf(intermediateCount), b)
                    state = State.GROUND
                }
            }
            State.ESCAPE_INTERMEDIATE -> when {
                isControl(b) -> handler.execute(b)
                b in 0x20..0x2F -> collect(b)
                b in 0x30..0x7E -> {
                    handler.escDispatch(intermediates.copyOf(intermediateCount), b)
                    state = State.GROUND
                }
            }
            State.CSI_ENTRY, State.CSI_PARAM -> when {
                isControl(b) -> handler.execute(b)
                b in '0'.code..'9'.code -> {
                    digit(b)
                    state = State.CSI_PARAM
                }
                b == ';'.code || b == ':'.code -> {
                    nextParam()
                    state = State.CSI_PARAM
                }
                b in 0x3C..0x3F -> if (state == State.CSI_ENTRY) {
                    collect(b)
                    state = State.CSI_PARAM
                } else {
                    state = State.CSI_IGNORE
                }
                b in 0x20..0x2F -> {
                    collect(b)
                    state = State.CSI_INTERMEDIATE
                }
                b in 0x40..0x7E -> dispatch(b)
            }
            State.CSI_INTERMEDIATE -> when {
                isControl(b) -> handler.execute(b)
                b in 0x20..0x2F -> collect(b)
                b in 0x30..0x3F -> state = State.CSI_IGNORE
                b in 0x40..0x7E -> dispatch(b)
            }
            State.CSI_IGNORE -> when {
                isControl(b) -> handler.execute(b)
                b in 0x40..0x7E -> state = State.GROUND
            }
            State.DCS_ENTRY -> if (b in 0x40..0x7E) {
                state = State.DCS_PASSTHROUGH
            }
            State.DCS_PASSTHROUGH, State.SOS_PM_APC_STRING -> if (b == 0x07 && state == State.SOS_PM_APC_STRING) {
                state = State.GROUND
            }
            State.OSC_STRING -> when {
                b == 0x07 -> {
                    endString(abandon = false)
                    state = State.GROUND
                }
                b >= 0x20 || b == 0x09 -> if (oscLength < MAX_OSC) {
                    osc[oscLength++] = b.toByte()
                }
            }
        }
    }

    private fun ground(b: Int) {
        if (b < 0x80) {
            if (utf8Length > 0) {
                utf8Length = 0
                handler.print(REPLACEMENT)
            }

            if (isControl(b)) {
                handler.execute(b)
            } else if (b != 0x7F) {
                handler.print(b)
            }
            return
        }

        if (utf8Length == 0) {
            utf8Expected = when (b) {
                in 0xC2..0xDF -> 2
                in 0xE0..0xEF -> 3
                in 0xF0..0xF4 -> 4
                else -> 0
            }
            if (utf8Expected == 0) {
                handler.print(REPLACEMENT)
                return
            }

            utf8[utf8Length++] = b
            return
        }

        if (b and 0xC0 != 0x80) {
            utf8Length = 0
            handler.print(REPLACEMENT)
            ground(b)
            return
        }

        utf8[utf8Length++] = b
        if (utf8Length == utf8Expected) {
            handler.print(decode())
            utf8Length = 0
        }
    }

    /** The collected sequence as a code point; overlong forms and surrogates are not characters. */
    private fun decode(): Int {
        val cp = when (utf8Length) {
            2 -> (utf8[0] and 0x1F shl 6) or (utf8[1] and 0x3F)
            3 -> (utf8[0] and 0x0F shl 12) or (utf8[1] and 0x3F shl 6) or (utf8[2] and 0x3F)
            else -> (utf8[0] and 0x07 shl 18) or (utf8[1] and 0x3F shl 12) or (utf8[2] and 0x3F shl 6) or (utf8[3] and 0x3F)
        }
        val minimum = when (utf8Length) {
            2 -> 0x80
            3 -> 0x800
            else -> 0x10000
        }
        return if (cp < minimum || cp in 0xD800..0xDFFF || cp > 0x10FFFF) REPLACEMENT else cp
    }

    private fun isControl(b: Int) = b <= 0x17 || b == 0x19 || b in 0x1C..0x1F

    private fun enter(next: State) {
        state = next
        paramCount = 0
        paramStarted = false
        pendingEmpty = false
        intermediateCount = 0
        intermediateOverflow = false
        oscLength = 0
        params.fill(0)
    }

    private fun collect(b: Int) {
        if (intermediateCount < MAX_INTERMEDIATES) {
            intermediates[intermediateCount++] = b
        } else {
            intermediateOverflow = true
        }
    }

    private fun digit(b: Int) {
        if (!paramStarted) {
            addParam()
            paramStarted = true
            pendingEmpty = false
        }

        val i = paramCount - 1
        params[i] = minOf(params[i] * 10 + (b - '0'.code), 65535)
    }

    private fun nextParam() {
        if (!paramStarted) {
            addParam()
        }

        paramStarted = false
        pendingEmpty = true
    }

    private fun addParam() {
        if (paramCount < MAX_PARAMETERS) {
            params[paramCount++] = 0
        }
    }

    private fun dispatch(final: Int) {
        if (pendingEmpty) {
            addParam()
            pendingEmpty = false
        }

        if (!intermediateOverflow) {
            handler.csiDispatch(params.copyOf(paramCount), intermediates.copyOf(intermediateCount), final)
        }

        state = State.GROUND
    }

    private fun endString(abandon: Boolean) {
        if (state == State.OSC_STRING && !abandon) {
            handler.oscDispatch(osc.copyOf(oscLength))
        }

        oscLength = 0
    }
}
