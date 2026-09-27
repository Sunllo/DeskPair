package com.sunllo.deskpair

import sunllo.messages.Permission

/**
 * What the host is currently willing to let a session do.
 *
 * The host states all five immediately after login and again whenever one changes
 * (`HostSession.OnPermissionChanged`), and the phone ignored every one of them. That is worse than it
 * sounds: a host that had switched the keyboard off still received keystrokes, and the user still saw a
 * full keyboard and a pointer that did nothing, with no way to tell a refused session from a broken one.
 * The desktop controller has tracked these since it was written.
 *
 * Every field starts true. The host's own answers arrive within milliseconds of login, and starting from
 * "everything is refused" would flash a disabled interface at every user on every connection.
 */
public data class HostPermissions(
    val keyboard: Boolean = true,
    val clipboard: Boolean = true,
    val audio: Boolean = true,
    val file: Boolean = true,
    val restart: Boolean = true,
) {
    /**
     * This set with one permission changed.
     *
     * An unknown value is ignored rather than rejected: a newer host may name a permission this build has
     * never heard of, and refusing the whole message over it would be the wrong way to fail.
     */
    internal fun with(permission: Permission, enabled: Boolean): HostPermissions = when (permission) {
        Permission.PERM_KEYBOARD -> copy(keyboard = enabled)
        Permission.PERM_CLIPBOARD -> copy(clipboard = enabled)
        Permission.PERM_AUDIO -> copy(audio = enabled)
        Permission.PERM_FILE -> copy(file = enabled)
        Permission.PERM_RESTART -> copy(restart = enabled)
        else -> this
    }
}
