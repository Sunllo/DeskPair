package com.sunllo.deskpair

import com.sunllo.deskpair.session.ClientCapabilities
import com.sunllo.deskpair.store.AppSettings
import sunllo.messages.BoolOption
import sunllo.messages.ImageQuality
import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * What the settings screen collects, and what actually leaves the phone.
 *
 * Everything here guards a defect rather than a feature. Seven stored settings were edited, shown, and read
 * by nobody, and the one option message the client did send was partial — which on this protocol is not a
 * patch but a replacement, so it quietly undid the rest.
 */
class SessionPreferencesTest {

    @Test
    fun `the stored quality names map to the wire`() {
        assertEquals(Quality.LOW, prefs("low").quality)
        assertEquals(Quality.BALANCED, prefs("balanced").quality)
        assertEquals(Quality.BEST, prefs("best").quality)
        assertEquals(Quality.CUSTOM, prefs("custom").quality)
    }

    @Test
    fun `a settings file saying something else still opens a session`() {
        // Hand-edited, or written by a newer version. A session that refused to start over one unknown
        // word would be a worse failure than one that picks the middle.
        assertEquals(Quality.BALANCED, prefs("ludicrous").quality)
        assertEquals(Quality.BALANCED, prefs("").quality)
    }

    @Test
    fun `the custom numbers only leave the phone in custom mode`() {
        val custom = AppSettings(defaultQuality = "custom", customBitrateKbps = 4000, customFps = 45)
        with(ClientCapabilities(preferences = SessionPreferences.from(custom)).toSessionOptions()) {
            assertEquals(ImageQuality.IQ_CUSTOM, image_quality)
            assertEquals(4000, custom_bitrate_kbps)
            assertEquals(45, custom_fps)
        }

        // The host reads these only in custom mode; sending stale numbers the rest of the time invites a
        // bug later, and the desktop's own BuildOptions zeroes them for the same reason.
        val balanced = AppSettings(defaultQuality = "balanced", customBitrateKbps = 4000, customFps = 45)
        with(ClientCapabilities(preferences = SessionPreferences.from(balanced)).toSessionOptions()) {
            assertEquals(0, custom_bitrate_kbps)
            assertEquals(0, custom_fps)
        }
    }

    @Test
    fun `the cursor and refinement switches reach the wire`() {
        val off = AppSettings(showRemoteCursor = false, losslessRefinement = false)
        with(ClientCapabilities(preferences = SessionPreferences.from(off)).toSessionOptions()) {
            assertEquals(BoolOption.BO_NO, show_remote_cursor)
            assertEquals(BoolOption.BO_NO, lossless_refinement)
        }

        val on = AppSettings(showRemoteCursor = true, losslessRefinement = true)
        with(ClientCapabilities(preferences = SessionPreferences.from(on)).toSessionOptions()) {
            assertEquals(BoolOption.BO_YES, show_remote_cursor)
            assertEquals(BoolOption.BO_YES, lossless_refinement)
        }
    }

    /**
     * The regression this whole file exists for.
     *
     * `OptionsHandler` on the host assigns the incoming options over its stored copy and re-derives the
     * session's permissions from the result, so an omitted field is not left alone — it is reset. Changing
     * the quality used to send three fields and nothing else, which handed the host's audio permission
     * back: `PermissionChanged: PermAudio=True`, followed by the host starting to capture sound the user
     * had declined.
     */
    @Test
    fun `changing the quality does not give back what was declined`() {
        val opened = ClientCapabilities(
            disableAudio = true,
            disableClipboard = true,
            preferences = SessionPreferences.from(AppSettings(defaultQuality = "balanced")),
        )

        val later = opened.copy(preferences = opened.preferences.copy(quality = Quality.LOW))

        with(later.toSessionOptions()) {
            assertEquals(ImageQuality.IQ_LOW, image_quality)
            assertEquals(BoolOption.BO_YES, disable_audio)
            assertEquals(BoolOption.BO_YES, disable_clipboard)
        }
    }

    @Test
    fun `the decoding capabilities survive an options update`() {
        // Declared once at login. The host keeps them across updates on its side too, but only because it
        // special-cases them; nothing else it stores is that lucky.
        val opened = ClientCapabilities(preferences = SessionPreferences.defaults())
        val later = opened.copy(preferences = opened.preferences.copy(quality = Quality.BEST))
        assertEquals(true, later.toSessionOptions().supported_decoding?.h264)
    }

    private fun prefs(quality: String) = SessionPreferences.from(AppSettings(defaultQuality = quality))
}
