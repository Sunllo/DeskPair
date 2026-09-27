package com.sunllo.deskpair

import sunllo.messages.ControlKey

/**
 * The keys a remote desktop client needs to name.
 *
 * This is the protocol's ControlKey set, minus the ones a phone has no way to produce and no reason to. The
 * mapping is exhaustive in the direction that matters — every value here reaches a real ControlKey — and a
 * test walks the protocol's enum in the other direction so a key added to the wire format cannot be quietly
 * forgotten here.
 */
public enum class RemoteKey {
    // Editing
    BACKSPACE, DELETE, INSERT, RETURN, TAB, SPACE, ESCAPE,

    // Navigation
    UP, DOWN, LEFT, RIGHT, HOME, END, PAGE_UP, PAGE_DOWN,

    // Modifiers, as keys in their own right rather than as state
    SHIFT, RIGHT_SHIFT, CONTROL, RIGHT_CONTROL, ALT, RIGHT_ALT, META, RIGHT_META, CAPS_LOCK,

    // Function row
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

    // Numeric keypad
    NUMPAD0, NUMPAD1, NUMPAD2, NUMPAD3, NUMPAD4,
    NUMPAD5, NUMPAD6, NUMPAD7, NUMPAD8, NUMPAD9,
    NUMPAD_ADD, NUMPAD_SUBTRACT, NUMPAD_MULTIPLY, NUMPAD_DIVIDE, NUMPAD_DECIMAL, NUMPAD_ENTER,
    NUM_LOCK,

    // The rest a keyboard actually has
    MENU, APPS, PRINT_SCREEN, SCROLL_LOCK, PAUSE, CLEAR, CANCEL, HELP, SELECT, PRINT, EXECUTE, SEPARATOR,
    EQUALS, SLEEP, POWER,
    VOLUME_UP, VOLUME_DOWN, VOLUME_MUTE,

    // Input-method keys, which matter wherever CJK does
    KANA, HANGUL, JUNJA, FINAL, HANJA, KANJI, CONVERT,

    /** Not a key at all: the host performs the sequence itself, because nothing can send it as three keys. */
    CTRL_ALT_DELETE,

    /** Asks the host to lock its screen. Also a command rather than a key. */
    LOCK_SCREEN,
    ;

    internal fun toWire(): ControlKey = when (this) {
        BACKSPACE -> ControlKey.CK_BACKSPACE
        DELETE -> ControlKey.CK_DELETE
        INSERT -> ControlKey.CK_INSERT
        RETURN -> ControlKey.CK_RETURN
        TAB -> ControlKey.CK_TAB
        SPACE -> ControlKey.CK_SPACE
        ESCAPE -> ControlKey.CK_ESCAPE
        UP -> ControlKey.CK_UP_ARROW
        DOWN -> ControlKey.CK_DOWN_ARROW
        LEFT -> ControlKey.CK_LEFT_ARROW
        RIGHT -> ControlKey.CK_RIGHT_ARROW
        HOME -> ControlKey.CK_HOME
        END -> ControlKey.CK_END
        PAGE_UP -> ControlKey.CK_PAGE_UP
        PAGE_DOWN -> ControlKey.CK_PAGE_DOWN
        SHIFT -> ControlKey.CK_SHIFT
        RIGHT_SHIFT -> ControlKey.CK_RSHIFT
        CONTROL -> ControlKey.CK_CONTROL
        RIGHT_CONTROL -> ControlKey.CK_RCONTROL
        ALT -> ControlKey.CK_ALT
        RIGHT_ALT -> ControlKey.CK_RALT
        META -> ControlKey.CK_META
        RIGHT_META -> ControlKey.CK_RWIN
        CAPS_LOCK -> ControlKey.CK_CAPS_LOCK
        F1 -> ControlKey.CK_F1
        F2 -> ControlKey.CK_F2
        F3 -> ControlKey.CK_F3
        F4 -> ControlKey.CK_F4
        F5 -> ControlKey.CK_F5
        F6 -> ControlKey.CK_F6
        F7 -> ControlKey.CK_F7
        F8 -> ControlKey.CK_F8
        F9 -> ControlKey.CK_F9
        F10 -> ControlKey.CK_F10
        F11 -> ControlKey.CK_F11
        F12 -> ControlKey.CK_F12
        NUMPAD0 -> ControlKey.CK_NUMPAD0
        NUMPAD1 -> ControlKey.CK_NUMPAD1
        NUMPAD2 -> ControlKey.CK_NUMPAD2
        NUMPAD3 -> ControlKey.CK_NUMPAD3
        NUMPAD4 -> ControlKey.CK_NUMPAD4
        NUMPAD5 -> ControlKey.CK_NUMPAD5
        NUMPAD6 -> ControlKey.CK_NUMPAD6
        NUMPAD7 -> ControlKey.CK_NUMPAD7
        NUMPAD8 -> ControlKey.CK_NUMPAD8
        NUMPAD9 -> ControlKey.CK_NUMPAD9
        NUMPAD_ADD -> ControlKey.CK_ADD
        NUMPAD_SUBTRACT -> ControlKey.CK_SUBTRACT
        NUMPAD_MULTIPLY -> ControlKey.CK_MULTIPLY
        NUMPAD_DIVIDE -> ControlKey.CK_DIVIDE
        NUMPAD_DECIMAL -> ControlKey.CK_DECIMAL
        NUMPAD_ENTER -> ControlKey.CK_NUMPAD_ENTER
        NUM_LOCK -> ControlKey.CK_NUM_LOCK
        MENU -> ControlKey.CK_MENU
        APPS -> ControlKey.CK_APPS
        PRINT_SCREEN -> ControlKey.CK_SNAPSHOT
        SCROLL_LOCK -> ControlKey.CK_SCROLL
        PAUSE -> ControlKey.CK_PAUSE
        CLEAR -> ControlKey.CK_CLEAR
        CANCEL -> ControlKey.CK_CANCEL
        HELP -> ControlKey.CK_HELP
        SELECT -> ControlKey.CK_SELECT
        PRINT -> ControlKey.CK_PRINT
        EXECUTE -> ControlKey.CK_EXECUTE
        SEPARATOR -> ControlKey.CK_SEPARATOR
        EQUALS -> ControlKey.CK_EQUALS
        SLEEP -> ControlKey.CK_SLEEP
        POWER -> ControlKey.CK_POWER
        VOLUME_UP -> ControlKey.CK_VOLUME_UP
        VOLUME_DOWN -> ControlKey.CK_VOLUME_DOWN
        VOLUME_MUTE -> ControlKey.CK_VOLUME_MUTE
        KANA -> ControlKey.CK_KANA
        HANGUL -> ControlKey.CK_HANGUL
        JUNJA -> ControlKey.CK_JUNJA
        FINAL -> ControlKey.CK_FINAL
        HANJA -> ControlKey.CK_HANJA
        KANJI -> ControlKey.CK_KANJI
        CONVERT -> ControlKey.CK_CONVERT
        CTRL_ALT_DELETE -> ControlKey.CK_CTRL_ALT_DEL
        LOCK_SCREEN -> ControlKey.CK_LOCK_SCREEN
    }

    public companion object {
        /**
         * Protocol keys this client deliberately does not offer, and why. Listed rather than ignored so the
         * coverage test can tell "decided against" from "forgotten".
         */
        internal val DELIBERATELY_UNMAPPED: Map<ControlKey, String> = mapOf(
            ControlKey.CK_UNKNOWN to "the enum's zero value, which means no key at all",
            ControlKey.CK_OPTION to "an alias for Alt on macOS; ALT already sends it",
        )
    }
}

/** Held modifiers, kept separate from [RemoteKey] because a shortcut is a key plus a state, not four keys. */
public enum class RemoteModifier {
    CONTROL, SHIFT, ALT, META,
    ;

    internal fun toWire(): ControlKey = when (this) {
        CONTROL -> ControlKey.CK_CONTROL
        SHIFT -> ControlKey.CK_SHIFT
        ALT -> ControlKey.CK_ALT
        META -> ControlKey.CK_META
    }
}

/**
 * PC set 1 scancodes for the keys that carry a character.
 *
 * Shortcuts have to travel positionally rather than as text. A host running a Dvorak or AZERTY layout
 * receives "the key in the C position", not the letter C, and reproduces the shortcut its own user would
 * type — whereas sending the character would land on whatever key happens to produce a C there.
 *
 * Only the main block is here. A phone's keyboard has nothing else on it, and the named keys above cover
 * everything a modifier bar offers.
 */
public object Scancodes {

    private val byCharacter: Map<Char, Int> = buildMap {
        // Top row.
        val digits = "1234567890-="
        digits.forEachIndexed { index, c -> put(c, 0x02 + index) }

        // Letter rows, in keyboard order rather than alphabetical: the scancode follows the position.
        "qwertyuiop[]".forEachIndexed { index, c -> put(c, 0x10 + index) }
        "asdfghjkl;'".forEachIndexed { index, c -> put(c, 0x1E + index) }
        "\\zxcvbnm,./".forEachIndexed { index, c -> put(c, 0x2B + index) }

        put('`', 0x29)
    }

    /** The scancode for the key that carries [character] on a US layout, or null if there is none. */
    public fun of(character: Char): Int? = byCharacter[character.lowercaseChar()]

    /** Every character with a positional key, for the coverage test. */
    internal val characters: Set<Char> get() = byCharacter.keys
}

/**
 * Two of these are not keys at all but instructions the host carries out: nothing can assemble
 * Ctrl+Alt+Delete from separate keystrokes on Windows, and locking a screen is not a keystroke anywhere.
 * The host acts on the down edge and ignores a "press", so they are sent differently.
 */
internal fun RemoteKey.isCommand(): Boolean =
    this == RemoteKey.CTRL_ALT_DELETE || this == RemoteKey.LOCK_SCREEN
