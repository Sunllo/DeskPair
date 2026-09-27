package com.sunllo.deskpair

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * That every key a host understands can actually be sent.
 *
 * Twenty-seven of eighty were reachable before this: the whole numeric keypad, Backspace, Space, Print
 * Screen, Menu and all seven input-method keys could not be sent at all without a hardware keyboard. That
 * is the kind of gap nobody reports, because the feature looks present — there is a keyboard on screen.
 *
 * The exceptions are listed rather than counted, so adding a key without deciding where it goes fails here
 * instead of quietly joining the unreachable pile.
 */
class KeyGroupsTest {

    /** Keys that deliberately do not appear on the bar, each for a stated reason. */
    private val elsewhere = mapOf(
        // Latching state on the bar, not caps: tap Ctrl, then the next key.
        RemoteKey.SHIFT to "a latching modifier",
        RemoteKey.CONTROL to "a latching modifier",
        RemoteKey.ALT to "a latching modifier",
        RemoteKey.META to "a latching modifier",
        // Its own chip, because the host performs the sequence rather than receiving three keys.
        RemoteKey.CTRL_ALT_DELETE to "its own chip",
        // A command rather than a key, and it lives in the Actions panel with the other commands.
        RemoteKey.LOCK_SCREEN to "the actions panel",
        // Real keys, deliberately withheld: a stray tap on a scrolling row should not be able to put
        // someone else's machine to sleep with no confirmation, because the session ends with it.
        RemoteKey.SLEEP to "too destructive for a stray tap",
        RemoteKey.POWER to "too destructive for a stray tap",
    )

    private val onTheBar: List<RemoteKey>
        get() = (
            KeyGroups.basic + KeyGroups.function + KeyGroups.numpad + KeyGroups.more + KeyGroups.ime
            ).map { it.key }

    @Test
    fun `every key is either on the bar or accounted for`() {
        val reachable = onTheBar.toSet()
        val missing = RemoteKey.entries.filter { it !in reachable && it !in elsewhere }
        assertEquals(emptyList(), missing, "these keys cannot be sent from either app")
    }

    @Test
    fun `no key appears twice`() {
        val caps = onTheBar
        assertEquals(caps.size, caps.toSet().size, "a key on two groups is a key with two labels")
    }

    @Test
    fun `the keys a text field needs are on the first row`() {
        // Backspace is the one that mattered: the typing box only submits whole strings, so without a
        // chip there was no way to delete a character in a remote text field at all.
        val basic = KeyGroups.basic.map { it.key }
        assertTrue(RemoteKey.BACKSPACE in basic)
        assertTrue(RemoteKey.SPACE in basic)
        assertTrue(RemoteKey.RETURN in basic)
    }

    @Test
    fun `the numeric keypad is complete`() {
        val numpad = KeyGroups.numpad.map { it.key }.toSet()
        val digits = listOf(
            RemoteKey.NUMPAD0, RemoteKey.NUMPAD1, RemoteKey.NUMPAD2, RemoteKey.NUMPAD3, RemoteKey.NUMPAD4,
            RemoteKey.NUMPAD5, RemoteKey.NUMPAD6, RemoteKey.NUMPAD7, RemoteKey.NUMPAD8, RemoteKey.NUMPAD9,
        )
        assertTrue(numpad.containsAll(digits))
        assertTrue(numpad.contains(RemoteKey.NUMPAD_ENTER))
        assertTrue(numpad.contains(RemoteKey.NUM_LOCK))
    }

    @Test
    fun `no cap is unlabelled`() {
        val blank = (
            KeyGroups.basic + KeyGroups.function + KeyGroups.numpad + KeyGroups.more + KeyGroups.ime
            ).filter { it.label.isBlank() }
        assertEquals(emptyList(), blank, "a key cap with nothing printed on it is a key nobody will press")
    }
}
