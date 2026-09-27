package com.sunllo.deskpair

/**
 * What a host password may be made of: printable ASCII -- letters, digits, the symbols on a US keyboard,
 * and the space.
 *
 * A password is typed on the host in one place and on the viewer in another, and the two places have
 * different keyboards: a phone with a Chinese input method open turns the same key sequence into
 * something the host never saw. Restricting both ends to ASCII is what makes a password portable. The
 * fields apply this as the text changes, so a character an input method slipped in is gone before it can
 * be sent; the desktop does the same in `PasswordText.Ascii`.
 */
public object PasswordText {
    public fun ascii(value: String): String {
        if (value.all { it in ' '..'~' }) {
            return value
        }

        return buildString(value.length) {
            for (c in value) {
                if (c in ' '..'~') {
                    append(c)
                }
            }
        }
    }
}
