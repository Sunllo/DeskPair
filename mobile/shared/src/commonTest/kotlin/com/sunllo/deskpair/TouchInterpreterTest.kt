package com.sunllo.deskpair

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * What each gesture means, written down where a test can read it.
 *
 * Both of the bugs this replaces were invisible for the same reason: nothing anywhere said what a drag or a
 * right click was supposed to send, so code that sent the wrong thing looked exactly like code that sent
 * the right thing. Every assertion here is a sentence from the gesture table.
 */
class TouchInterpreterTest {

    private val canvas = CanvasState.forHost(
        CanvasState.initial().copy(viewWidth = 400f, viewHeight = 800f),
        1000,
        1000,
    ).fitted()

    private fun interpreter(mode: PointerMode) = TouchInterpreter().apply { this.mode = mode }

    private fun finger(x: Float, y: Float) = listOf(Touch(1L, x, y))

    private fun twoFingers(x1: Float, y1: Float, x2: Float, y2: Float) =
        listOf(Touch(1L, x1, y1), Touch(2L, x2, y2))

    private fun TouchInterpreter.press(x: Float, y: Float) = update(finger(x, y), canvas)

    private fun TouchInterpreter.lift() = update(emptyList(), canvas)

    private fun pointers(outcomes: List<TouchOutcome>) = outcomes.filterIsInstance<TouchOutcome.Pointer>()

    // ---- touch mode ----

    @Test
    fun a_tap_in_touch_mode_moves_to_the_finger_and_clicks_there() {
        val it = interpreter(PointerMode.TOUCH)
        val where = canvas.toHost(120f, 300f)

        assertTrue(it.press(120f, 300f).isEmpty(), "nothing should be sent before the finger lifts")
        val outcomes = pointers(it.lift())

        assertEquals(
            listOf(PointerAction.MOVE, PointerAction.DOWN, PointerAction.UP),
            outcomes.map { o -> o.action },
        )
        assertTrue(outcomes.all { o -> o.x == where.x && o.y == where.y }, "all three at the tapped pixel")
    }

    @Test
    fun one_finger_in_touch_mode_moves_the_picture_and_never_the_host() {
        // A phone shows a desktop several times its own width, so looking around is what a finger does
        // most. Making that gesture grab whatever is under it turns a glance into a window dragged across
        // somebody's screen.
        val it = interpreter(PointerMode.TOUCH)
        it.press(100f, 200f)

        val started = it.press(180f, 260f)
        assertTrue(pointers(started).isEmpty(), "nothing is pressed on the host")
        val first = started.filterIsInstance<TouchOutcome.PanCanvas>().single()
        assertEquals(80f, first.dx)
        assertEquals(60f, first.dy)

        val again = it.press(200f, 270f).filterIsInstance<TouchOutcome.PanCanvas>().single()
        assertEquals(20f, again.dx)
        assertEquals(10f, again.dy)

        // And lifting says nothing at all: there is no button to release and it was not a click.
        assertTrue(it.lift().isEmpty())
    }

    @Test
    fun a_pan_that_never_moved_far_enough_is_still_a_click() {
        // The slop is what separates the two, and a tap must survive a slightly unsteady thumb.
        val it = interpreter(PointerMode.TOUCH)
        it.press(100f, 200f)
        it.press(101f, 201f)

        val end = pointers(it.lift()).map { o -> o.action }
        assertEquals(listOf(PointerAction.MOVE, PointerAction.DOWN, PointerAction.UP), end)
    }

    @Test
    fun a_finger_resting_on_the_screen_still_only_ever_left_clicks() {
        // A held finger used to be the right click. It is two fingers now, and this is the case that
        // proves the old meaning is gone: somebody reading the screen with a thumb on it, for however
        // long, must not open a context menu on the host.
        val it = interpreter(PointerMode.TOUCH)
        it.press(150f, 400f)
        // Named rather than implicit: an unnamed repeat parameter would shadow the interpreter.
        repeat(20) { _ -> assertTrue(it.press(150f, 400f).isEmpty(), "a held finger sends nothing") }

        val outcomes = pointers(it.lift())
        assertEquals(
            listOf(PointerAction.MOVE, PointerAction.DOWN, PointerAction.UP),
            outcomes.map { o -> o.action },
        )
        assertTrue(outcomes.none { o -> o.button == PointerButton.RIGHT }, "and never the right button")
    }

    // ---- mouse mode ----

    @Test
    fun a_drag_in_mouse_mode_nudges_the_pointer_instead_of_dragging_it() {
        val it = interpreter(PointerMode.MOUSE)
        it.cursor = HostPoint(500, 500)

        it.press(100f, 200f)
        val outcomes = it.press(150f, 200f) + it.press(200f, 200f)

        assertTrue(pointers(outcomes).isEmpty(), "mouse mode must not press a button to move")
        val moves = outcomes.filterIsInstance<TouchOutcome.RelativePointer>()
        assertTrue(moves.isNotEmpty(), "the pointer should have been nudged")
        assertTrue(moves.all { m -> m.dy == 0 }, "a horizontal drag should not move vertically")
        assertTrue(moves.sumOf { m -> m.dx } > 0, "and should move to the right")
    }

    @Test
    fun a_tap_in_mouse_mode_clicks_where_the_pointer_already_is() {
        val it = interpreter(PointerMode.MOUSE)
        it.cursor = HostPoint(640, 480)

        it.press(10f, 10f)
        val outcomes = pointers(it.lift())

        // No move: a trackpad tap does not teleport the pointer to the finger.
        assertEquals(listOf(PointerAction.DOWN, PointerAction.UP), outcomes.map { o -> o.action })
        assertTrue(outcomes.all { o -> o.x == 640 && o.y == 480 })
    }

    @Test
    fun double_tap_and_hold_is_how_mouse_mode_drags() {
        val it = interpreter(PointerMode.MOUSE)
        it.cursor = HostPoint(300, 300)

        val down = pointers(it.doubleTapDragStart(100f, 100f, canvas))
        assertEquals(listOf(PointerAction.DOWN), down.map { o -> o.action })
        assertEquals(PointerButton.LEFT, down[0].button)

        val up = pointers(it.lift())
        assertEquals(listOf(PointerAction.UP), up.map { o -> o.action })
    }

    // ---- the right click, which is two fingers in both modes ----

    @Test
    fun two_fingers_tapped_are_a_right_click_where_they_touched() {
        val it = interpreter(PointerMode.TOUCH)
        val where = canvas.toHost(150f, 200f)

        assertTrue(it.update(twoFingers(100f, 200f, 200f, 200f), canvas).isEmpty())
        val outcomes = pointers(it.lift())

        assertEquals(
            listOf(PointerAction.MOVE, PointerAction.DOWN, PointerAction.UP),
            outcomes.map { o -> o.action },
        )
        assertEquals(PointerButton.RIGHT, outcomes[1].button)
        assertTrue(
            outcomes.all { o -> o.x == where.x && o.y == where.y },
            "the click lands between the fingers, not at one of them",
        )
    }

    @Test
    fun two_fingers_tapped_in_mouse_mode_click_where_the_pointer_already_is() {
        val it = interpreter(PointerMode.MOUSE)
        it.cursor = HostPoint(640, 480)

        it.update(twoFingers(10f, 10f, 60f, 10f), canvas)
        val outcomes = pointers(it.lift())

        // No move: a trackpad does not teleport the pointer to the fingers, on either button.
        assertEquals(listOf(PointerAction.DOWN, PointerAction.UP), outcomes.map { o -> o.action })
        assertEquals(PointerButton.RIGHT, outcomes[0].button)
        assertTrue(outcomes.all { o -> o.x == 640 && o.y == 480 })
    }

    @Test
    fun fingers_that_leave_one_at_a_time_are_still_a_tap() {
        // Fingers rarely leave the glass in the same event, which is exactly what both of the platform
        // recognisers this replaces had to guess about.
        val it = interpreter(PointerMode.TOUCH)
        it.update(twoFingers(100f, 200f, 200f, 200f), canvas)
        assertTrue(it.update(listOf(Touch(1L, 100f, 200f)), canvas).isEmpty())

        val outcomes = pointers(it.lift())
        assertEquals(PointerButton.RIGHT, outcomes[1].button)
    }

    @Test
    fun a_pinch_is_not_a_right_click_however_it_ends() {
        val it = interpreter(PointerMode.TOUCH)
        it.update(twoFingers(100f, 200f, 200f, 200f), canvas)
        it.update(twoFingers(60f, 200f, 300f, 200f), canvas)

        assertTrue(pointers(it.lift()).isEmpty(), "looking around must never click anything")
    }

    @Test
    fun a_two_finger_slide_is_not_a_right_click_either() {
        val it = interpreter(PointerMode.TOUCH)
        it.update(twoFingers(100f, 200f, 200f, 200f), canvas)
        it.update(twoFingers(100f, 320f, 200f, 320f), canvas)

        assertTrue(pointers(it.lift()).isEmpty())
    }

    @Test
    fun a_three_finger_scroll_does_not_end_in_a_right_click() {
        // Three fingers come off one at a time, so the gesture passes back through two on the way out.
        val it = interpreter(PointerMode.TOUCH)
        val three = listOf(Touch(1L, 100f, 200f), Touch(2L, 160f, 200f), Touch(3L, 220f, 200f))
        it.update(three, canvas)
        it.update(three.map { f -> f.copy(y = 300f) }, canvas)
        it.update(twoFingers(100f, 300f, 160f, 300f), canvas)

        assertTrue(pointers(it.lift()).isEmpty())
    }

    @Test
    fun the_gesture_after_a_right_click_still_works() {
        // The Android recogniser used to leave the interpreter consumed with nothing left to reset it,
        // so the tap after every right click did nothing at all.
        val it = interpreter(PointerMode.TOUCH)
        it.update(twoFingers(100f, 200f, 200f, 200f), canvas)
        assertEquals(PointerButton.RIGHT, pointers(it.lift())[1].button)

        it.press(120f, 300f)
        val after = pointers(it.lift())
        assertEquals(listOf(PointerAction.MOVE, PointerAction.DOWN, PointerAction.UP), after.map { o -> o.action })
        assertEquals(PointerButton.LEFT, after[1].button)
    }

    // ---- the canvas, which both modes share ----

    @Test
    fun two_fingers_pan_and_pinch_at_once() {
        val it = interpreter(PointerMode.TOUCH)
        val start = listOf(Touch(1L, 100f, 100f), Touch(2L, 200f, 100f))
        assertTrue(it.update(start, canvas).isEmpty(), "the first frame only establishes a baseline")

        // Both fingers move right and apart: a slide and a zoom in one gesture, as a hand does it.
        val next = listOf(Touch(1L, 120f, 100f), Touch(2L, 260f, 100f))
        val outcomes = it.update(next, canvas)

        val pan = outcomes.filterIsInstance<TouchOutcome.PanCanvas>().single()
        val zoom = outcomes.filterIsInstance<TouchOutcome.ZoomCanvas>().single()
        assertEquals(40f, pan.dx, "the centroid moved right by 40")
        assertTrue(zoom.factor > 1f, "the fingers spread, so it zooms in")
    }

    @Test
    fun a_second_finger_lets_go_of_a_drag_before_moving_the_picture() {
        // Mouse mode, where one finger can still be holding the button down. Touch mode has nothing to
        // release, because one finger there moves the picture.
        val it = interpreter(PointerMode.MOUSE)
        it.press(100f, 200f)
        it.press(200f, 300f)
        it.lift()
        it.press(200f, 300f)
        it.doubleTapDragStart(200f, 300f, canvas)

        val outcomes = pointers(it.update(listOf(Touch(1L, 200f, 300f), Touch(2L, 260f, 300f)), canvas))
        assertEquals(listOf(PointerAction.UP), outcomes.map { o -> o.action })
    }

    @Test
    fun three_fingers_scroll_in_notches_rather_than_pixels() {
        val it = interpreter(PointerMode.TOUCH)
        val at = { y: Float -> listOf(Touch(1L, 100f, y), Touch(2L, 150f, y), Touch(3L, 200f, y)) }

        assertTrue(it.update(at(400f), canvas).isEmpty())
        assertTrue(it.update(at(395f), canvas).isEmpty(), "five points is less than one notch")

        val outcomes = it.update(at(370f), canvas).filterIsInstance<TouchOutcome.Scroll>()
        assertEquals(1, outcomes.size)
        assertTrue(outcomes[0].ticks < 0, "dragging up scrolls down")
    }

    @Test
    fun a_cancelled_gesture_releases_the_button() {
        // Mouse mode, because that is where a button can be held now: touch mode's one finger moves the
        // picture and holds nothing.
        val it = interpreter(PointerMode.MOUSE)
        it.press(100f, 200f)
        it.lift()
        it.press(100f, 200f)
        it.doubleTapDragStart(100f, 200f, canvas)

        // A phone call, a system gesture, the app backgrounded. Neither platform handled this, so the
        // host was left with the mouse held down and no way to learn otherwise.
        val outcomes = pointers(it.cancel())
        assertEquals(listOf(PointerAction.UP), outcomes.map { o -> o.action })
        assertTrue(it.cancel().isEmpty(), "cancelling twice must not release twice")
    }

    @Test
    fun a_cancelled_pan_has_nothing_to_release() {
        val it = interpreter(PointerMode.TOUCH)
        it.press(100f, 200f)
        it.press(200f, 300f)

        assertTrue(it.cancel().isEmpty(), "moving the picture never pressed anything on the host")
    }
}
