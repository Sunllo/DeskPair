package com.sunllo.deskpair.store

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class AppSettingsTest {

    @Test
    fun `a recent connection goes to the front and is not duplicated`() {
        val settings = AppSettings()
            .withRecent("111111111", nowMs = 1)
            .withRecent("222222222", nowMs = 2)
            .withRecent("111111111", nowMs = 3)

        assertEquals(listOf("111111111", "222222222"), settings.recent.map { it.id })
        assertEquals(3, settings.recent.first().lastConnectedMs)
    }

    @Test
    fun `the recent list is clamped whatever the file said`() {
        val overflowing = AppSettings(recentLimit = 3)
        val filled = (1..10).fold(overflowing) { acc, i -> acc.withRecent("id$i", nowMs = i.toLong()) }
        assertEquals(3, filled.recent.size)
        assertEquals(listOf("id10", "id9", "id8"), filled.recent.map { it.id })

        // A hand-edited file saying 0, or 9999, must not produce an empty or unbounded list.
        val zero = AppSettings(recentLimit = 0).withRecent("a", nowMs = 1).withRecent("b", nowMs = 2)
        assertEquals(listOf("b"), zero.recent.map { it.id })
    }

    @Test
    fun `a peer that reports no platform keeps the one it reported last time`() {
        // Otherwise the logo in the list appears and disappears between connections.
        val settings = AppSettings()
            .withRecent("111111111", platform = "Windows", nowMs = 1)
            .withRecent("111111111", platform = "", nowMs = 2)

        assertEquals("Windows", settings.recent.single().platform)
    }

    @Test
    fun `a peer that reports a new platform replaces the old one`() {
        val settings = AppSettings()
            .withRecent("111111111", platform = "Windows", nowMs = 1)
            .withRecent("111111111", platform = "macOS", nowMs = 2)

        assertEquals("macOS", settings.recent.single().platform)
    }

    @Test
    fun `a favourite is added once and can be removed`() {
        val once = AppSettings().withFavourite(Favourite("123456789", alias = "Office"))
        val twice = once.withFavourite(Favourite("123456789", alias = "Office again"))

        assertEquals(1, twice.favourites.size)
        assertEquals("Office", twice.favourites.single().alias)
        assertTrue(twice.isFavourite("123456789"))
        assertFalse(twice.withoutFavourite("123456789").isFavourite("123456789"))
    }

    @Test
    fun `an address favourite matches regardless of case`() {
        val settings = AppSettings().withFavourite(Favourite("Desk.Local:21118"))
        assertTrue(settings.isFavourite("desk.local:21118"))
    }

    @Test
    fun `a favourite shows its alias or the grouped id when it has none`() {
        assertEquals("Office", Favourite("123456789", alias = "Office").title)
        assertEquals("123 456 789", Favourite("123456789").title)
        assertEquals("192.168.1.5", Favourite("192.168.1.5").title)
    }

    @Test
    fun `a file written before a setting existed keeps that setting's default`() {
        // The hazard HostConfig.cs documents: reading a missing flag as false silently withdraws it.
        // Here that would turn UDP media and lossless refinement off for everyone who upgrades.
        val old = """{"rendezvousServer":"rv.example.com","language":"zh-TW"}"""
        val settings = SettingsStore(EphemeralSettingsStorage(old)).current

        assertEquals("rv.example.com", settings.rendezvousServer)
        assertEquals("zh-TW", settings.language)
        assertTrue(settings.udpMedia)
        assertTrue(settings.losslessRefinement)
        assertEquals(20, settings.recentLimit)
        assertEquals("balanced", settings.defaultQuality)
    }

    @Test
    fun `a file from a newer build is read rather than rejected`() {
        val newer = """{"rendezvousServer":"rv.example.com","somethingAddedLater":42}"""
        assertEquals("rv.example.com", SettingsStore(EphemeralSettingsStorage(newer)).current.rendezvousServer)
    }

    @Test
    fun `a corrupt file does not stop the app starting`() {
        assertEquals(AppSettings(), SettingsStore(EphemeralSettingsStorage("{ this is not json")).current)
        assertEquals(AppSettings(), SettingsStore(EphemeralSettingsStorage(null)).current)
    }

    @Test
    fun `flush writes what update was holding`() {
        val storage = EphemeralSettingsStorage()
        val store = SettingsStore(storage)

        store.update { it.copy(rendezvousServer = "rv.example.com") }
        store.flush()

        assertEquals("rv.example.com", SettingsStore(EphemeralSettingsStorage(storage.read())).current.rendezvousServer)
    }

    @Test
    fun `settings that are still at their default are not written out`() {
        val storage = EphemeralSettingsStorage()
        SettingsStore(storage).apply { update { it.copy(rendezvousServer = "rv") } }.flush()

        val written = storage.read()!!
        assertTrue(written.contains("rendezvousServer"))
        assertFalse(written.contains("recentLimit"), "defaults should stay out of the file: $written")
    }
}

class SecretStoreTest {

    @Test
    fun `pins and passwords are kept apart`() {
        val secrets = EphemeralSecretStore()
        val pins = PersistentPinnedKeyStore(secrets)
        val passwords = RememberedPasswords(secrets)

        pins.pin("192.168.1.5:21118", "abcdef")
        passwords.remember("192.168.1.5:21118", "hunter2")

        assertEquals("abcdef", pins.pinnedFingerprint("192.168.1.5:21118"))
        assertEquals("hunter2", passwords.get("192.168.1.5:21118"))
        assertEquals(listOf("192.168.1.5:21118"), pins.all().map { it.hostId })
        assertEquals(listOf("192.168.1.5:21118"), passwords.targets())
    }

    @Test
    fun `forgetting a pin leaves the password alone and the other way round`() {
        val secrets = EphemeralSecretStore()
        val pins = PersistentPinnedKeyStore(secrets)
        val passwords = RememberedPasswords(secrets)

        pins.pin("a:1", "f1")
        passwords.remember("a:1", "p1")

        pins.forget("a:1")
        assertNull(pins.pinnedFingerprint("a:1"))
        assertEquals("p1", passwords.get("a:1"))

        passwords.forgetAll()
        assertNull(passwords.get("a:1"))
    }

    @Test
    fun `an empty password forgets rather than storing nothing`() {
        val passwords = RememberedPasswords(EphemeralSecretStore())
        passwords.remember("a:1", "p1")
        passwords.remember("a:1", "")
        assertNull(passwords.get("a:1"))
    }

    @Test
    fun `a short fingerprint is the first eight bytes as the desktop prints them`() {
        val pins = PersistentPinnedKeyStore(EphemeralSecretStore())
        pins.pin("a:1", "0123456789abcdef0123456789abcdef")
        assertEquals("0123456789ABCDEF", pins.all().single().shortFingerprint)
    }

    @Test
    fun `a key that could escape its store is refused`() {
        val refused = listOf("../etc/passwd", "a/b", "a\\b", "", "a..b")
        for (bad in refused) {
            val threw = try {
                SecretKeys.pin(bad)
                false
            } catch (_: IllegalArgumentException) {
                true
            }
            assertTrue(threw, "should have been refused: $bad")
        }

        // A normalised host:port is the common case and must be allowed through.
        assertEquals("pin:192.168.1.5:21118", SecretKeys.pin("192.168.1.5:21118"))
        assertEquals("pin:123456789", SecretKeys.pin("123456789"))
    }
}

class ServerKeyTest {

    @Test
    fun `paste damage is tolerated the way PeerSettings tolerates it`() {
        val spki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE" + "A".repeat(52) + "=="
        assertEquals(spki, ServerKey.clean(spki))
        assertEquals(spki, ServerKey.clean("  \"$spki\"  "))
        assertEquals(spki, ServerKey.clean("key: $spki"))
        assertEquals(spki, ServerKey.clean("`$spki`"))
        assertEquals(spki, ServerKey.clean(spki.chunked(20).joinToString("\n")))
    }

    @Test
    fun `a colon inside the key itself is not mistaken for a prefix`() {
        // The C# only looks at the first twelve characters and only when nothing before the colon is
        // base64 punctuation, so a long key that happens to contain one keeps all of it.
        val looksLikeAPrefix = "abcdefghijklmnop:qrst"
        assertEquals(looksLikeAPrefix, ServerKey.clean(looksLikeAPrefix))
    }

    @Test
    fun `an empty key decodes to nothing rather than failing`() {
        assertNull(ServerKey.decode(""))
        assertNull(ServerKey.decode("   "))
    }

    @Test
    fun `a key that is not base64 is refused with something actionable`() {
        assertFalse(ServerKey.isValid("this is not base64 !!!"))
        val message = try {
            ServerKey.decode("!!!not base64!!!")
            ""
        } catch (e: IllegalArgumentException) {
            e.message.orEmpty()
        }
        assertTrue(message.contains("21114"), "the message should say where to get a real one: $message")
    }

    @Test
    fun `a chosen resolution is remembered per display and forgotten on original`() {
        val settings = AppSettings()
            .withPeerResolution("123456789", "DISPLAY1", PeerResolution("", "", 1920, 1080))
            .withPeerResolution("123456789", "DISPLAY2", PeerResolution("", "", 1280, 720, 2.0))

        assertEquals(PeerResolution("123456789", "DISPLAY1", 1920, 1080), settings.resolutionFor("123456789", "DISPLAY1"))
        assertEquals(2.0, settings.resolutionFor("123456789", "DISPLAY2")?.scale)
        assertNull(settings.resolutionFor("987654321", "DISPLAY1"))

        val replaced = settings.withPeerResolution("123456789", "DISPLAY1", PeerResolution("", "", 1600, 900))
        assertEquals(1, replaced.peerResolutions.count { it.display == "DISPLAY1" })
        assertEquals(1600, replaced.resolutionFor("123456789", "DISPLAY1")?.width)

        val forgotten = replaced.withPeerResolution("123456789", "DISPLAY1", null)
        assertNull(forgotten.resolutionFor("123456789", "DISPLAY1"))
        assertEquals(1280, forgotten.resolutionFor("123456789", "DISPLAY2")?.width)
    }
}
