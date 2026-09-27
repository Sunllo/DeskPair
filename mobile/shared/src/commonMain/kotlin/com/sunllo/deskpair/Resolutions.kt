package com.sunllo.deskpair

import kotlin.math.abs
import kotlin.math.floor
import kotlin.math.roundToInt

/**
 * A size a remote display can be set to: pixels as they arrive in the stream, and the backing scale on a
 * HiDPI mode (2.0 on a Retina Mac; 0 when the host does not say).
 */
public data class RemoteResolution(val width: Int, val height: Int, val scale: Double = 0.0) {

    /**
     * What to show for it. Pixels, or points with the scale on a HiDPI mode: a Retina Mac's 3840×2160 at
     * 2x is what macOS calls 1920×1080 and what the person expects to pick.
     */
    public val label: String
        get() = if (scale > 1.01) {
            "${(width / scale).roundToInt()}×${(height / scale).roundToInt()} (${formatScale(scale)}x)"
        } else {
            "${width}×${height}"
        }

    /** Whether this is the mode [display] is in now. Scale is compared only when both sides know it. */
    public fun matches(display: RemoteDisplay): Boolean =
        width == display.width && height == display.height &&
            (scale == 0.0 || display.scale == 0.0 || abs(scale - display.scale) < 0.01)

    public fun sameAs(other: RemoteResolution): Boolean =
        width == other.width && height == other.height &&
            (scale == 0.0 || other.scale == 0.0 || abs(scale - other.scale) < 0.01)

    internal fun toWire(): sunllo.messages.Resolution =
        sunllo.messages.Resolution(width = width, height = height, scale = scale)

    internal companion object {
        fun fromWire(wire: sunllo.messages.Resolution): RemoteResolution =
            RemoteResolution(wire.width, wire.height, wire.scale)

        private fun formatScale(scale: Double): String =
            if (scale == floor(scale)) scale.toInt().toString() else scale.toString()
    }
}

/**
 * Where a viewer's choice of resolution for a remote display is kept between sessions. The apps back it
 * with their settings file; the session asks it once after login and tells it after every confirmed change.
 */
public interface ResolutionMemory {
    public fun remembered(display: String): RemoteResolution?

    /** Keeps [resolution] for [display], or forgets the display when it is null ("original" is not a choice to reapply). */
    public fun remember(display: String, resolution: RemoteResolution?)
}

/** The choices the resolution picker offers for one display, shared by both phones. */
public object Resolutions {

    /** Which item is lit: 0 for "original", else 1 + the index into [RemoteDisplay.modes]. */
    public fun selectedChoice(display: RemoteDisplay): Int {
        // A display sitting at its original mode is best described as "original", not as one of the list.
        if (display.original?.matches(display) == true) {
            return 0
        }

        val index = display.modes.indexOfFirst { it.matches(display) }
        return if (index < 0) 0 else index + 1
    }

    /**
     * What to ask for after login, given what this viewer chose last time: the remembered mode if the host
     * still offers it and the display is not already there, else nothing.
     */
    public fun reapplyWanted(display: RemoteDisplay, saved: RemoteResolution?): RemoteResolution? {
        if (saved == null || saved.matches(display)) {
            return null
        }

        return display.modes.firstOrNull { it.sameAs(saved) }
    }
}
