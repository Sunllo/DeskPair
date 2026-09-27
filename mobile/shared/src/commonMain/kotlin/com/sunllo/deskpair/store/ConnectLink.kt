package com.sunllo.deskpair.store

/**
 * What a desk puts in a QR code so a phone can reach it.
 *
 * The reading half of `Core/Config/ConnectLink.cs`. A phone is controller-only, so it never writes one —
 * but the two implementations are in different languages and must agree exactly, which is why the strings
 * the desktop produces are committed as conformance vectors and checked here rather than described.
 *
 * The code carries the rendezvous server and its public key as well as the id, because those are the
 * barrier that actually stops people: nobody types a ninety-one byte SPKI with their thumbs, and without
 * it a connection by id verifies nothing.
 *
 * The password it carries is not the one printed under the id on the desk. It is the host's link password,
 * which the first connection to use it spends: the desk issues another and redraws the code, so a
 * photograph of it is worth nothing afterwards. That is what makes a secret in a picture acceptable, and it
 * is why this one is used once and never saved.
 */
public data class ConnectLink(
    val id: String,
    val rendezvousServer: String = "",
    /** Standard base64 SPKI, converted back from the base64url the link carries. */
    val serverPublicKeyBase64: String = "",
    val deviceName: String = "",
    /** The host's one-time link password, or empty when the desk offered none. */
    val password: String = "",
) {
    public companion object {
        /** The only version this build reads. Bumped in lockstep with `ConnectLink.Version` on the desktop. */
        public const val VERSION: Int = 1

        private const val PREFIX = "sunllo://connect?"

        /**
         * Reads a scanned string.
         *
         * Returns [Scanned.Understood] or a [Scanned.Rejected] carrying a reason, rather than null: a
         * scanner that silently does nothing when it reads the wrong thing is a scanner people point at a
         * code over and over wondering why.
         */
        public fun parse(text: String?): Scanned {
            val value = (text ?: "").trim()
            if (!value.startsWith(PREFIX, ignoreCase = true)) {
                return Scanned.Rejected(Problem.NOT_A_DESKPAIR_CODE, 0)
            }

            val fields = parseQuery(value.substring(PREFIX.length))

            val version = fields["v"]?.toIntOrNull()
                ?: return Scanned.Rejected(Problem.NO_VERSION, 0)

            if (version != VERSION) {
                // Deliberately not best effort: a newer build may mean something different by the same
                // field, and guessing is how a phone dials the wrong machine.
                return Scanned.Rejected(Problem.NEWER_VERSION, version)
            }

            val id = fields["id"].orEmpty()
            if (id.isEmpty()) {
                return Scanned.Rejected(Problem.NO_ID, 0)
            }

            val key = fields["k"].orEmpty().let { if (it.isEmpty()) "" else fromBase64Url(it) }
            if (key.isNotEmpty() && !ServerKey.isValid(key)) {
                return Scanned.Rejected(Problem.DAMAGED_KEY, 0)
            }

            return Scanned.Understood(
                ConnectLink(
                    id = id,
                    rendezvousServer = fields["rs"].orEmpty(),
                    serverPublicKeyBase64 = key,
                    deviceName = fields["n"].orEmpty(),
                    password = fields["p"].orEmpty(),
                ),
            )
        }

        /** Unknown keys are ignored rather than refused, so an older build can still read the basics. */
        private fun parseQuery(query: String): Map<String, String> {
            val fields = mutableMapOf<String, String>()
            for (pair in query.split('&')) {
                val equals = pair.indexOf('=')
                if (equals <= 0) {
                    continue
                }

                fields[pair.substring(0, equals).lowercase()] = percentDecode(pair.substring(equals + 1))
            }

            return fields
        }

        /**
         * Percent decoding, over UTF-8.
         *
         * Hand-written because there is no common Kotlin equivalent: `java.net.URLDecoder` is not on
         * Native, and `+` must stay a plus here rather than becoming a space — this is a URI query, not
         * an HTML form body, and a device name with a plus in it is not a device name with a space.
         */
        private fun percentDecode(value: String): String {
            if (!value.contains('%')) {
                return value
            }

            val bytes = ArrayList<Byte>(value.length)
            var i = 0
            while (i < value.length) {
                val c = value[i]
                if (c == '%' && i + 2 < value.length) {
                    val hex = value.substring(i + 1, i + 3).toIntOrNull(16)
                    if (hex != null) {
                        bytes.add(hex.toByte())
                        i += 3
                        continue
                    }
                }

                // Anything else goes through as its own UTF-8 bytes, so a code with a stray character in
                // it still yields a readable name rather than nothing at all.
                c.toString().encodeToByteArray().forEach(bytes::add)
                i++
            }

            return bytes.toByteArray().decodeToString()
        }

        private fun fromBase64Url(value: String): String {
            val standard = value.replace('-', '+').replace('_', '/')
            val padding = (4 - (standard.length % 4)) % 4
            return standard + "=".repeat(padding)
        }
    }

    /** Why a scanned string was not usable. A code rather than a sentence, so the app can translate it. */
    public enum class Problem {
        NOT_A_DESKPAIR_CODE,
        NO_VERSION,
        NEWER_VERSION,
        NO_ID,
        DAMAGED_KEY,
    }

    /** The outcome of a scan. */
    public sealed interface Scanned {
        public data class Understood(val link: ConnectLink) : Scanned

        /** [version] is what the code claimed, for a message that can say which one it needs. */
        public data class Rejected(val problem: Problem, val version: Int) : Scanned
    }
}
