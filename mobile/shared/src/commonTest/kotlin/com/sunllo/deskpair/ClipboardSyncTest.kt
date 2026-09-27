package com.sunllo.deskpair

import com.sunllo.deskpair.clipboard.ClipboardSync
import com.sunllo.deskpair.framing.hexToBytes
import com.sunllo.deskpair.framing.toHex
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okio.ByteString.Companion.toByteString
import sunllo.messages.Clipboard
import sunllo.messages.ClipboardFormat
import sunllo.messages.MultiClipboards
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The clipboard hash, against the one the desktop computes.
 *
 * The host puts its own hash on the wire and both sides use it to recognise an echo. If the two disagree,
 * nothing fails loudly — the clipboard simply bounces back and forth, or a real copy is mistaken for an
 * echo and silently dropped. That is exactly the kind of bug a vector catches and a session does not.
 */
class ClipboardSyncTest {

    private val clipboard: JsonObject =
        Json.parseToJsonElement(PROTOCOL_VECTORS_JSON).jsonObject["clipboard"]!!.jsonObject

    @Test
    fun the_text_hash_matches_the_desktop() {
        for (case in clipboard["text"]!!.jsonArray) {
            val fields = case.jsonObject
            val name = fields["name"]!!.jsonPrimitive.content
            val text = fields["text"]!!.jsonPrimitive.content

            assertEquals(
                fields["utf8"]!!.jsonPrimitive.content,
                text.encodeToByteArray().toHex(),
                "$name: UTF-8 encoding differs from the desktop's",
            )

            val wire = MultiClipboards(
                items = listOf(
                    Clipboard(
                        format = ClipboardFormat.CF_TEXT,
                        content = text.encodeToByteArray().toByteString(),
                    ),
                ),
            )

            assertEquals(
                fields["hash"]!!.jsonPrimitive.content,
                ClipboardSync.hash(wire).toHex(),
                "$name: content hash differs from the desktop's",
            )
        }
    }

    @Test
    fun the_hash_sorts_by_format_rather_than_by_arrival() {
        val ordering = clipboard["ordering"]!!.jsonObject
        val html = Clipboard(
            format = ClipboardFormat.CF_HTML,
            content = ordering["html"]!!.jsonPrimitive.content.encodeToByteArray().toByteString(),
        )
        val text = Clipboard(
            format = ClipboardFormat.CF_TEXT,
            content = ordering["text"]!!.jsonPrimitive.content.encodeToByteArray().toByteString(),
        )

        // The desktop hashed these in format order after receiving them the other way round; both orders
        // have to produce the same digest or two machines holding the same clipboard will disagree.
        val expected = ordering["hash"]!!.jsonPrimitive.content
        assertEquals(expected, ClipboardSync.hash(MultiClipboards(items = listOf(html, text))).toHex())
        assertEquals(expected, ClipboardSync.hash(MultiClipboards(items = listOf(text, html))).toHex())
    }

    @Test
    fun textToWire_attaches_the_hash_the_host_will_check() {
        val wire = ClipboardSync.textToWire("hello from the desktop")
        assertEquals(ClipboardSync.hash(wire).toHex(), wire.content_hash.toByteArray().toHex())

        val expected = clipboard["text"]!!.jsonArray
            .first { it.jsonObject["name"]!!.jsonPrimitive.content == "ascii" }
            .jsonObject["hash"]!!.jsonPrimitive.content
        assertEquals(expected, wire.content_hash.toByteArray().toHex())
    }

    @Test
    fun a_clipboard_without_text_yields_nothing() {
        val imageOnly = MultiClipboards(
            items = listOf(
                Clipboard(format = ClipboardFormat.CF_IMAGE_PNG, content = "notreally".encodeToByteArray().toByteString()),
            ),
        )

        // A phone carries text. Handing an image's bytes back as a string would put mojibake on the
        // clipboard, which is worse than carrying nothing.
        assertNull(ClipboardSync.textFromWire(imageOnly))
        assertNull(ClipboardSync.textFromWire(MultiClipboards()))
    }

    @Test
    fun the_image_hash_matches_the_desktop() {
        val image = clipboard["image"]!!.jsonObject
        val png = image["png"]!!.jsonPrimitive.content.hexToBytes()

        val wire = ClipboardSync.imageToWire(png)
        assertEquals(1, wire.items.size)
        assertEquals(ClipboardFormat.CF_IMAGE_PNG, wire.items[0].format)

        // The bytes travel untouched: re-encoding a PNG on the way out would change the hash and lose
        // whatever the phone's encoder chose.
        assertEquals(png.toHex(), wire.items[0].content.toByteArray().toHex())
        assertEquals(
            image["hash"]!!.jsonPrimitive.content,
            wire.content_hash.toByteArray().toHex(),
            "the image hash differs from the desktop's",
        )
    }

    @Test
    fun text_and_an_image_hash_in_format_order_not_arrival_order() {
        val case = clipboard["textAndImage"]!!.jsonObject
        val png = case["png"]!!.jsonPrimitive.content.hexToBytes()
        val text = case["text"]!!.jsonPrimitive.content

        // The desktop built this one image-first. CF_TEXT is 0 and CF_IMAGE_PNG is 3, so anything that
        // hashes in the order it collected them disagrees here rather than in the field.
        val wire = ClipboardSync.toWire(text, png)!!
        assertEquals(
            case["hash"]!!.jsonPrimitive.content,
            wire.content_hash.toByteArray().toHex(),
            "the mixed text+image hash differs from the desktop's",
        )
    }

    @Test
    fun both_sides_agree_on_how_much_a_clipboard_may_carry() {
        assertEquals(clipboard["maxBytes"]!!.jsonPrimitive.content.toInt(), ClipboardSync.MAX_BYTES)
    }

    @Test
    fun something_too_large_is_left_out_rather_than_taking_the_rest_with_it() {
        val huge = ByteArray(ClipboardSync.MAX_BYTES + 1)
        val wire = ClipboardSync.toWire("keep me", huge)

        // The caption survives the picture that would not fit — the same rule the desktop's ToWire follows.
        assertEquals(1, wire!!.items.size)
        assertEquals(ClipboardFormat.CF_TEXT, wire.items[0].format)
        assertEquals("keep me", ClipboardSync.textFromWire(wire))

        // And nothing at all when there is nothing that fits.
        assertNull(ClipboardSync.toWire(null, huge))
        assertNull(ClipboardSync.toWire(null, null))
        assertNull(ClipboardSync.toWire("", ByteArray(0)))
    }

    @Test
    fun an_echo_is_refused_in_both_directions() {
        val sync = ClipboardSync()
        val ours = ClipboardSync.textToWire("copied here").content_hash.toByteArray()
        val theirs = ClipboardSync.textToWire("copied there").content_hash.toByteArray()

        assertTrue(sync.shouldSend(ours))
        sync.markSent(ours)

        // The host applies it, its own watcher fires, and it sends the same content back. Applying that
        // would fire ours in turn, and the two clipboards would never stop talking.
        assertFalse(sync.shouldApply(ours), "our own send must not come back as an incoming change")
        assertFalse(sync.shouldSend(ours), "the same content must not be sent twice")

        assertTrue(sync.shouldApply(theirs))
        sync.markApplied(theirs)
        assertFalse(sync.shouldSend(theirs), "what the host gave us must not be sent back to it")

        // Something genuinely new still gets through, which is the part a blunt "ignore for 500 ms" breaks.
        assertTrue(sync.shouldSend(ClipboardSync.textToWire("copied later").content_hash.toByteArray()))
    }

    @Test
    fun the_hash_is_stable_across_encodings_of_the_same_text() {
        // Same characters, so the same UTF-8 bytes, so the same hash: the desktop's vector for this text
        // already pins it, and this asserts the Kotlin side does not normalise or re-encode on the way.
        val expected = clipboard["text"]!!.jsonArray
            .first { it.jsonObject["name"]!!.jsonPrimitive.content == "unicode" }
        val text = expected.jsonObject["text"]!!.jsonPrimitive.content

        assertEquals(
            expected.jsonObject["utf8"]!!.jsonPrimitive.content.hexToBytes().toHex(),
            text.encodeToByteArray().toHex(),
        )
        assertEquals(
            expected.jsonObject["hash"]!!.jsonPrimitive.content,
            ClipboardSync.hash(ClipboardSync.textToWire(text)).toHex(),
        )
    }
}
