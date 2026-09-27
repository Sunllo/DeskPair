package com.sunllo.deskpair.android

import android.view.KeyEvent
import com.sunllo.deskpair.RemoteKey
import com.sunllo.deskpair.RemoteModifier
import com.sunllo.deskpair.Scancodes

/**
 * Android key codes, translated for the host.
 *
 * Two paths, and which one a key takes matters. A key that prints a character goes positionally, by
 * scancode, so a shortcut reproduces on whatever layout the host runs — Ctrl+C means "the key where C is on
 * a US board", not "the letter C". Everything else goes by name.
 *
 * Text the user is writing does not come through here at all: the soft keyboard produces it and it is sent
 * as text, because reproducing a character is the host's job and it knows its own layout.
 */
object AndroidKeys {

    private val named: Map<Int, RemoteKey> = mapOf(
        KeyEvent.KEYCODE_DEL to RemoteKey.BACKSPACE,
        KeyEvent.KEYCODE_FORWARD_DEL to RemoteKey.DELETE,
        KeyEvent.KEYCODE_INSERT to RemoteKey.INSERT,
        KeyEvent.KEYCODE_ENTER to RemoteKey.RETURN,
        KeyEvent.KEYCODE_NUMPAD_ENTER to RemoteKey.NUMPAD_ENTER,
        KeyEvent.KEYCODE_TAB to RemoteKey.TAB,
        KeyEvent.KEYCODE_SPACE to RemoteKey.SPACE,
        KeyEvent.KEYCODE_ESCAPE to RemoteKey.ESCAPE,

        KeyEvent.KEYCODE_DPAD_UP to RemoteKey.UP,
        KeyEvent.KEYCODE_DPAD_DOWN to RemoteKey.DOWN,
        KeyEvent.KEYCODE_DPAD_LEFT to RemoteKey.LEFT,
        KeyEvent.KEYCODE_DPAD_RIGHT to RemoteKey.RIGHT,
        KeyEvent.KEYCODE_MOVE_HOME to RemoteKey.HOME,
        KeyEvent.KEYCODE_MOVE_END to RemoteKey.END,
        KeyEvent.KEYCODE_PAGE_UP to RemoteKey.PAGE_UP,
        KeyEvent.KEYCODE_PAGE_DOWN to RemoteKey.PAGE_DOWN,

        KeyEvent.KEYCODE_SHIFT_LEFT to RemoteKey.SHIFT,
        KeyEvent.KEYCODE_SHIFT_RIGHT to RemoteKey.RIGHT_SHIFT,
        KeyEvent.KEYCODE_CTRL_LEFT to RemoteKey.CONTROL,
        KeyEvent.KEYCODE_CTRL_RIGHT to RemoteKey.RIGHT_CONTROL,
        KeyEvent.KEYCODE_ALT_LEFT to RemoteKey.ALT,
        KeyEvent.KEYCODE_ALT_RIGHT to RemoteKey.RIGHT_ALT,
        KeyEvent.KEYCODE_META_LEFT to RemoteKey.META,
        KeyEvent.KEYCODE_META_RIGHT to RemoteKey.RIGHT_META,
        KeyEvent.KEYCODE_CAPS_LOCK to RemoteKey.CAPS_LOCK,

        KeyEvent.KEYCODE_F1 to RemoteKey.F1,
        KeyEvent.KEYCODE_F2 to RemoteKey.F2,
        KeyEvent.KEYCODE_F3 to RemoteKey.F3,
        KeyEvent.KEYCODE_F4 to RemoteKey.F4,
        KeyEvent.KEYCODE_F5 to RemoteKey.F5,
        KeyEvent.KEYCODE_F6 to RemoteKey.F6,
        KeyEvent.KEYCODE_F7 to RemoteKey.F7,
        KeyEvent.KEYCODE_F8 to RemoteKey.F8,
        KeyEvent.KEYCODE_F9 to RemoteKey.F9,
        KeyEvent.KEYCODE_F10 to RemoteKey.F10,
        KeyEvent.KEYCODE_F11 to RemoteKey.F11,
        KeyEvent.KEYCODE_F12 to RemoteKey.F12,

        KeyEvent.KEYCODE_NUMPAD_0 to RemoteKey.NUMPAD0,
        KeyEvent.KEYCODE_NUMPAD_1 to RemoteKey.NUMPAD1,
        KeyEvent.KEYCODE_NUMPAD_2 to RemoteKey.NUMPAD2,
        KeyEvent.KEYCODE_NUMPAD_3 to RemoteKey.NUMPAD3,
        KeyEvent.KEYCODE_NUMPAD_4 to RemoteKey.NUMPAD4,
        KeyEvent.KEYCODE_NUMPAD_5 to RemoteKey.NUMPAD5,
        KeyEvent.KEYCODE_NUMPAD_6 to RemoteKey.NUMPAD6,
        KeyEvent.KEYCODE_NUMPAD_7 to RemoteKey.NUMPAD7,
        KeyEvent.KEYCODE_NUMPAD_8 to RemoteKey.NUMPAD8,
        KeyEvent.KEYCODE_NUMPAD_9 to RemoteKey.NUMPAD9,
        KeyEvent.KEYCODE_NUMPAD_ADD to RemoteKey.NUMPAD_ADD,
        KeyEvent.KEYCODE_NUMPAD_SUBTRACT to RemoteKey.NUMPAD_SUBTRACT,
        KeyEvent.KEYCODE_NUMPAD_MULTIPLY to RemoteKey.NUMPAD_MULTIPLY,
        KeyEvent.KEYCODE_NUMPAD_DIVIDE to RemoteKey.NUMPAD_DIVIDE,
        KeyEvent.KEYCODE_NUMPAD_DOT to RemoteKey.NUMPAD_DECIMAL,
        KeyEvent.KEYCODE_NUM_LOCK to RemoteKey.NUM_LOCK,

        KeyEvent.KEYCODE_MENU to RemoteKey.MENU,
        KeyEvent.KEYCODE_SYSRQ to RemoteKey.PRINT_SCREEN,
        KeyEvent.KEYCODE_SCROLL_LOCK to RemoteKey.SCROLL_LOCK,
        KeyEvent.KEYCODE_BREAK to RemoteKey.PAUSE,
        KeyEvent.KEYCODE_CLEAR to RemoteKey.CLEAR,
        KeyEvent.KEYCODE_HELP to RemoteKey.HELP,
        KeyEvent.KEYCODE_SLEEP to RemoteKey.SLEEP,
        KeyEvent.KEYCODE_POWER to RemoteKey.POWER,
        KeyEvent.KEYCODE_VOLUME_UP to RemoteKey.VOLUME_UP,
        KeyEvent.KEYCODE_VOLUME_DOWN to RemoteKey.VOLUME_DOWN,
        KeyEvent.KEYCODE_VOLUME_MUTE to RemoteKey.VOLUME_MUTE,

        KeyEvent.KEYCODE_KANA to RemoteKey.KANA,
        KeyEvent.KEYCODE_HENKAN to RemoteKey.CONVERT,
    )

    /** The named key this code stands for, or null when it carries a character instead. */
    fun namedKey(keyCode: Int): RemoteKey? = named[keyCode]

    /**
     * The scancode for a key that prints a character.
     *
     * Read from the unmodified label rather than from what the key would produce now: with Shift held,
     * getUnicodeChar returns 'C' for the C key and Ctrl+Shift+C must still be the same physical key.
     */
    fun scancode(event: KeyEvent): Int? {
        val label = event.getUnicodeChar(0)
        if (label == 0) {
            return null
        }
        return Scancodes.of(label.toChar())
    }

    /** Which modifiers Android says are held, as the protocol names them. */
    fun modifiers(event: KeyEvent): Set<RemoteModifier> = buildSet {
        if (event.isCtrlPressed) add(RemoteModifier.CONTROL)
        if (event.isShiftPressed) add(RemoteModifier.SHIFT)
        if (event.isAltPressed) add(RemoteModifier.ALT)
        if (event.isMetaPressed) add(RemoteModifier.META)
    }
}
