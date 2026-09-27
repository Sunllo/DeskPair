package com.sunllo.deskpair.android.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color

/**
 * The app's colours, following the system between light and dark.
 *
 * Dynamic colour is deliberately not used. It would paint DeskPair in whatever the wallpaper happens to be,
 * and this app is one end of a pair — the thing on screen is someone else's desk, and the chrome around it
 * should look the same as the application running over there.
 */
@Composable
fun DeskPairTheme(dark: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    CompositionLocalProvider(LocalDarkTheme provides dark) {
        MaterialTheme(colorScheme = if (dark) DarkColours else LightColours, content = content)
    }
}

/**
 * Whether the dark scheme is in force.
 *
 * Recorded by [DeskPairTheme] rather than re-read from the system, so that a preview or a screenshot run can
 * ask for one scheme and have everything agree — including [card], which is the one colour whose meaning
 * depends on which way round the surfaces go.
 */
internal val LocalDarkTheme = staticCompositionLocalOf { false }

/**
 * The surface a card sits on: white above the grey ground in light, a step up from near-black in dark.
 *
 * This cannot be a single Material role. Material's container ramp runs *away* from `surface` in opposite
 * directions in the two schemes — `surfaceContainerLowest` is the lightest colour there is in light and the
 * darkest in dark — so a card pinned to either role would be invisible in one of the two.
 */
internal val ColorScheme.card: Color
    @Composable
    @ReadOnlyComposable
    get() = if (LocalDarkTheme.current) surfaceContainer else surfaceContainerLowest
