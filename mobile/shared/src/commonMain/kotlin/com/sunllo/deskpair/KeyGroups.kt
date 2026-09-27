package com.sunllo.deskpair

/**
 * The keys a key bar offers, beyond the handful that fit on one row.
 *
 * Here rather than in each app because the two bars had already drifted: iOS carried a Ctrl+Alt+Del chip
 * that Android's did not, while Android's file documented one it did not have. Of the eighty values in
 * [RemoteKey], twenty-seven were reachable from either app — the whole numeric keypad, Backspace, Space,
 * Print Screen, Menu and all seven input-method keys could not be sent at all without a hardware keyboard.
 *
 * Backspace is the one that mattered most: there was no way to delete a character in a remote text field,
 * because the typing box only submits whole strings.
 *
 * The caps are not translated. A key is labelled with what is printed on it, and "Esc" is "Esc" in every
 * language this app ships; the group names beside them are translated, because those are words.
 */
public object KeyGroups {

    /** One key as a bar shows it: what is printed on the cap, and what it sends. */
    public data class Cap(val label: String, val key: RemoteKey)

    /** Always on the bar. Ordered by how often a hand reaches for them, not by the enum. */
    public val basic: List<Cap> = listOf(
        Cap("Esc", RemoteKey.ESCAPE),
        Cap("Tab", RemoteKey.TAB),
        Cap("⌫", RemoteKey.BACKSPACE),
        Cap("␣", RemoteKey.SPACE),
        Cap("↵", RemoteKey.RETURN),
        Cap("←", RemoteKey.LEFT),
        Cap("↑", RemoteKey.UP),
        Cap("↓", RemoteKey.DOWN),
        Cap("→", RemoteKey.RIGHT),
        Cap("Del", RemoteKey.DELETE),
        Cap("Home", RemoteKey.HOME),
        Cap("End", RemoteKey.END),
        Cap("PgUp", RemoteKey.PAGE_UP),
        Cap("PgDn", RemoteKey.PAGE_DOWN),
        Cap("Ins", RemoteKey.INSERT),
    )

    public val function: List<Cap> = listOf(
        RemoteKey.F1, RemoteKey.F2, RemoteKey.F3, RemoteKey.F4,
        RemoteKey.F5, RemoteKey.F6, RemoteKey.F7, RemoteKey.F8,
        RemoteKey.F9, RemoteKey.F10, RemoteKey.F11, RemoteKey.F12,
    ).mapIndexed { index, key -> Cap("F${index + 1}", key) }

    /** The keypad, in the order it is laid out rather than the order it is declared. */
    public val numpad: List<Cap> = listOf(
        Cap("Num", RemoteKey.NUM_LOCK),
        Cap("7", RemoteKey.NUMPAD7),
        Cap("8", RemoteKey.NUMPAD8),
        Cap("9", RemoteKey.NUMPAD9),
        Cap("4", RemoteKey.NUMPAD4),
        Cap("5", RemoteKey.NUMPAD5),
        Cap("6", RemoteKey.NUMPAD6),
        Cap("1", RemoteKey.NUMPAD1),
        Cap("2", RemoteKey.NUMPAD2),
        Cap("3", RemoteKey.NUMPAD3),
        Cap("0", RemoteKey.NUMPAD0),
        Cap(".", RemoteKey.NUMPAD_DECIMAL),
        Cap("+", RemoteKey.NUMPAD_ADD),
        Cap("−", RemoteKey.NUMPAD_SUBTRACT),
        Cap("×", RemoteKey.NUMPAD_MULTIPLY),
        Cap("÷", RemoteKey.NUMPAD_DIVIDE),
        Cap("⌤", RemoteKey.NUMPAD_ENTER),
    )

    /**
     * The rest of a real keyboard.
     *
     * Sleep and Power are deliberately left out. They are genuine keys and the protocol carries them, but a
     * stray tap on a scrolling row should not be able to put someone else's machine to sleep with no
     * confirmation and no way back — the session would end with it.
     */
    public val more: List<Cap> = listOf(
        Cap("Caps", RemoteKey.CAPS_LOCK),
        Cap("PrtSc", RemoteKey.PRINT_SCREEN),
        Cap("ScrLk", RemoteKey.SCROLL_LOCK),
        Cap("Pause", RemoteKey.PAUSE),
        Cap("Menu", RemoteKey.MENU),
        Cap("Apps", RemoteKey.APPS),
        Cap("Clear", RemoteKey.CLEAR),
        Cap("Cancel", RemoteKey.CANCEL),
        Cap("Help", RemoteKey.HELP),
        Cap("Select", RemoteKey.SELECT),
        Cap("Print", RemoteKey.PRINT),
        Cap("Exec", RemoteKey.EXECUTE),
        Cap("Sep", RemoteKey.SEPARATOR),
        Cap("=", RemoteKey.EQUALS),
        Cap("Vol+", RemoteKey.VOLUME_UP),
        Cap("Vol−", RemoteKey.VOLUME_DOWN),
        Cap("Mute", RemoteKey.VOLUME_MUTE),
        // The right-hand modifiers, which are keys here rather than latching state: a few Windows layouts
        // and a lot of games tell the two sides apart.
        Cap("RShift", RemoteKey.RIGHT_SHIFT),
        Cap("RCtrl", RemoteKey.RIGHT_CONTROL),
        Cap("RAlt", RemoteKey.RIGHT_ALT),
        Cap("RCmd", RemoteKey.RIGHT_META),
    )

    /**
     * Input-method keys.
     *
     * `Keyboard.kt` has carried the comment "which matter wherever CJK does" since these were mapped, and
     * this app ships a Traditional Chinese translation — so it is a fair bet that the people most likely to
     * need them are already using it.
     */
    public val ime: List<Cap> = listOf(
        Cap("Kana", RemoteKey.KANA),
        Cap("Hangul", RemoteKey.HANGUL),
        Cap("Junja", RemoteKey.JUNJA),
        Cap("Final", RemoteKey.FINAL),
        Cap("Hanja", RemoteKey.HANJA),
        Cap("Kanji", RemoteKey.KANJI),
        Cap("Convert", RemoteKey.CONVERT),
    )
}
