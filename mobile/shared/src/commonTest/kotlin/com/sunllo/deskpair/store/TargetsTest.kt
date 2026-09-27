package com.sunllo.deskpair.store

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The same table the desktop asserts in `PeerConnectorTargetTests`, so the two clients cannot quietly come
 * to different conclusions about what the user typed. A disagreement here would show up as "it connects
 * from my laptop but not from my phone", which is an expensive thing to diagnose.
 */
class TargetsTest {

    @Test
    fun `a nine digit id is never an address`() {
        // The trap: 123456789 is a valid integer, and integer-form IPv4 parsing would take it.
        assertFalse(Targets.isDirect("123456789"))
        assertFalse(Targets.isDirect("123456789"))
        assertFalse(Targets.isDirect("1"))
        assertFalse(Targets.isDirect(""))
        assertFalse(Targets.isDirect("   "))
    }

    @Test
    fun `dotted quads are addresses`() {
        assertTrue(Targets.isDirect("192.168.1.5"))
        assertTrue(Targets.isDirect("10.0.2.2"))
        assertTrue(Targets.isDirect("127.0.0.1"))
        assertTrue(Targets.isDirect("255.255.255.255"))
    }

    @Test
    fun `leading zeros and out of range parts are not addresses`() {
        // .NET has rejected leading zeros since 5.0, because 010 reading as octal was a real source of bugs.
        assertFalse(Targets.isDirect("192.168.01.5"))
        assertFalse(Targets.isDirect("256.1.1.1"))
        assertFalse(Targets.isDirect("1.2.3.4.5"))
    }

    @Test
    fun `anything with a port is an address`() {
        assertTrue(Targets.isDirect("192.168.1.5:21118"))
        assertTrue(Targets.isDirect("desk.local:21118"))
        assertTrue(Targets.isDirect("host:1"))
    }

    @Test
    fun `ipv6 literals are addresses`() {
        assertTrue(Targets.isDirect("[fe80::1]"))
        assertTrue(Targets.isDirect("[fe80::1]:21118"))
        assertTrue(Targets.isDirect("fe80::1"))
    }

    @Test
    fun `a bare hostname is not a direct target`() {
        // Matches the C#: only a parseable IPv4 literal counts, so "desk.local" goes to the server.
        assertFalse(Targets.isDirect("desk.local"))
        assertFalse(Targets.isDirect("example.com"))
    }

    @Test
    fun `whitespace around a target does not change what it is`() {
        assertTrue(Targets.isDirect("  192.168.1.5  "))
        assertFalse(Targets.isDirect("  123456789  "))
    }

    @Test
    fun `an address splits into host and default port`() {
        assertEquals("192.168.1.5" to 21118, Targets.splitAddress("192.168.1.5"))
        assertEquals("192.168.1.5" to 5900, Targets.splitAddress("192.168.1.5:5900"))
        assertEquals("fe80::1" to 21118, Targets.splitAddress("[fe80::1]"))
        assertEquals("fe80::1" to 5900, Targets.splitAddress("[fe80::1]:5900"))
        // A bare IPv6 literal has no port to find, and its colons must not be mistaken for one.
        assertEquals("fe80::1" to 21118, Targets.splitAddress("fe80::1"))
    }

    @Test
    fun `a pin key fills in the default port so one machine has one pin`() {
        // Dialling the same desk with and without the port must not produce two entries, or "the key
        // changed" would never fire for the half the user did not happen to use last time.
        assertEquals("192.168.1.5:21118", Targets.pinKey("192.168.1.5"))
        assertEquals("192.168.1.5:21118", Targets.pinKey("192.168.1.5:21118"))
        assertEquals("123456789", Targets.pinKey("123456789"))
    }

    @Test
    fun `nine digits are grouped and everything else is left alone`() {
        assertEquals("123 456 789", Targets.formatId("123456789"))
        assertEquals("12345678", Targets.formatId("12345678"))
        assertEquals("1234567890", Targets.formatId("1234567890"))
        assertEquals("192.168.1.5", Targets.formatId("192.168.1.5"))
    }

    @Test
    fun `the grouped form pastes back in`() {
        // This is the whole point of stripping spaces: the desktop shows "123 456 789" and someone copies it.
        assertEquals("123456789", Targets.normalise(Targets.formatId("123456789")))
        assertEquals("192.168.1.5", Targets.normalise("  192.168.1.5 "))
    }
}
