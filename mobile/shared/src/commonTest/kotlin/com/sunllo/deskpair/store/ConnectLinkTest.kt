package com.sunllo.deskpair.store

import com.sunllo.deskpair.PROTOCOL_VECTORS_JSON
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertTrue

/**
 * The pairing code, read against strings the desktop's own ConnectLink produced.
 *
 * Two implementations in two languages have to agree on one format, and the way that usually fails is
 * quietly: a percent-encoded colon that comes back wrong, base64url padding restored differently, a field
 * order nobody thought about. Describing the format twice would not catch any of those. The desktop's
 * actual output would.
 */
class ConnectLinkTest {

    private val vectors: JsonObject =
        Json.parseToJsonElement(PROTOCOL_VECTORS_JSON).jsonObject["connectLink"]!!.jsonObject

    @Test
    fun `both sides agree on the version`() {
        // A mismatch here means one of the two will start refusing the other's codes.
        assertEquals(vectors["version"]!!.jsonPrimitive.int, ConnectLink.VERSION)
    }

    @Test
    fun `every link the desktop writes is read back to the same fields`() {
        val cases = vectors["cases"]!!.jsonArray
        assertTrue(cases.size >= 4, "expected the desktop's cases, saw ${cases.size}")

        for (element in cases) {
            val case = element.jsonObject
            val name = case["name"]!!.jsonPrimitive.content
            val scanned = ConnectLink.parse(case["text"]!!.jsonPrimitive.content)

            val understood = assertIs<ConnectLink.Scanned.Understood>(scanned, "case $name")
            val link = understood.link

            assertEquals(case["id"]!!.jsonPrimitive.content, link.id, "id in case $name")
            assertEquals(
                case["rendezvousServer"]!!.jsonPrimitive.content,
                link.rendezvousServer,
                "server in case $name",
            )
            assertEquals(
                case["serverPublicKeyBase64"]!!.jsonPrimitive.content,
                link.serverPublicKeyBase64,
                "key in case $name",
            )
            assertEquals(case["deviceName"]!!.jsonPrimitive.content, link.deviceName, "name in case $name")
            assertEquals(
                case["password"]!!.jsonPrimitive.content,
                link.password,
                "password in case $name",
            )
        }
    }

    /**
     * The one-time password, which is the whole reason a scan can connect without asking.
     *
     * Worth its own case because it is the field most likely to go missing quietly: a phone that drops it
     * still connects, it just asks for a password the user has not got and cannot find, since the one on
     * the desk is a different password.
     */
    @Test
    fun `the one-time password comes through and its absence is not a failure`() {
        val cases = vectors["cases"]!!.jsonArray.map { it.jsonObject }
        val withPassword = cases.first { it["name"]!!.jsonPrimitive.content == "full" }
        val without = cases.first { it["name"]!!.jsonPrimitive.content == "noPassword" }

        val scanned = assertIs<ConnectLink.Scanned.Understood>(
            ConnectLink.parse(withPassword["text"]!!.jsonPrimitive.content),
        ).link
        assertTrue(scanned.password.isNotEmpty())
        assertEquals(withPassword["password"]!!.jsonPrimitive.content, scanned.password)

        // A desk with temporary passwords switched off is not a broken code: it is understood, and the
        // phone asks.
        val silent = assertIs<ConnectLink.Scanned.Understood>(
            ConnectLink.parse(without["text"]!!.jsonPrimitive.content),
        ).link
        assertEquals("", silent.password)
    }

    @Test
    fun `the key comes back as base64 the rest of the app can decode`() {
        val full = vectors["cases"]!!.jsonArray
            .map { it.jsonObject }
            .first { it["name"]!!.jsonPrimitive.content == "full" }

        val link = assertIs<ConnectLink.Scanned.Understood>(
            ConnectLink.parse(full["text"]!!.jsonPrimitive.content),
        ).link

        // The link carries base64url; what comes out has to be what ServerKey.decode expects, or the
        // settings it writes would fail at the next connection rather than here.
        assertTrue(ServerKey.isValid(link.serverPublicKeyBase64))
        assertEquals(91, ServerKey.decode(link.serverPublicKeyBase64)!!.size)
    }

    @Test
    fun `everything the desktop refuses is refused here too`() {
        for (element in vectors["refused"]!!.jsonArray) {
            val text = element.jsonPrimitive.content
            assertIs<ConnectLink.Scanned.Rejected>(ConnectLink.parse(text), "should have been refused: $text")
        }
    }

    @Test
    fun `a newer version says so and says which`() {
        val rejected = assertIs<ConnectLink.Scanned.Rejected>(
            ConnectLink.parse("sunllo://connect?v=2&id=123456789"),
        )
        assertEquals(ConnectLink.Problem.NEWER_VERSION, rejected.problem)
        assertEquals(2, rejected.version)
    }

    @Test
    fun `fields can arrive in any order and unknown ones are ignored`() {
        val link = assertIs<ConnectLink.Scanned.Understood>(
            ConnectLink.parse("sunllo://connect?id=123456789&future=whatever&v=1&rs=example.com%3A21116"),
        ).link

        assertEquals("123456789", link.id)
        assertEquals("example.com:21116", link.rendezvousServer)
    }

    @Test
    fun `a plus in a device name stays a plus`() {
        // This is a URI query, not a form body. Decoding '+' as a space would rename someone's machine.
        val link = assertIs<ConnectLink.Scanned.Understood>(
            ConnectLink.parse("sunllo://connect?v=1&id=123456789&n=desk%2Blab"),
        ).link
        assertEquals("desk+lab", link.deviceName)
    }

    @Test
    fun `the scanned id is something the home screen can dial`() {
        val link = assertIs<ConnectLink.Scanned.Understood>(
            ConnectLink.parse("sunllo://connect?v=1&id=123456789"),
        ).link

        // An id, not an address: if this flipped, a scan would send the phone to a nonexistent host.
        assertTrue(!Targets.isDirect(link.id))
        assertEquals("123 456 789", Targets.formatId(link.id))
    }
}
