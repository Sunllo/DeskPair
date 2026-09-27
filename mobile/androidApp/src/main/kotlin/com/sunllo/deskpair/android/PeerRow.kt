package com.sunllo.deskpair.android

import android.text.format.DateUtils
import androidx.annotation.DrawableRes
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.foundation.background
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import com.sunllo.deskpair.transport.OnlineState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sunllo.deskpair.PlatformLogo
import com.sunllo.deskpair.PlatformLogos

// The pieces both peer lists need. Favourites and Recent are separate tabs now, so what they share has to
// live somewhere neither of them owns.

/**
 * One machine in a list: a platform icon, what it is called, and what is worth knowing under it.
 *
 * Both lines are held to one line each. The old row let the subtitle wrap, so "1 hour ago" broke across two
 * lines and every row in the list was a different height — which is the single thing that most makes a list
 * look unfinished.
 */
@Composable
internal fun PeerRow(
    title: String,
    subtitle: String,
    platform: String,
    monospacedTitle: Boolean,
    onClick: () -> Unit,
    online: OnlineState = OnlineState.UNKNOWN,
    trailing: @Composable RowScope.() -> Unit,
) {
    // Offline is the one answer that takes the tap away and dims the row. Unknown does not: "the server did
    // not answer" is not "the machine is off".
    val reachable = online != OnlineState.OFFLINE
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(enabled = reachable, onClick = onClick)
            .alpha(if (reachable) 1f else 0.45f)
            .padding(start = 16.dp, end = 8.dp, top = 12.dp, bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // The space is taken whether or not there is a logo to put in it, so an unrecognised peer does not
        // shunt its own name out of the column every other name is in.
        Box(modifier = Modifier.size(24.dp), contentAlignment = Alignment.Center) {
            platformDrawable(platform)?.let { drawable ->
                Icon(
                    painter = painterResource(drawable),
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.size(22.dp),
                )
            }
        }
        Column(modifier = Modifier.weight(1f).padding(start = 16.dp, end = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                // Reachable, not reachable, or not known: a dot the desktop's device list also shows.
                // Grey for unknown rather than red, because "the server did not answer" is not "off".
                val dotColour: Color = when (online) {
                    OnlineState.ONLINE -> Color(0xFF4CAF50)
                    OnlineState.OFFLINE -> Color(0xFF9E9E9E)
                    OnlineState.UNKNOWN -> MaterialTheme.colorScheme.outlineVariant
                }
                val dotLabel = stringResource(
                    when (online) {
                        OnlineState.ONLINE -> R.string.presence_online
                        OnlineState.OFFLINE -> R.string.presence_offline
                        OnlineState.UNKNOWN -> R.string.presence_unknown
                    },
                )
                Box(
                    modifier = Modifier
                        .padding(end = 8.dp)
                        .size(8.dp)
                        .background(dotColour, CircleShape)
                        .semantics { contentDescription = dotLabel },
                )
                Text(
                    text = title,
                    style = MaterialTheme.typography.bodyLarge,
                    fontFamily = if (monospacedTitle) FontFamily.Monospace else null,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            if (subtitle.isNotEmpty()) {
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
        trailing()
    }
}

/**
 * Which machine this is, as a picture.
 *
 * These were Material symbols — a monitor, a terminal, a phone outline — which named the *kind* of machine
 * and left every desktop in the list looking alike. They are now the platforms' own logos, and the drawings
 * are the desktop app's: `tools/platform-icons.py` writes these drawables from the same paths
 * `Assets/PlatformIcons.axaml` holds, so a peer looks the same on the phone as it does on the desk.
 *
 * Which logo a string maps to is decided in the shared module, not here, so Android and iOS cannot drift.
 */
@DrawableRes
private fun platformDrawable(platform: String): Int? = when (PlatformLogos.of(platform)) {
    PlatformLogo.WINDOWS -> R.drawable.ic_platform_windows
    PlatformLogo.MACOS -> R.drawable.ic_platform_macos
    PlatformLogo.IOS -> R.drawable.ic_platform_ios
    PlatformLogo.ANDROID -> R.drawable.ic_platform_android
    PlatformLogo.LINUX -> R.drawable.ic_platform_linux
    PlatformLogo.UBUNTU -> R.drawable.ic_platform_ubuntu
    PlatformLogo.DEBIAN -> R.drawable.ic_platform_debian
    PlatformLogo.REDHAT -> R.drawable.ic_platform_redhat
    null -> null
}

/**
 * Roughly how long ago, in the reader's language.
 *
 * `DateUtils` rather than arithmetic and an English suffix: the iOS side gets this from
 * RelativeDateTimeFormatter, and a list that says "11m ago" in a Chinese interface is the kind of
 * inconsistency nobody reports but everybody notices.
 */
internal fun ago(millis: Long): String {
    if (millis <= 0) {
        return ""
    }

    return DateUtils.getRelativeTimeSpanString(
        millis,
        System.currentTimeMillis(),
        DateUtils.MINUTE_IN_MILLIS,
    ).toString()
}
