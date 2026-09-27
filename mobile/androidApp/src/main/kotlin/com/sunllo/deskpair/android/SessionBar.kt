package com.sunllo.deskpair.android

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.outlined.Keyboard
import androidx.compose.material.icons.outlined.Layers
import androidx.compose.material.icons.outlined.Monitor
import androidx.compose.material.icons.outlined.Mouse
import androidx.compose.material.icons.outlined.SignalCellularAlt
import androidx.compose.material.icons.outlined.SignalCellularAlt1Bar
import androidx.compose.material.icons.outlined.SignalCellularAlt2Bar
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.sunllo.deskpair.android.ui.card

/**
 * Which panel of the session bar is open, if any.
 *
 * The session used to carry a row of six identical text buttons, a floating button with the word "More" in
 * it, and three different kinds of pop-up — a dropdown menu, an inline sheet and an alert dialog — for three
 * groups of controls that are peers. They are four panels of one bar now, and the same card and row pieces
 * the settings screen is built from draw all of them.
 */
enum class SessionPanel { NONE, ACTIONS, INPUT, SCREEN, KEYBOARD }

/**
 * The bar itself: four tabs, and the panel above them.
 *
 * Tapping the open tab closes it, which is what leaves the picture alone. That matters more here than
 * anywhere else in the app — everything on this screen is covering something the user is trying to see.
 */
@Composable
fun SessionBar(
    panel: SessionPanel,
    onPanel: (SessionPanel) -> Unit,
    content: @Composable () -> Unit,
) {
    Column(modifier = Modifier.fillMaxWidth()) {
        AnimatedVisibility(
            visible = panel != SessionPanel.NONE,
            enter = fadeIn() + expandVertically(),
            exit = fadeOut() + shrinkVertically(),
        ) {
            Column(modifier = Modifier.padding(horizontal = 8.dp, vertical = 8.dp)) { content() }
        }

        Surface(
            color = MaterialTheme.colorScheme.card,
            shape = RoundedCornerShape(22.dp),
            // No inset of its own: the stack this sits in already holds the bar clear of the gesture bar
            // and of the soft keyboard, and a second allowance here would double the first.
            modifier = Modifier
                .padding(horizontal = 12.dp, vertical = 8.dp)
                .fillMaxWidth(),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp),
                horizontalArrangement = Arrangement.SpaceEvenly,
            ) {
                Tab(SessionPanel.ACTIONS, panel, onPanel, Icons.Outlined.Layers, R.string.session_tab_actions)
                Tab(SessionPanel.INPUT, panel, onPanel, Icons.Outlined.Mouse, R.string.session_tab_input)
                Tab(SessionPanel.SCREEN, panel, onPanel, Icons.Outlined.Monitor, R.string.session_tab_screen)
                Tab(SessionPanel.KEYBOARD, panel, onPanel, Icons.Outlined.Keyboard, R.string.session_tab_keyboard)
            }
        }
    }
}

@Composable
private fun Tab(
    tab: SessionPanel,
    current: SessionPanel,
    onPanel: (SessionPanel) -> Unit,
    icon: ImageVector,
    label: Int,
) {
    val selected = tab == current
    val tint = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant

    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = Modifier
            .clickable { onPanel(if (selected) SessionPanel.NONE else tab) }
            .padding(horizontal = 12.dp, vertical = 4.dp),
    ) {
        Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(24.dp))
        Spacer(Modifier.height(2.dp))
        Text(stringResource(label), style = MaterialTheme.typography.labelSmall, color = tint)
    }
}

/**
 * How well the session is running, as three bars rather than a sentence.
 *
 * This used to be a line of white text across the top-left corner — hostname, transport and round-trip —
 * permanently over the picture. The number matters far less often than "is it healthy", so the chip carries
 * the shape and keeps the sentence for a tap.
 */
@Composable
fun SignalChip(
    hostname: String,
    platform: String,
    transport: String,
    roundTripMillis: Int?,
    stalled: Boolean,
    expanded: Boolean,
    onToggle: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val bars = when {
        stalled || roundTripMillis == null -> Icons.Outlined.SignalCellularAlt1Bar
        roundTripMillis < 60 -> Icons.Outlined.SignalCellularAlt
        roundTripMillis < 150 -> Icons.Outlined.SignalCellularAlt2Bar
        else -> Icons.Outlined.SignalCellularAlt1Bar
    }

    val tint = when {
        stalled -> MaterialTheme.colorScheme.error
        roundTripMillis == null || roundTripMillis >= 150 -> HEALTH_WARNING
        else -> HEALTH_GOOD
    }

    Surface(
        color = SCRIM,
        shape = if (expanded) RoundedCornerShape(14.dp) else CircleShape,
        modifier = modifier.statusBarsPadding().padding(8.dp).clickable(onClick = onToggle),
    ) {
        if (!expanded) {
            Icon(
                bars,
                contentDescription = null,
                tint = tint,
                modifier = Modifier.padding(horizontal = 8.dp, vertical = 6.dp).size(18.dp),
            )
            return@Surface
        }

        // One fact per line.
        //
        // These four used to be a single line of text joined with separators, inside a chip that grew to
        // the right until it ran under the disconnect button and was cut off mid-word -- so the one thing
        // the chip exists to tell you, tapped for deliberately, was the part you could not read. Down the
        // page there is nothing to collide with, and each line can be labelled rather than inferred from
        // its position.
        Column(
            modifier = Modifier.widthIn(max = 240.dp).padding(horizontal = 10.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Row(
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(bars, contentDescription = null, tint = tint, modifier = Modifier.size(16.dp))
                Text(
                    text = hostname,
                    color = Color.White,
                    style = MaterialTheme.typography.labelMedium,
                )
            }

            SignalLine(stringResource(R.string.session_signal_platform), platform)
            SignalLine(stringResource(R.string.session_signal_route), routeName(transport))
            roundTripMillis?.let {
                SignalLine(stringResource(R.string.session_rtt), "$it ms")
            }
        }
    }
}

@Composable
private fun SignalLine(label: String, value: String) {
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(
            text = label,
            color = Color.White.copy(alpha = 0.6f),
            style = MaterialTheme.typography.labelSmall,
        )
        Text(
            text = value,
            color = Color.White,
            style = MaterialTheme.typography.labelSmall,
        )
    }
}

/**
 * The transport in words rather than in the identifier the protocol uses.
 *
 * `UDP_REFLEXIVE` is a true and useful thing to print in a log. It is not an answer to "how am I
 * connected", which is what somebody tapping this chip is asking.
 */
@Composable
private fun routeName(transport: String): String = when (transport) {
    "LAN", "UDP_LOCAL" -> stringResource(R.string.session_route_lan)
    "DIRECT_TCP" -> stringResource(R.string.session_route_direct)
    "PUNCHED_TCP", "UDP_REFLEXIVE" -> stringResource(R.string.session_route_punched)
    "RELAY", "UDP_RELAY" -> stringResource(R.string.session_route_relay)
    else -> transport
}

/**
 * The way out, on its own and in red.
 *
 * It was one of six identical text buttons, which gave ending the session exactly the same weight as
 * changing the picture quality. The desktop has had the rule for longer: the way out of the application is
 * red, and nothing else is.
 */
@Composable
fun DisconnectButton(onClick: () -> Unit, modifier: Modifier = Modifier) {
    Surface(
        color = MaterialTheme.colorScheme.error,
        shape = RoundedCornerShape(18.dp),
        modifier = modifier.statusBarsPadding().padding(8.dp).clickable(onClick = onClick),
    ) {
        Row(
            modifier = Modifier.padding(start = 10.dp, end = 14.dp, top = 8.dp, bottom = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                imageVector = Icons.Filled.Close,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onError,
                modifier = Modifier.size(18.dp),
            )
            Text(
                text = stringResource(R.string.session_disconnect),
                color = MaterialTheme.colorScheme.onError,
                style = MaterialTheme.typography.labelLarge,
                modifier = Modifier.padding(start = 6.dp),
            )
        }
    }
}

/** Over a picture nobody chose, only a wash of the app's own black is safe to put text on. */
private val SCRIM = Color(0xCC101010)

// The desktop's own health colours, so a phone and a desk side by side agree about what "fine" looks like.
private val HEALTH_GOOD = Color(0xFF4CAF50)
private val HEALTH_WARNING = Color(0xFFC9A227)

/** A panel: the same inset card the settings screen uses, over the video rather than over a page. */
@Composable
fun SessionPanelCard(content: @Composable androidx.compose.foundation.layout.ColumnScope.() -> Unit) {
    Surface(
        color = MaterialTheme.colorScheme.card,
        shape = RoundedCornerShape(14.dp),
        modifier = Modifier.fillMaxWidth().padding(horizontal = 4.dp),
    ) {
        Column(content = content)
    }
}
