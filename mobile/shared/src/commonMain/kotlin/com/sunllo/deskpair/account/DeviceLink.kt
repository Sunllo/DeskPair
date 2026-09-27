package com.sunllo.deskpair.account

/**
 * The exact bytes a device signs to prove it holds its identity key when linking to an account.
 *
 * A transcription of `Protocol/Crypto/DeviceLink.cs`, and the transcription is the risk: three
 * implementations have to agree byte for byte, and a disagreement has exactly one symptom — the portal
 * answers "this device could not prove it owns its key", which reads like a key problem and is not one.
 * `DeviceLinkTest` pins the same vector the C# tests pin, so a drift fails on the build machine instead.
 *
 * What it binds, and why each part is there:
 *
 *  - A fixed prefix, so a signature made for this purpose can never be read as a signature made for
 *    another. The identity key signs nothing else today, but that is a fact about now, not a guarantee.
 *  - The link code, which is single use, short lived and belongs to one account.
 *  - The portal's own base URL, so a signature captured by one portal cannot be replayed at another.
 *
 * There is deliberately no server nonce: it would cost a round trip to buy replay protection the single-use
 * code already gives. None of it survives an active attacker on the wire, who can substitute their own
 * public key and signature — that is what TLS is for, and this deployment does not have it yet.
 */
public object DeviceLink {
    private const val PREFIX = "deskpair-device-link\n"

    /** Characters in a link code: no 0/O and no 1/I/l, so a code read off a screen can be typed. */
    public const val CODE_ALPHABET: String = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"

    public const val CODE_LENGTH: Int = 8

    /** The message to sign. [audience] is the portal's base URL, exactly as the portal is configured with it. */
    public fun challenge(code: String, audience: String): ByteArray =
        (PREFIX + normalise(code) + "\n" + audience.trimEnd('/')).encodeToByteArray()

    /**
     * Codes are compared in one form, so someone typing lower case or leaving the separator in still links.
     * The console prints two groups of four; people type back what they see.
     */
    public fun normalise(code: String): String = buildString(code.length) {
        for (c in code) {
            if (c in 'a'..'z' || c in 'A'..'Z' || c in '0'..'9') {
                append(c.uppercaseChar())
            }
        }
    }

    /** The code as the console shows it: `ABCD-EFGH`. */
    public fun format(code: String): String =
        if (code.length == CODE_LENGTH) code.substring(0, 4) + "-" + code.substring(4) else code
}
