package com.sunllo.deskpair

import com.sunllo.deskpair.crypto.Crypto
import com.sunllo.deskpair.crypto.Handshake
import com.sunllo.deskpair.crypto.Hkdf
import com.sunllo.deskpair.crypto.PasswordProof
import com.sunllo.deskpair.framing.SessionCipher
import com.sunllo.deskpair.framing.hexToBytes
import com.sunllo.deskpair.framing.toHex
import com.sunllo.deskpair.media.RelayDatagram
import com.sunllo.deskpair.session.LEGACY_HELLO_VERSION
import com.sunllo.deskpair.session.MIN_PROTOCOL_VERSION
import com.sunllo.deskpair.session.PROTOCOL_VERSION
import sunllo.messages.PasswordKdf
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The Kotlin protocol checked against values produced by the C# one.
 *
 * These run on every target, which is the point: the JVM answers in seconds while the protocol is being
 * written, and iosSimulatorArm64 answers the question that actually matters — whether Apple's crypto agrees
 * with Microsoft's about what this handshake means.
 *
 * A failure here is not a test being fussy. It means the phone would derive different keys from the same
 * handshake, and the symptom in the field would be a connection that authenticates and then cannot decrypt.
 */
class ProtocolVectorsTest {

    private val vectors: JsonObject =
        Json.parseToJsonElement(PROTOCOL_VECTORS_JSON).jsonObject

    private fun section(name: String): JsonArray = vectors[name]!!.jsonArray

    private fun JsonObject.hex(field: String): ByteArray = this[field]!!.jsonPrimitive.content.hexToBytes()

    private fun JsonObject.str(field: String): String = this[field]!!.jsonPrimitive.content

    @Test
    fun constants_agree_with_the_desktop() {
        val constants = vectors["constants"]!!.jsonObject
        assertEquals(4, constants["frameHeaderBytes"]!!.jsonPrimitive.int)
        assertEquals(SessionCipher.TAG_BYTES, constants["tagBytes"]!!.jsonPrimitive.int)
        assertEquals(SessionCipher.NONCE_BYTES, constants["cipherNonceBytes"]!!.jsonPrimitive.int)
        assertEquals(Handshake.KEY_BYTES, constants["keyBytes"]!!.jsonPrimitive.int)
        assertEquals(Handshake.IV_PREFIX_BYTES, constants["ivPrefixBytes"]!!.jsonPrimitive.int)
        assertEquals(Handshake.NONCE_BYTES, constants["handshakeNonceBytes"]!!.jsonPrimitive.int)
        assertEquals(Crypto.SIGNATURE_BYTES, constants["signatureBytes"]!!.jsonPrimitive.int)
        assertEquals(PROTOCOL_VERSION, constants["protocolVersion"]!!.jsonPrimitive.int)
        assertEquals(MIN_PROTOCOL_VERSION, constants["minProtocolVersion"]!!.jsonPrimitive.int)
        assertEquals(LEGACY_HELLO_VERSION, constants["legacyHelloVersion"]!!.jsonPrimitive.int)
        assertEquals(PasswordProof.MAX_ITERATIONS, constants["pbkdf2MaxIterations"]!!.jsonPrimitive.int)
    }

    @Test
    fun sha256_matches() {
        // Everything else rests on this one, so it is worth proving on its own before anything composite.
        for (entry in section("passwordProof")) {
            val o = entry.jsonObject
            if (o["kdf"]!!.jsonPrimitive.int != PasswordKdf.KDF_SHA256.value) {
                continue // the stretched ones are checked in password_proofs_match
            }
            assertContentEquals(
                o.hex("h1"),
                Crypto.sha256(o.hex("passwordUtf8") + o.hex("salt")),
                "SHA-256 disagrees for password '${o.str("password")}'",
            )
        }
    }

    @Test
    fun an_spki_public_key_is_read_the_same_way() {
        // Not a round trip through our own code: the bytes came from .NET, and a P-256 SPKI is 91 bytes of
        // DER that a reimplementation can mis-parse while still producing something key-shaped.
        for (entry in section("keys")) {
            val o = entry.jsonObject
            val spki = o.hex("spki")
            assertEquals(91, spki.size, "A P-256 SPKI is 91 bytes")
            assertContentEquals(
                o.hex("fingerprintSha256"),
                Crypto.sha256(spki),
                "The pinning fingerprint would differ, so a user comparing keys would see the wrong value",
            )
        }
    }

    @Test
    fun ecdh_yields_the_raw_x_coordinate_unhashed() {
        for (entry in section("ecdh")) {
            val o = entry.jsonObject
            val privateKey = Crypto.ecdhPrivateKeyFromPkcs8(o.hex("privatePkcs8"))
            val secret = Crypto.ecdhRawSecret(privateKey, o.hex("peerSpki"))
            assertEquals(32, secret.size)
            assertContentEquals(
                o.hex("rawSecret"),
                secret,
                "The shared secret is the raw x coordinate and must not be hashed",
            )
        }
    }

    @Test
    fun ecdsa_signatures_are_raw_r_and_s_not_der() {
        var accepted = 0
        var rejected = 0
        for (entry in section("ecdsa")) {
            val o = entry.jsonObject
            val ok = Crypto.ecdsaVerify(o.hex("spki"), o.hex("message"), o.hex("signature"))
            if (o["verifies"]!!.jsonPrimitive.boolean) {
                assertTrue(ok, "A signature the host would produce was rejected")
                accepted++
            } else {
                assertFalse(ok, "A tampered signature was accepted")
                rejected++
            }
        }

        // Guards against a verifier that says yes to everything, and one that says no to everything.
        assertTrue(accepted > 0 && rejected > 0, "The vectors must exercise both outcomes")
    }

    @Test
    fun the_signed_transcript_is_byte_identical() {
        for (entry in section("transcript")) {
            val o = entry.jsonObject
            assertContentEquals(
                o.hex("hash"),
                Handshake.transcript(
                    hostId = o.str("hostId"),
                    identityPk = o.hex("identityPk"),
                    hostEphemeralPk = o.hex("hostEphemeralPk"),
                    hostNonce = o.hex("hostNonce"),
                    controllerEphemeralPk = o.hex("controllerEphemeralPk"),
                    controllerNonce = o.hex("controllerNonce"),
                ),
                "The host signs this hash; a different one means every signature fails",
            )
        }
    }

    @Test
    fun all_eight_derived_keys_match() {
        for (entry in section("hkdf")) {
            val o = entry.jsonObject
            val keys = Handshake.derive(
                ownPrivateKey = Crypto.ecdhPrivateKeyFromPkcs8(o.hex("controllerPrivatePkcs8")),
                peerEphemeralSpki = o.hex("hostEphemeralSpki"),
                controllerNonce = o.hex("controllerNonce"),
                hostNonce = o.hex("hostNonce"),
            )

            assertContentEquals(o.hex("txKey"), keys.txKey, "tx key")
            assertContentEquals(o.hex("txIvPrefix"), keys.txIvPrefix, "tx nonce prefix")
            assertContentEquals(o.hex("rxKey"), keys.rxKey, "rx key")
            assertContentEquals(o.hex("rxIvPrefix"), keys.rxIvPrefix, "rx nonce prefix")
            // Derived even though phase 1 declines UDP: a mistake here would only surface much later.
            assertContentEquals(o.hex("mediaTxKey"), keys.mediaTxKey, "media tx key")
            assertContentEquals(o.hex("mediaTxIvPrefix"), keys.mediaTxIvPrefix, "media tx nonce prefix")
            assertContentEquals(o.hex("mediaRxKey"), keys.mediaRxKey, "media rx key")
            assertContentEquals(o.hex("mediaRxIvPrefix"), keys.mediaRxIvPrefix, "media rx nonce prefix")
        }
    }

    @Test
    fun hkdf_expand_spans_more_than_one_block() {
        // The vectors only need 32 and 4 bytes, both under one HMAC block, so the counter loop is never
        // exercised by them. RFC 5869 A.1 covers 42 bytes, which is two.
        val prk = Hkdf.extract(
            salt = "000102030405060708090a0b0c".hexToBytes(),
            inputKeyMaterial = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b".hexToBytes(),
        )
        assertEquals("077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5", prk.toHex())

        val okm = Hkdf.expand(prk, "f0f1f2f3f4f5f6f7f8f9".hexToBytes(), 42)
        assertEquals(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            okm.toHex(),
        )
    }

    @Test
    fun the_cipher_counts_from_one_and_binds_the_header() {
        for (entry in section("cipher")) {
            val o = entry.jsonObject
            val cipher = SessionCipher(o.hex("key"), o.hex("ivPrefix"))

            for (frameElement in o["frames"]!!.jsonArray) {
                val frame = frameElement.jsonObject
                val header = frame.hex("header")
                val plaintext = frame.hex("plaintext")
                val expected = frame.hex("ciphertext") + frame.hex("tag")

                val sealed = cipher.seal(plaintext, header)

                assertEquals(
                    frame["counter"]!!.jsonPrimitive.int.toULong(),
                    cipher.lastCounter,
                    "The counter must start at 1 and advance once per encrypted frame",
                )
                assertContentEquals(expected, sealed, "Ciphertext or tag differs from the desktop's")
            }
        }
    }

    @Test
    fun a_tampered_frame_does_not_open() {
        val entry = section("cipher").first().jsonObject
        val frame = entry["frames"]!!.jsonArray.first().jsonObject
        val header = frame.hex("header")
        val sealed = frame.hex("ciphertext") + frame.hex("tag")

        // Fresh ciphers so both sides start at counter 1, as two ends of a session do.
        assertContentEquals(
            frame.hex("plaintext"),
            SessionCipher(entry.hex("key"), entry.hex("ivPrefix")).open(sealed, header),
        )

        val wrongHeader = header.copyOf().also { it[0] = (it[0].toInt() + 1).toByte() }
        assertEquals(
            null,
            SessionCipher(entry.hex("key"), entry.hex("ivPrefix")).open(sealed, wrongHeader),
            "The 4-byte header is the associated data; changing it must fail the tag",
        )

        val tampered = sealed.copyOf().also { it[0] = (it[0].toInt() xor 0x01).toByte() }
        assertEquals(
            null,
            SessionCipher(entry.hex("key"), entry.hex("ivPrefix")).open(tampered, header),
            "A flipped ciphertext bit must fail the tag",
        )
    }

    @Test
    fun password_proofs_match() {
        for (entry in section("passwordProof")) {
            val o = entry.jsonObject
            val kdf = PasswordKdf.fromValue(o["kdf"]!!.jsonPrimitive.int) ?: error("unknown kdf in vectors")
            val iterations = o["iterations"]!!.jsonPrimitive.int
            assertContentEquals(
                o.hex("h1"),
                PasswordProof.computeH1(o.str("password"), o.hex("salt"), kdf, iterations),
                "h1 disagrees for password '${o.str("password")}' under $kdf",
            )
            assertContentEquals(
                o.hex("proof"),
                PasswordProof.computeProof(o.str("password"), o.hex("salt"), o.hex("challenge"), kdf, iterations),
                "The host would reject this login, for password '${o.str("password")}' under $kdf",
            )
        }
    }

    @Test
    fun pbkdf2_fallback_agrees_with_the_backend() {
        // The pure-Kotlin path is what a platform without a PBKDF2 backend gets; it must be the same
        // function, so it is checked against the desktop's vectors directly rather than trusted.
        for (entry in section("passwordProof")) {
            val o = entry.jsonObject
            if (o["kdf"]!!.jsonPrimitive.int != PasswordKdf.KDF_PBKDF2_SHA256.value) {
                continue
            }
            assertContentEquals(
                o.hex("h1"),
                Crypto.pbkdf2Sha256(o.hex("passwordUtf8"), o.hex("salt"), o["iterations"]!!.jsonPrimitive.int, 32),
            )
        }
    }

    @Test
    fun relay_ticket_bind_datagram_matches() {
        // The phone never verifies a ticket -- the relay does -- but it does put one on the wire, after the
        // token the host derived from it, and a byte out of place is a bind the relay drops without a word.
        val section = vectors["relayTicket"]!!.jsonObject
        val ticket = section["ticket"]!!.jsonObject
        val encoded = ticket.hex("encoded")
        val decoded = sunllo.rendezvous.RelayTicket.ADAPTER.decode(encoded)
        assertContentEquals(ticket.hex("payload"), decoded.payload.toByteArray())
        assertContentEquals(ticket.hex("signature"), decoded.signature.toByteArray())
        assertContentEquals(encoded, decoded.encode(), "Wire must re-encode the ticket byte for byte")

        val token = section.hex("udpToken")
        assertContentEquals(
            section.hex("bindDatagram"),
            RelayDatagram.writeBindTicket(token, encoded),
        )
        assertContentEquals(
            token,
            Crypto.sha256("sunllo/relay/udp".encodeToByteArray() + ticket.hex("payload")).copyOf(RelayDatagram.TOKEN_BYTES),
            "the token is the first half of SHA-256(label || payload)",
        )
    }

    @Test
    fun secure_randomness_is_available_and_not_constant() {
        // Not a statistical test. It catches a platform where the call silently returns zeroes, which would
        // repeat the AES-GCM nonce and break the cipher outright.
        val a = Crypto.randomBytes(32)
        val b = Crypto.randomBytes(32)
        assertEquals(32, a.size)
        assertFalse(a.all { it == 0.toByte() }, "Randomness returned all zeroes")
        assertFalse(a.contentEquals(b), "Two draws returned the same bytes")
    }

    @Test
    fun a_generated_key_pair_round_trips_through_a_shared_secret() {
        // Proves the generator and the SPKI encoder agree with the decoder, which the vectors cannot: they
        // only ever feed in keys made elsewhere.
        val controller = Crypto.generateEcdhKeyPair()
        val host = Crypto.generateEcdhKeyPair()

        assertEquals(91, controller.publicKeySpki.size)
        assertContentEquals(
            Crypto.ecdhRawSecret(controller.privateKey, host.publicKeySpki),
            Crypto.ecdhRawSecret(host.privateKey, controller.publicKeySpki),
            "Both sides of a fresh agreement must reach the same secret",
        )
    }
}
