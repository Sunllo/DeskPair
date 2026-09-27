package com.sunllo.deskpair.terminal

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlin.random.Random
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * The desktop's terminal vectors, held against this parser. Each is fed whole, a byte at a time and in
 * seeded random pieces, exactly as `VtVectorTests` does in C#: the same files, the same answers.
 */
class VtVectorsTest {

    private val vectors: List<Pair<String, JsonObject>> = VT_VECTORS_JSON.flatMap { (file, text) ->
        Json.parseToJsonElement(text).jsonArray.map { v -> "$file: ${v.jsonObject["name"]!!.jsonPrimitive.content}" to v.jsonObject }
    }

    @Test
    fun there_are_vectors() {
        assertTrue(vectors.size >= 49, "found ${vectors.size}")
    }

    @Test
    fun every_vector_holds_however_the_bytes_are_cut() {
        val failures = mutableListOf<String>()
        for ((name, v) in vectors) {
            val input = v["inputHex"]?.jsonPrimitive?.content?.let(::hex) ?: v["input"]!!.jsonPrimitive.content.encodeToByteArray()
            val columns = v["columns"]!!.jsonPrimitive.int
            val rows = v["rows"]!!.jsonPrimitive.int

            val whole = TerminalScreen(columns, rows).apply { feed(input) }
            check(whole, v, "$name, fed whole")?.let(failures::add)

            val single = TerminalScreen(columns, rows)
            input.forEach { single.feed(byteArrayOf(it)) }
            check(single, v, "$name, a byte at a time")?.let(failures::add)

            val random = Random(input.size * 7919 + columns)
            val pieces = TerminalScreen(columns, rows)
            var at = 0
            while (at < input.size) {
                val n = minOf(random.nextInt(1, 5), input.size - at)
                pieces.feed(input, at, n)
                at += n
            }
            check(pieces, v, "$name, in random pieces")?.let(failures::add)
        }

        if (failures.isNotEmpty()) {
            fail(failures.joinToString("\n"))
        }
    }

    private fun check(screen: TerminalScreen, v: JsonObject, how: String): String? {
        val lines = v["lines"]!!.jsonArray.map { it.jsonPrimitive.content }
        for (r in 0 until screen.rows) {
            val want = lines.getOrElse(r) { "" }
            if (screen.rowText(r) != want) return "$how: row $r is \"${screen.rowText(r)}\", expected \"$want\""
        }

        val cursor = v["cursor"]!!.jsonArray.map { it.jsonPrimitive.int }
        if (screen.cursorColumn != cursor[0] || screen.cursorRow != cursor[1]) {
            return "$how: cursor at ${screen.cursorColumn},${screen.cursorRow}, expected ${cursor[0]},${cursor[1]}"
        }

        v["scrollback"]?.jsonArray?.map { it.jsonPrimitive.content }?.let { want ->
            val got = screen.scrollback.map { line -> line.joinToString("") { it.text }.trimEnd(' ') }
            if (got != want) return "$how: scrollback $got, expected $want"
        }

        v["replies"]?.jsonArray?.map { it.jsonPrimitive.content }?.let { want ->
            val got = screen.takeReplies().map { it.decodeToString() }
            if (got != want) return "$how: replies $got, expected $want"
        }

        v["title"]?.jsonPrimitive?.content?.let { want ->
            if (screen.title != want) return "$how: title \"${screen.title}\", expected \"$want\""
        }

        v["modes"]?.jsonObject?.forEach { (mode, value) ->
            val got = when (mode) {
                "alternateScreen" -> screen.onAlternateScreen
                "bracketedPaste" -> screen.bracketedPaste
                "applicationCursorKeys" -> screen.applicationCursorKeys
                "cursorVisible" -> screen.cursorVisible
                else -> return "$how: unknown mode $mode"
            }
            if (got != value.jsonPrimitive.boolean) return "$how: $mode is $got"
        }

        v["cells"]?.jsonArray?.forEach { c ->
            val o = c.jsonObject
            val row = o["row"]!!.jsonPrimitive.int
            val column = o["column"]!!.jsonPrimitive.int
            val cell = screen.row(row)[column]
            val fg = color(cell.colors.foreground)
            val bg = color(cell.colors.background)
            val attributes = attributes(cell.colors.attributes)
            val wantAttributes = o["attributes"]!!.jsonArray.map { it.jsonPrimitive.content }.sorted()
            if (fg != o["foreground"]!!.jsonPrimitive.content || bg != o["background"]!!.jsonPrimitive.content || attributes != wantAttributes) {
                return "$how: cell $row,$column is $fg/$bg/$attributes"
            }
        }

        return null
    }

    private fun color(c: Int): String = when {
        c == CellColors.DEFAULT -> "default"
        c in 1..256 -> "palette:${c - 1}"
        c and CellColors.RGB_TAG != 0 -> "rgb:" + (c and 0xFFFFFF).toString(16).padStart(6, '0')
        else -> "?"
    }

    private fun attributes(a: Int): List<String> = listOf(
        CellAttribute.BOLD to "bold", CellAttribute.FAINT to "faint", CellAttribute.ITALIC to "italic",
        CellAttribute.UNDERLINE to "underline", CellAttribute.BLINK to "blink", CellAttribute.INVERSE to "inverse",
        CellAttribute.INVISIBLE to "invisible", CellAttribute.STRIKE to "strike",
    ).filter { (bit, _) -> a and bit != 0 }.map { it.second }.sorted()

    private fun hex(s: String): ByteArray = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
}

/** The key table, the same bytes the desktop's tests pin. */
class TerminalKeysTest {
    @Test
    fun keys_match_the_desktop() {
        assertEquals("\u001b[A", TerminalKeys.key(TerminalKey.UP).decodeToString())
        assertEquals("\u001bOA", TerminalKeys.key(TerminalKey.UP, applicationCursorKeys = true).decodeToString())
        assertEquals("\u001b[1;5C", TerminalKeys.key(TerminalKey.RIGHT, control = true, applicationCursorKeys = true).decodeToString())
        assertEquals("\u001b[Z", TerminalKeys.key(TerminalKey.TAB, shift = true).decodeToString())
        assertEquals("\u001b[24~", TerminalKeys.key(TerminalKey.F12).decodeToString())
        assertEquals(listOf<Byte>(3), TerminalKeys.text("c", control = true).toList())
        assertEquals(listOf<Byte>(0x1B, 'b'.code.toByte()), TerminalKeys.text("b", alt = true).toList())
    }

    @Test
    fun a_paste_cannot_close_its_own_frame() {
        val pasted = TerminalKeys.paste("echo safe\u001b[201~\nrm -rf ~\n", bracketed = true).decodeToString()
        assertTrue(pasted.startsWith("\u001b[200~") && pasted.endsWith("\u001b[201~"))
        assertTrue("\u001b[201~" !in pasted.substring(6, pasted.length - 6))
        assertTrue(TerminalKeys.isMultiLine("ls\nrm x"))
        assertTrue(!TerminalKeys.isMultiLine("ls\n"))
    }
}

/** Wide characters stay on the grid in what the apps draw: a wide character is always a run of its own. */
class TerminalRunsTest {
    @Test
    fun cjk_widths() {
        assertEquals(2, CharWidth.of('中'.code))
        assertEquals(2, CharWidth.of('한'.code))
        assertEquals(1, CharWidth.of('a'.code))
        assertEquals(0, CharWidth.of(0x0301))
        assertEquals(1, CharWidth.of('─'.code))
    }
}
