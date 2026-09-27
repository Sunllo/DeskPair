package com.sunllo.deskpair

import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.roundToInt

/**
 * How a phone drives a pointer.
 *
 * Two modes, differing in exactly one row of the table, which is what makes the choice explainable at all.
 *
 * [TOUCH] is a touchscreen laid over the desktop: wherever you put your finger is where the pointer goes,
 * and dragging drags. Direct and obvious, and it has one flaw that cannot be designed away — your finger
 * covers the thing you are aiming at.
 *
 * [MOUSE] is a trackpad: the pointer stays where it was and your finger nudges it, so you can see what you
 * are about to click. Precise, and it costs a moment of learning.
 */
public enum class PointerMode { TOUCH, MOUSE }

/** One finger, as the platform reports it. Coordinates are in view points, not host pixels. */
public data class Touch(val id: Long, val x: Float, val y: Float)

/** Something the session or the canvas should do. */
public sealed interface TouchOutcome {

    /** An absolute pointer event, in host pixels. */
    public data class Pointer(
        val action: PointerAction,
        val button: PointerButton,
        val x: Int,
        val y: Int,
    ) : TouchOutcome

    /** A nudge, in host pixels. Mouse mode only; the host applies its own acceleration. */
    public data class RelativePointer(val dx: Int, val dy: Int) : TouchOutcome

    /** Wheel notches; positive is away from the user, which scrolls content up. */
    public data class Scroll(val ticks: Int) : TouchOutcome

    public data class PanCanvas(val dx: Float, val dy: Float) : TouchOutcome

    public data class ZoomCanvas(val factor: Float, val focalX: Float, val focalY: Float) : TouchOutcome
}

/**
 * Turns a stream of fingers into pointer events and canvas movements.
 *
 * This is shared rather than written twice because the two apps had already drifted apart: one released the
 * mouse button at the host's top-left corner after every drag, the other fired a left click *and* a right
 * click for a single long press. Both survived because a gesture's meaning had never been written down
 * anywhere a test could read it.
 *
 * Timing is the platform's job, not this class's. The double tap is delivered as an explicit call because
 * Android and iOS each have a recogniser that already honours the user's accessibility settings, and
 * reimplementing that here would be both more code and less correct. Everything else — how many fingers,
 * what counts as a tap rather than a drag, which mode means what — is decided here.
 */
public class TouchInterpreter(
    /** How far a finger may wander and still count as a tap, in view points. */
    private val slop: Float = 12f,
    /** View points of three-finger travel per wheel notch. */
    private val scrollStep: Float = 16f,
) {

    public var mode: PointerMode = PointerMode.TOUCH

    /**
     * Where the host says its pointer is. Mouse mode needs it to know what a tap will hit, and it is the
     * host's own report rather than a guess — in relative mode the host applies acceleration this side
     * cannot model, so any local estimate drifts within seconds.
     */
    public var cursor: HostPoint = HostPoint(0, 0)

    private enum class Phase { NONE, PENDING, PANNING, DRAGGING, RELATIVE, TWO_FINGER, THREE_FINGER }

    private var phase = Phase.NONE
    private var startX = 0f
    private var startY = 0f
    private var lastX = 0f
    private var lastY = 0f
    private var buttonDown = false

    private var lastPinchDistance = 0f
    private var lastCentroidX = 0f
    private var lastCentroidY = 0f
    private var scrollCarry = 0f

    /**
     * Whether two fingers going down and coming up again would still mean a right click.
     *
     * Set when two fingers arrive on an otherwise idle screen, and cleared the moment they do anything
     * else: travel or a pinch past [slop], a third finger, or two fingers that arrived in the middle of
     * some other gesture. Without the last two, lifting after a three-finger scroll, or after a drag that
     * picked up a second finger, would fire a right click at the end of it.
     */
    private var twoFingerTapPossible = false
    private var twoFingerStartX = 0f
    private var twoFingerStartY = 0f
    private var twoFingerStartDistance = 0f

    /** True while a gesture is being handled, so a UI can suppress its own scrolling. */
    public val isActive: Boolean get() = phase != Phase.NONE

    /**
     * The fingers changed: one went down, one moved, one came up.
     *
     * The platform passes every pointer it currently has, which avoids this class having to track
     * identities across events and gets the finger-count transitions right for free.
     */
    public fun update(pointers: List<Touch>, canvas: CanvasState): List<TouchOutcome> = when {
        pointers.isEmpty() -> finish(canvas)
        pointers.size == 1 -> oneFinger(pointers[0], canvas)
        pointers.size == 2 -> twoFingers(pointers, canvas)
        else -> threeFingers(pointers)
    }

    /**
     * A second tap came down and is being held — the trackpad's drag gesture.
     *
     * Mouse mode only, and that is now a gap rather than a tidiness: it was limited this way because a
     * plain one-finger drag used to drag the host in touch mode, which is no longer true. Allowing it here
     * is how touch mode gets the ability to drag something back, without taking the pan away again.
     */
    public fun doubleTapDragStart(x: Float, y: Float, canvas: CanvasState): List<TouchOutcome> {
        if (mode != PointerMode.MOUSE) {
            return emptyList()
        }

        phase = Phase.DRAGGING
        startX = x
        startY = y
        lastX = x
        lastY = y
        buttonDown = true
        return listOf(TouchOutcome.Pointer(PointerAction.DOWN, PointerButton.LEFT, cursor.x, cursor.y))
    }

    /**
     * The gesture was taken away — a phone call, a system gesture, the app going to the background.
     *
     * A held button must be released or the host is left with the mouse down and no way to know better.
     * This was simply missing before, on both platforms.
     */
    public fun cancel(): List<TouchOutcome> {
        val outcomes = releaseIfHeld()
        reset()
        return outcomes
    }

    private fun oneFinger(touch: Touch, canvas: CanvasState): List<TouchOutcome> {
        when (phase) {
            Phase.NONE -> {
                phase = Phase.PENDING
                startX = touch.x
                startY = touch.y
                lastX = touch.x
                lastY = touch.y
                return emptyList()
            }

            Phase.PENDING -> {
                if (hypot(touch.x - startX, touch.y - startY) < slop) {
                    return emptyList()
                }

                // It is a drag. What that means is the one thing the two modes disagree about.
                //
                // In touch mode it moves the picture, not the host. A phone shows a desktop several times
                // its own width, so looking around is the thing a finger does most, and making the
                // commonest gesture the one that grabs whatever happens to be under it is how a glance
                // turns into a window dragged across somebody's screen.
                //
                // The cost is real and accepted for now: nothing in touch mode holds the left button, so
                // the host cannot be dragged at all -- no moving a window, no selecting text, no sliders.
                // doubleTapDragStart is where that goes when it is wanted, and it already works; it is
                // limited to mouse mode only because this used to make it redundant here.
                return if (mode == PointerMode.TOUCH) {
                    phase = Phase.PANNING
                    val dx = touch.x - lastX
                    val dy = touch.y - lastY
                    lastX = touch.x
                    lastY = touch.y
                    listOf(TouchOutcome.PanCanvas(dx, dy))
                } else {
                    phase = Phase.RELATIVE
                    lastX = touch.x
                    lastY = touch.y
                    emptyList()
                }
            }

            Phase.PANNING -> {
                val dx = touch.x - lastX
                val dy = touch.y - lastY
                lastX = touch.x
                lastY = touch.y
                return if (dx == 0f && dy == 0f) emptyList() else listOf(TouchOutcome.PanCanvas(dx, dy))
            }

            Phase.DRAGGING -> {
                lastX = touch.x
                lastY = touch.y
                val at = canvas.toHost(touch.x, touch.y)
                cursor = at
                // The button travels on the move event: on macOS a plain move with the button already
                // down does not drag, it lurches.
                return listOf(TouchOutcome.Pointer(PointerAction.MOVE, PointerButton.LEFT, at.x, at.y))
            }

            Phase.RELATIVE -> {
                val dx = ((touch.x - lastX) / canvas.scale).roundToInt()
                val dy = ((touch.y - lastY) / canvas.scale).roundToInt()
                if (dx == 0 && dy == 0) {
                    return emptyList()
                }

                lastX = touch.x
                lastY = touch.y
                cursor = HostPoint(
                    (cursor.x + dx).coerceIn(0, maxOf(0, canvas.hostWidth - 1)),
                    (cursor.y + dy).coerceIn(0, maxOf(0, canvas.hostHeight - 1)),
                )
                return listOf(TouchOutcome.RelativePointer(dx, dy))
            }

            // A gesture that became two fingers and came back to one is still that gesture; starting a
            // pan or a drag from whichever finger happened to survive is never what anyone meant. Holding
            // the phase is also what lets a two-finger tap survive fingers leaving one at a time.
            Phase.TWO_FINGER, Phase.THREE_FINGER -> return emptyList()
        }
    }

    private fun twoFingers(pointers: List<Touch>, canvas: CanvasState): List<TouchOutcome> {
        val centroidX = (pointers[0].x + pointers[1].x) / 2f
        val centroidY = (pointers[0].y + pointers[1].y) / 2f
        val distance = hypot(pointers[0].x - pointers[1].x, pointers[0].y - pointers[1].y)

        if (phase != Phase.TWO_FINGER) {
            // A second finger arrived mid-drag. Let go of the button before the picture starts moving, or
            // the host is left dragging a window around while the user is only trying to look elsewhere.
            val released = releaseIfHeld()

            // Only two fingers landing on a screen that was doing nothing else can become a right click.
            twoFingerTapPossible = phase == Phase.NONE || phase == Phase.PENDING
            twoFingerStartX = centroidX
            twoFingerStartY = centroidY
            twoFingerStartDistance = distance

            phase = Phase.TWO_FINGER
            lastPinchDistance = distance
            lastCentroidX = centroidX
            lastCentroidY = centroidY
            return released
        }

        // Moved or pinched: this is the picture being looked around, not a click.
        if (twoFingerTapPossible &&
            (hypot(centroidX - twoFingerStartX, centroidY - twoFingerStartY) > slop ||
                abs(distance - twoFingerStartDistance) > slop)
        ) {
            twoFingerTapPossible = false
        }

        val outcomes = mutableListOf<TouchOutcome>()

        // Pan and zoom together rather than as modes: a pinch that also slides is one gesture to a hand.
        val dx = centroidX - lastCentroidX
        val dy = centroidY - lastCentroidY
        if (dx != 0f || dy != 0f) {
            outcomes += TouchOutcome.PanCanvas(dx, dy)
        }

        if (lastPinchDistance > 0f && distance > 0f) {
            val factor = distance / lastPinchDistance
            if (abs(factor - 1f) > 0.001f) {
                outcomes += TouchOutcome.ZoomCanvas(factor, centroidX, centroidY)
            }
        }

        lastPinchDistance = distance
        lastCentroidX = centroidX
        lastCentroidY = centroidY
        return outcomes
    }

    private fun threeFingers(pointers: List<Touch>): List<TouchOutcome> {
        val centroidY = pointers.map { it.y }.average().toFloat()

        if (phase != Phase.THREE_FINGER) {
            val released = releaseIfHeld()
            twoFingerTapPossible = false
            phase = Phase.THREE_FINGER
            lastCentroidY = centroidY
            scrollCarry = 0f
            return released
        }

        // Integrated rather than sent per pixel: a wheel has notches, and a datagram per pixel of finger
        // travel would flood the session to say very little.
        scrollCarry += centroidY - lastCentroidY
        lastCentroidY = centroidY

        val ticks = (scrollCarry / scrollStep).toInt()
        if (ticks == 0) {
            return emptyList()
        }

        scrollCarry -= ticks * scrollStep
        return listOf(TouchOutcome.Scroll(ticks))
    }

    private fun finish(canvas: CanvasState): List<TouchOutcome> {
        val outcomes = mutableListOf<TouchOutcome>()

        when (phase) {
            // Down and up without wandering: a click, wherever the mode says a click lands.
            Phase.PENDING -> {
                val at = if (mode == PointerMode.TOUCH) canvas.toHost(startX, startY) else cursor
                cursor = at
                if (mode == PointerMode.TOUCH) {
                    outcomes += TouchOutcome.Pointer(PointerAction.MOVE, PointerButton.NONE, at.x, at.y)
                }
                outcomes += TouchOutcome.Pointer(PointerAction.DOWN, PointerButton.LEFT, at.x, at.y)
                outcomes += TouchOutcome.Pointer(PointerAction.UP, PointerButton.LEFT, at.x, at.y)
            }

            // Released where the finger actually was. The Android code used to send literal (0, 0) here,
            // so every drag ended by dropping whatever it held into the host's top-left corner.
            Phase.DRAGGING -> {
                val at = if (mode == PointerMode.TOUCH) canvas.toHost(lastX, lastY) else cursor
                cursor = at
                outcomes += TouchOutcome.Pointer(PointerAction.UP, PointerButton.LEFT, at.x, at.y)
                buttonDown = false
            }

            // Moving the picture is not a click and holds nothing, so lifting the finger ends it and
            // says nothing.
            Phase.PANNING -> Unit

            // Two fingers down and up again without going anywhere: right click.
            //
            // This used to be the platform's job -- a long press on one side, a two-finger tap recogniser
            // on the other, and the latter only in mouse mode. Deciding it here instead is what the rest
            // of this class already does, and it is what makes the staggered lift work: fingers rarely
            // leave the glass together, so the gesture passes through one finger on the way out, and both
            // recognisers had their own answer to that. It also ends two real faults. On iOS two fingers
            // lifting in the same event after a pinch fired a right click; on Android the tap left the
            // interpreter in a consumed state that nothing reset, so the gesture after every right click
            // did nothing at all.
            Phase.TWO_FINGER -> if (twoFingerTapPossible) {
                // Where the fingers were in touch mode; where the pointer already is in mouse mode, which
                // is the whole point of mouse mode.
                val at = if (mode == PointerMode.TOUCH) canvas.toHost(lastCentroidX, lastCentroidY) else cursor
                cursor = at
                if (mode == PointerMode.TOUCH) {
                    outcomes += TouchOutcome.Pointer(PointerAction.MOVE, PointerButton.NONE, at.x, at.y)
                }

                outcomes += TouchOutcome.Pointer(PointerAction.DOWN, PointerButton.RIGHT, at.x, at.y)
                outcomes += TouchOutcome.Pointer(PointerAction.UP, PointerButton.RIGHT, at.x, at.y)
            }

            else -> outcomes += releaseIfHeld()
        }

        reset()
        return outcomes
    }

    private fun releaseIfHeld(): List<TouchOutcome> {
        if (!buttonDown) {
            return emptyList()
        }

        buttonDown = false
        return listOf(TouchOutcome.Pointer(PointerAction.UP, PointerButton.LEFT, cursor.x, cursor.y))
    }

    private fun reset() {
        phase = Phase.NONE
        buttonDown = false
        lastPinchDistance = 0f
        scrollCarry = 0f
        twoFingerTapPossible = false
    }
}
