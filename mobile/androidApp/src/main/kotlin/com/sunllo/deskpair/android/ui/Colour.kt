package com.sunllo.deskpair.android.ui

import androidx.compose.material3.ColorScheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.ui.graphics.Color

// The only file in the Android app that names a colour.
//
// DeskPair had no colour of its own here: `MaterialTheme { }` with no arguments is the Material 3 baseline,
// which is purple, so every button, switch and heading in the app was rendering in a colour nobody chose.
// The desktop app has an identity and the logo has an identity; this is both of them, restated in the roles
// Material 3 asks for.
//
// Two sources, no invention:
//
//   * the logo gradient (tools/DeskPair.Tools.IconGen/Program.cs, the authority for the mark):
//     #74D7EA -> #0AA9D1 at .35 -> #418BD6 at .72 -> #003C7E, on a #002559 -> #001839 tile.
//   * the desktop accent, App.axaml's `Button.accent`: #2E6BD6.
//
// #2E6BD6 itself is not used. White text on it falls short of 4.5:1, and it is a fill colour for buttons
// here, so the light scheme takes a darkened sibling and the dark scheme takes a lightened one. The logo's
// cyan supplies the secondary family, which is why "secondary" is a real colour from the mark rather than a
// desaturated copy of the primary.
//
// The dark surfaces are not derived — they are quoted. #12151A is the desktop's window background, #1E2228
// its card, #2C323A its in-card divider, #8A929E its `.label`/`.hint`, #E6E9EF its tray-menu grey. Someone
// running the phone next to the desk should not be able to tell the two greys apart.

/** The mark's own colours, for the brand mark and nothing else. See [BrandMark]. */
internal object Logo {
    val Sky: Color = Color(0xFF74D7EA)
    val Cyan: Color = Color(0xFF0AA9D1)
    val Azure: Color = Color(0xFF418BD6)
    val Navy: Color = Color(0xFF003C7E)

    /** The end stop the navy tile uses, so the diamond does not sink into the background behind it. */
    val NavyLift: Color = Color(0xFF0D5BA6)

    val TileTop: Color = Color(0xFF002559)
    val TileBottom: Color = Color(0xFF001839)
}

internal val LightColours: ColorScheme = lightColorScheme(
    primary = Color(0xFF1F5FC9),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFD8E3FC),
    onPrimaryContainer = Color(0xFF00204E),
    inversePrimary = Color(0xFF7FB0F5),

    secondary = Color(0xFF00687E),
    onSecondary = Color(0xFFFFFFFF),
    secondaryContainer = Color(0xFFB3EBF8),
    onSecondaryContainer = Color(0xFF001F27),

    tertiary = Color(0xFF2C5488),
    onTertiary = Color(0xFFFFFFFF),
    tertiaryContainer = Color(0xFFD4E3FF),
    onTertiaryContainer = Color(0xFF001B3D),

    // A grey with a blue bias rather than a neutral one: against the accent a true grey reads as dirty.
    background = Color(0xFFF2F4F7),
    onBackground = Color(0xFF151A21),
    surface = Color(0xFFF2F4F7),
    onSurface = Color(0xFF151A21),
    surfaceVariant = Color(0xFFE0E5EC),
    onSurfaceVariant = Color(0xFF5C6672),
    surfaceTint = Color(0xFF1F5FC9),
    inverseSurface = Color(0xFF2A2F36),
    inverseOnSurface = Color(0xFFF0F2F5),

    error = Color(0xFFB3261E),
    onError = Color(0xFFFFFFFF),
    errorContainer = Color(0xFFF9DEDC),
    onErrorContainer = Color(0xFF410E0B),

    outline = Color(0xFF8C949F),
    // The hairline between two rows of the same card. Deliberately faint: it separates, it does not divide.
    outlineVariant = Color(0xFFE3E7EE),
    scrim = Color(0xFF000000),

    surfaceDim = Color(0xFFD8DCE3),
    surfaceBright = Color(0xFFFFFFFF),
    // The ground is grey and the cards are white, which is the opposite way round from stock Material 3.
    // That is the whole look of the reference screenshots, and it is why `surface` above is not near-white.
    surfaceContainerLowest = Color(0xFFFFFFFF),
    surfaceContainerLow = Color(0xFFF8FAFC),
    surfaceContainer = Color(0xFFEDF0F4),
    surfaceContainerHigh = Color(0xFFE7EBF0),
    surfaceContainerHighest = Color(0xFFE1E6EC),
)

internal val DarkColours: ColorScheme = darkColorScheme(
    primary = Color(0xFF7FB0F5),
    onPrimary = Color(0xFF08284F),
    primaryContainer = Color(0xFF12427F),
    onPrimaryContainer = Color(0xFFD8E3FC),
    inversePrimary = Color(0xFF1F5FC9),

    secondary = Color(0xFF66D3EC),
    onSecondary = Color(0xFF003641),
    secondaryContainer = Color(0xFF004E5E),
    onSecondaryContainer = Color(0xFFB3EBF8),

    tertiary = Color(0xFFA8C7F5),
    onTertiary = Color(0xFF122F55),
    tertiaryContainer = Color(0xFF2C456D),
    onTertiaryContainer = Color(0xFFD4E3FF),

    background = Color(0xFF12151A),
    onBackground = Color(0xFFE6E9EF),
    surface = Color(0xFF12151A),
    onSurface = Color(0xFFE6E9EF),
    surfaceVariant = Color(0xFF3A424D),
    onSurfaceVariant = Color(0xFF8A929E),
    surfaceTint = Color(0xFF7FB0F5),
    inverseSurface = Color(0xFFE6E9EF),
    inverseOnSurface = Color(0xFF1E2228),

    error = Color(0xFFEE8888),
    onError = Color(0xFF4A1513),
    errorContainer = Color(0xFF6B2320),
    onErrorContainer = Color(0xFFF8D7D5),

    outline = Color(0xFF6B7480),
    outlineVariant = Color(0xFF2C323A),
    scrim = Color(0xFF000000),

    surfaceDim = Color(0xFF0C0F13),
    surfaceBright = Color(0xFF383E46),
    surfaceContainerLowest = Color(0xFF0B0E12),
    surfaceContainerLow = Color(0xFF1A1E24),
    surfaceContainer = Color(0xFF1E2228),
    surfaceContainerHigh = Color(0xFF252A31),
    surfaceContainerHighest = Color(0xFF2C323A),
)
