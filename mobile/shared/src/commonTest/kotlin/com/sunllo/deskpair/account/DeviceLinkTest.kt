package com.sunllo.deskpair.account

import com.sunllo.deskpair.crypto.Crypto
import com.sunllo.deskpair.store.EphemeralSecretStore
import com.sunllo.deskpair.store.SecretKeys
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue

/**
 * The bytes this app signs when linking to an account, and the key it signs them with.
 *
 * The vector below is the same one `DeviceLinkTests.cs` pins. That is the whole point of it: the portal, the
 * desktop and this app all build the challenge separately, and a disagreement shows up only as the portal
 * saying "this device could not prove it owns its key" — which reads like a key problem and is not one.
 */
class DeviceLinkTest {

    @Test
    fun `the challenge is the same shape the portal builds`() {
        val challenge = DeviceLink.challenge("ABCD2345", "http://portal.test:21120")

        // Written out rather than rebuilt: a test that constructs the string the way the code does proves
        // only that the code is self-consistent, which is not the risk here.
        assertEquals(
            "deskpair-device-link\nABCD2345\nhttp://portal.test:21120",
            challenge.decodeToString(),
        )
    }

    @Test
    fun `the audience drops a trailing slash`() {
        assertContentEquals(
            DeviceLink.challenge("ABCD2345", "http://portal.test:21120"),
            DeviceLink.challenge("ABCD2345", "http://portal.test:21120/"),
        )
    }

    @Test
    fun `a code is read the way a person types it`() {
        for (typed in listOf("abcd2345", "ABCD-2345", " abcd 2345 ", "abcd-2345\n")) {
            assertEquals("ABCD2345", DeviceLink.normalise(typed), typed)
        }
    }

    @Test
    fun `a code is shown in two groups`() {
        assertEquals("ABCD-2345", DeviceLink.format("ABCD2345"))
        // Anything that is not a code is left alone rather than sliced at index four.
        assertEquals("SHORT", DeviceLink.format("SHORT"))
    }

    @Test
    fun `the code alphabet has no ambiguous characters`() {
        assertEquals(DeviceLink.CODE_ALPHABET.length, DeviceLink.CODE_ALPHABET.toSet().size)
        for (c in listOf('0', 'O', '1', 'I')) {
            assertFalse(c in DeviceLink.CODE_ALPHABET, "$c is easy to misread")
        }
    }

    @Test
    fun `a signature this app makes verifies against the key it published`() {
        val identity = DeviceIdentity(EphemeralSecretStore())
        val challenge = DeviceLink.challenge("ABCD2345", "http://portal.test")

        // Signing and verifying are different code paths through the same library; until now the app only
        // ever verified, so this is the first assertion that it can sign at all.
        assertTrue(Crypto.ecdsaVerify(identity.publicKeySpki(), challenge, identity.sign(challenge)))
    }

    @Test
    fun `a signature does not verify for another code or another portal`() {
        val identity = DeviceIdentity(EphemeralSecretStore())
        val signature = identity.sign(DeviceLink.challenge("ABCD2345", "http://portal.one"))
        val pk = identity.publicKeySpki()

        assertFalse(Crypto.ecdsaVerify(pk, DeviceLink.challenge("ABCD2346", "http://portal.one"), signature))
        assertFalse(Crypto.ecdsaVerify(pk, DeviceLink.challenge("ABCD2345", "http://portal.two"), signature))
    }

    @Test
    fun `the identity is generated once and kept`() {
        val secrets = EphemeralSecretStore()
        val first = DeviceIdentity(secrets).publicKeySpki()

        // A second object over the same store is the same device: the key survives the app being restarted,
        // which is what stops every launch showing up in the console as a new machine.
        assertContentEquals(first, DeviceIdentity(secrets).publicKeySpki())
        assertContentEquals(first, DeviceIdentity(secrets).publicKeySpki())
    }

    @Test
    fun `two installs are two devices`() {
        val one = DeviceIdentity(EphemeralSecretStore()).publicKeySpki()
        val two = DeviceIdentity(EphemeralSecretStore()).publicKeySpki()
        assertFalse(one.contentEquals(two))
    }

    @Test
    fun `a damaged stored key is replaced rather than failing forever`() {
        val secrets = EphemeralSecretStore()
        val before = DeviceIdentity(secrets).publicKeySpki()
        secrets.set(SecretKeys.DEVICE_KEY, "not base64 at all")

        // The cost is linking again, which is one code away. The alternative is an install that can never
        // link without its data being cleared.
        val after = DeviceIdentity(secrets).publicKeySpki()
        assertFalse(before.contentEquals(after))

        val challenge = DeviceLink.challenge("ABCD2345", "http://portal.test")
        assertTrue(Crypto.ecdsaVerify(after, challenge, DeviceIdentity(secrets).sign(challenge)))
    }

    @Test
    fun `a portal address is completed the way the desktop completes it`() {
        assertEquals("http://portal.test:21120", PortalClient.normalise("portal.test"))
        assertEquals("http://portal.test:9000", PortalClient.normalise("portal.test:9000"))
        assertEquals("https://portal.test", PortalClient.normalise("https://portal.test/"))
        assertEquals("", PortalClient.normalise("  "))
        assertNotEquals("", PortalClient.normalise(" portal.test "))
    }
}
