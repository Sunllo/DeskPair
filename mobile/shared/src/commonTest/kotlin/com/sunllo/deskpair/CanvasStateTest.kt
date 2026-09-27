package com.sunllo.deskpair

import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The geometry between a finger and a host pixel.
 *
 * Worth testing hard, because every failure here is silent and maddening: the picture looks right and the
 * taps land somewhere else. The old code had exactly one of these — a drag that released the button at the
 * host's top-left corner — and it survived because nothing ever checked where a gesture said it went.
 */
class CanvasStateTest {

    /** A 2560×1440 desktop on a phone-shaped view, which is the case that makes zoom necessary. */
    private fun canvas(viewWidth: Float = 402f, viewHeight: Float = 874f): CanvasState =
        CanvasState.forHost(
            CanvasState.initial().copy(viewWidth = viewWidth, viewHeight = viewHeight),
            2560,
            1440,
        )

    private fun assertClose(expected: Float, actual: Float, tolerance: Float = 0.01f, message: String = "") {
        assertTrue(abs(expected - actual) <= tolerance, "$message expected $expected, was $actual")
    }

    @Test
    fun fitting_shows_the_whole_desktop_and_centres_it() {
        val c = canvas()

        // Width is the binding axis for a landscape desktop on a portrait phone.
        assertClose(402f / 2560f, c.scale, message = "fit scale")
        assertClose(0f, c.x, message = "no horizontal bar when width binds")
        assertClose((874f - 1440f * c.scale) / 2f, c.y, message = "vertical bars share the remainder")
        assertTrue(c.isFitted)

        // The far corner of the desktop maps to the far corner of the drawn picture.
        val corner = c.toView(2559, 1439)
        assertTrue(corner.x <= 402f, "the picture must not extend past the view")
        assertTrue(corner.y <= 874f, "the picture must not extend past the view")
    }

    @Test
    fun a_touch_and_a_host_pixel_are_inverses() {
        val c = canvas().zoomed(3f, 200f, 400f).panned(-50f, 30f)

        for (host in listOf(HostPoint(0, 0), HostPoint(1280, 720), HostPoint(2559, 1439))) {
            val view = c.toView(host.x, host.y)
            val back = c.toHost(view.x, view.y)
            assertEquals(host, back, "round trip through the view lost the pixel")
        }
    }

    @Test
    fun the_focal_point_stays_under_the_same_pixel_while_pinching() {
        val c = canvas()
        val focalX = 150f
        val focalY = 500f
        val before = c.toHost(focalX, focalY)

        // Several steps, as a real pinch arrives: each one must hold the invariant, not just the total.
        var zoomed = c
        for (step in 0 until 5) {
            zoomed = zoomed.zoomed(1.2f, focalX, focalY)
            val after = zoomed.toHost(focalX, focalY)
            assertTrue(
                abs(after.x - before.x) <= 2 && abs(after.y - before.y) <= 2,
                "the pixel under the fingers moved: $before → $after at step $step",
            )
        }
    }

    @Test
    fun zoom_is_clamped_to_the_content_rather_than_to_a_fixed_number() {
        val c = canvas()

        // However hard you pinch out, 150% is always reachable — a 4K desktop must still be magnifiable
        // enough to hit a checkbox.
        var wide = c
        repeat(50) { wide = wide.zoomed(2f, 200f, 400f) }
        assertClose(c.maxScale, wide.scale, message = "zoom in limit")
        assertTrue(wide.scale >= 1.5f, "must always reach at least 150%")

        // And you can always pull back past fitting, so a lost user has somewhere to go.
        var narrow = c
        repeat(50) { narrow = narrow.zoomed(0.5f, 200f, 400f) }
        assertClose(c.fitScale / 1.5f, narrow.scale, message = "zoom out limit")
        assertTrue(narrow.scale < c.fitScale, "must be able to see more than the fit")
    }

    @Test
    fun a_touch_on_the_black_bar_means_the_nearest_real_pixel() {
        val c = canvas()

        // Above the picture and below it: both clamp, rather than producing a coordinate the host rejects
        // or a gesture that silently does nothing.
        assertEquals(0, c.toHost(10f, 0f).y)
        assertEquals(1439, c.toHost(10f, 873f).y)
        assertEquals(0, c.toHost(-100f, 400f).x)
        assertEquals(2559, c.toHost(10_000f, 400f).x)
    }

    @Test
    fun rotating_keeps_a_fitted_canvas_fitted_and_a_zoomed_one_where_it_was() {
        val fitted = canvas().resized(874f, 402f)
        assertTrue(fitted.isFitted, "a fitted canvas should refit to the new shape")
        // Landscape now, and the view is proportionally wider than the desktop, so height is what binds.
        assertClose(402f / 1440f, fitted.scale, message = "refit to the shorter view")

        // Somebody who zoomed into a corner meant it; rotation should not throw that away.
        val zoomed = canvas().zoomed(4f, 200f, 400f)
        val centreBefore = zoomed.toHost(zoomed.viewWidth / 2f, zoomed.viewHeight / 2f)
        val rotated = zoomed.resized(874f, 402f)

        assertClose(zoomed.scale, rotated.scale, message = "scale survives rotation")
        val centreAfter = rotated.toHost(rotated.viewWidth / 2f, rotated.viewHeight / 2f)
        assertTrue(
            abs(centreAfter.x - centreBefore.x) <= 2 && abs(centreAfter.y - centreBefore.y) <= 2,
            "the middle of the view should still be looking at the same thing: $centreBefore → $centreAfter",
        )
    }

    @Test
    fun revealing_moves_as_little_as_it_can_and_not_at_all_when_it_need_not() {
        val c = canvas().zoomed(4f, 200f, 400f)

        val visible = c.toHost(200f, 400f)
        assertEquals(c, c.revealing(visible), "a point already in view should not move the canvas")

        // The cursor has gone off the left edge; it should come just inside, not jump to the middle.
        val offScreen = c.toHost(-500f, 400f)
        val moved = c.revealing(offScreen, margin = 48f)
        val at = moved.toView(offScreen.x, offScreen.y)
        assertClose(48f, at.x, tolerance = 1f, message = "brought to the margin")
        assertTrue(moved.x > c.x, "the canvas moved right to reveal a point off the left")
    }

    @Test
    fun a_display_of_a_different_size_refits_but_the_same_one_does_not_disturb_the_view() {
        val zoomed = canvas().zoomed(3f, 100f, 100f)

        // Switching to a display of the same size mid-gesture must not throw away where the user was.
        assertEquals(zoomed, CanvasState.forHost(zoomed, 2560, 1440))

        // A genuinely different display has nothing to preserve, so it fits.
        val other = CanvasState.forHost(zoomed, 1920, 1080)
        assertTrue(other.isFitted)
        assertEquals(1920, other.hostWidth)
    }

    @Test
    fun a_canvas_with_nothing_in_it_yet_answers_safely() {
        val empty = CanvasState.initial()

        // Frames arrive before the view is measured, and vice versa. Neither order may divide by zero.
        assertEquals(HostPoint(0, 0), empty.toHost(100f, 100f))
        assertEquals(1f, empty.fitScale)
        assertEquals(empty, empty.resized(0f, 0f))
    }
}
