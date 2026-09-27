package com.sunllo.deskpair.android

import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.input.pointer.AwaitPointerEventScope
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.PointerInputChange
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import com.sunllo.deskpair.Touch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Fingers, forwarded to the shared interpreter.
 *
 * Deliberately thin. Compose's ready-made detectors cannot express this — `detectTapGestures` and
 * `detectDragGestures` mounted side by side fight over the same pointers, which is how the old code ended
 * up firing a tap and a drag for one gesture — so the raw event loop is read directly and every pointer is
 * handed over as-is. What any of it *means* is decided in `TouchInterpreter`, once, for both platforms.
 *
 * One thing is decided here, because only one is the platform's to decide: how quickly a second tap must
 * follow the first. It comes from `viewConfiguration`, which already honours the user's accessibility
 * settings.
 */
fun Modifier.remoteTouch(model: SessionViewModel): Modifier = composed {
    this
        .onSizeChanged { model.viewResized(it.width.toFloat(), it.height.toFloat()) }
        .pointerInput(Unit) {
            val doubleTapTimeout = viewConfiguration.doubleTapTimeoutMillis

            awaitEachGesture {
                val first = awaitFirstDown(requireUnconsumed = false)

                // Whether every finger was seen to lift. If the gesture is torn down before that — the
                // composable leaving, the pointer input being reset — the interpreter is still holding a
                // button down, and so is the host. The `finally` below is the only place that can tell.
                var fingersLifted = false
                var sawSecondFinger = false

                model.onTouch(listOf(first.toTouch()))

                try {
                    while (true) {
                        val event = awaitPointerEvent(PointerEventPass.Main)
                        val active = event.changes.filter { it.pressed }

                        if (active.size >= 2) {
                            sawSecondFinger = true
                        }

                        model.onTouch(active.map { it.toTouch() })
                        event.changes.forEach { it.consume() }

                        if (active.isEmpty()) {
                            fingersLifted = true
                            break
                        }
                    }

                    // The right click used to be recognised here, from a second finger that came and went
                    // without travelling, and the long press was timed here too. Both are gone: a right
                    // click is two fingers now, and it is read from the fingers themselves in
                    // TouchInterpreter, which sees the whole gesture rather than a summary of it and
                    // therefore knows not to fire one at the end of a pinch or a three-finger scroll.
                    //
                    // What is left is the wait below, which is a wait for something that has not happened
                    // yet and could not be moved.

                    // Cleared first: the lift recorded above is stale once a second gesture may begin, and
                    // the wait for it can itself be torn down with a button held.
                    if (!sawSecondFinger) {
                        fingersLifted = false
                        fingersLifted = awaitDoubleTapDrag(model, doubleTapTimeout)
                    }
                } finally {
                    if (!fingersLifted) {
                        model.onTouchCancelled()
                    }
                }
            }
        }
}

/**
 * A second tap that arrives quickly and is then held: the trackpad's drag.
 *
 * Only meaningful in mouse mode, where an ordinary drag moves the pointer without pressing anything — so
 * there has to be some way to say "and hold the button while I do". The interpreter ignores it otherwise.
 *
 * Returns whether the gesture finished with every finger lifted, so the caller knows whether it has to
 * cancel. No second tap at all counts as finished: there is nothing held.
 */
private suspend fun AwaitPointerEventScope.awaitDoubleTapDrag(
    model: SessionViewModel,
    doubleTapTimeout: Long,
): Boolean {
    val second = withTimeoutOrNull(doubleTapTimeout) { awaitFirstDown(requireUnconsumed = false) }
        ?: return true

    model.onDoubleTapDrag(second.position.x, second.position.y)
    model.onTouch(listOf(second.toTouch()))

    while (true) {
        val event = awaitPointerEvent(PointerEventPass.Main)
        val active = event.changes.filter { it.pressed }
        model.onTouch(active.map { it.toTouch() })
        event.changes.forEach { it.consume() }
        if (active.isEmpty()) {
            return true
        }
    }
}

private fun PointerInputChange.toTouch(): Touch =
    Touch(id.value, position.x, position.y)
