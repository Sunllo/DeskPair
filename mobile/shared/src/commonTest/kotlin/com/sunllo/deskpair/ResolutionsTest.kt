package com.sunllo.deskpair

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** The resolution picker's items and which one is lit, from what the host advertised. */
class ResolutionsTest {

    private fun display(w: Int, h: Int, scale: Double = 0.0, original: RemoteResolution? = null, vararg modes: RemoteResolution) =
        RemoteDisplay(w, h, 0, 0, name = "DISPLAY1", scale = scale, modes = modes.toList(), original = original)

    @Test
    fun `labels are pixels or points with the scale on a HiDPI mode`() {
        assertEquals("1920×1080", RemoteResolution(1920, 1080).label)
        assertEquals("1920×1080 (2x)", RemoteResolution(3840, 2160, 2.0).label)
        assertEquals("2560×1440 (1.5x)", RemoteResolution(3840, 2160, 1.5).label)
    }

    @Test
    fun `the current mode is selected and original leads`() {
        val d = display(1280, 720, modes = arrayOf(RemoteResolution(1920, 1080), RemoteResolution(1280, 720)))
        assertEquals(2, Resolutions.selectedChoice(d))

        val back = display(1920, 1080, original = RemoteResolution(1920, 1080), modes = arrayOf(RemoteResolution(1920, 1080), RemoteResolution(1280, 720)))
        assertEquals(0, Resolutions.selectedChoice(back), "a display at its original size reads as original, not as a list entry")
    }

    @Test
    fun `HiDPI modes with the same pixels are told apart by scale`() {
        val d = display(3840, 2160, scale = 2.0, modes = arrayOf(RemoteResolution(3840, 2160, 2.0), RemoteResolution(3840, 2160, 1.0)))
        assertEquals(1, Resolutions.selectedChoice(d))
    }

    @Test
    fun `a remembered mode is asked for only when offered and not already there`() {
        val d = display(1920, 1080, modes = arrayOf(RemoteResolution(1920, 1080), RemoteResolution(1280, 720)))

        assertNull(Resolutions.reapplyWanted(d, null))
        assertNull(Resolutions.reapplyWanted(d, RemoteResolution(1920, 1080)), "already there")
        assertNull(Resolutions.reapplyWanted(d, RemoteResolution(999, 999)), "no longer offered")
        assertEquals(RemoteResolution(1280, 720), Resolutions.reapplyWanted(d, RemoteResolution(1280, 720)))
    }
}
