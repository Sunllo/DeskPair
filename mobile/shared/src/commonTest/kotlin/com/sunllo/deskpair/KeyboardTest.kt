package com.sunllo.deskpair

import sunllo.messages.ControlKey
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.fail

/**
 * The keyboard mapping, checked in the direction that actually catches mistakes.
 *
 * Testing that every RemoteKey reaches a ControlKey proves nothing: the compiler already does that, because
 * the when is exhaustive. What matters is the other direction — a key added to the wire format should not be
 * silently absent from the client, and this fails when one is.
 */
class KeyboardTest {

    @Test
    fun every_protocol_key_is_either_mapped_or_refused_on_purpose() {
        val mapped = RemoteKey.entries.map { it.toWire() }.toSet()
        val refused = RemoteKey.DELIBERATELY_UNMAPPED.keys

        val forgotten = ControlKey.entries.filterNot { it in mapped || it in refused }
        if (forgotten.isNotEmpty()) {
            fail(
                "These keys exist in the protocol but this client neither sends nor refuses them: " +
                    forgotten.joinToString { it.name } +
                    ". Add them to RemoteKey, or to DELIBERATELY_UNMAPPED with a reason.",
            )
        }
    }

    @Test
    fun no_two_keys_mean_the_same_thing() {
        // Two RemoteKey values mapping to one ControlKey would make the UI offer a choice that is not one.
        val byWire = RemoteKey.entries.groupBy { it.toWire() }.filterValues { it.size > 1 }
        assertTrue(byWire.isEmpty(), "Duplicate mappings: $byWire")
    }

    @Test
    fun a_refusal_carries_a_reason() {
        for ((key, reason) in RemoteKey.DELIBERATELY_UNMAPPED) {
            assertTrue(reason.isNotBlank(), "$key is refused without saying why")
        }
    }

    @Test
    fun modifiers_map_to_the_left_hand_keys() {
        // The host treats a modifier in the list as "held", and the left-hand key is the one every layout has.
        assertEquals(ControlKey.CK_CONTROL, RemoteModifier.CONTROL.toWire())
        assertEquals(ControlKey.CK_SHIFT, RemoteModifier.SHIFT.toWire())
        assertEquals(ControlKey.CK_ALT, RemoteModifier.ALT.toWire())
        assertEquals(ControlKey.CK_META, RemoteModifier.META.toWire())
    }

    @Test
    fun scancodes_match_the_desktop_table() {
        // Spot values from Desktop/Input/KeyMapper.cs. If these drift, a shortcut lands on the wrong key and
        // nothing says so — Ctrl+C would become Ctrl+something.
        assertEquals(0x1E, Scancodes.of('a'))
        assertEquals(0x2E, Scancodes.of('c'))
        assertEquals(0x2F, Scancodes.of('v'))
        assertEquals(0x10, Scancodes.of('q'))
        assertEquals(0x02, Scancodes.of('1'))
        assertEquals(0x0B, Scancodes.of('0'))
        assertEquals(0x29, Scancodes.of('`'))
        assertEquals(0x0C, Scancodes.of('-'))
        assertEquals(0x0D, Scancodes.of('='))
    }

    @Test
    fun a_capital_letter_is_the_same_key_as_a_small_one() {
        // Case is a matter of shift, not of position, and the scancode is positional.
        assertEquals(Scancodes.of('c'), Scancodes.of('C'))
    }

    @Test
    fun every_scancode_is_used_once() {
        val codes = Scancodes.characters.mapNotNull { Scancodes.of(it) }
        assertEquals(codes.size, codes.toSet().size, "Two characters claim the same physical key")
    }

    @Test
    fun a_character_with_no_key_is_refused_rather_than_guessed() {
        assertEquals(null, Scancodes.of('\u00e9'))
        assertEquals(null, Scancodes.of('\u4e2d'))
    }
}
