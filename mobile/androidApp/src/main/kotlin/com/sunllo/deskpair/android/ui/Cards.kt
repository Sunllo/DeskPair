package com.sunllo.deskpair.android.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

// The shape every list in this app is made of: small grey headings over white cards with hairlines between
// their rows. Settings used to be a flat scrolling column of dividers and the peer lists were bare rows on
// the page background, so the two screens looked like different products. One set of pieces, used by both.

/**
 * Where a card's rows begin, past the icon column.
 *
 * A divider that runs the full width cuts through the icons and makes the column read as a table. Starting
 * it after them is what makes a stack of rows read as one object.
 */
val RowInset: Dp = 56.dp

/** The label over a card. Small and grey: it names the group, it is not a heading in the page. */
@Composable
fun SectionHeader(text: String, modifier: Modifier = Modifier) {
    Text(
        text = text,
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = modifier.padding(start = 16.dp, end = 16.dp, top = 20.dp, bottom = 8.dp),
    )
}

/**
 * A group of rows on one surface.
 *
 * White on the light ground, a step up from near-black on the dark one — see [card], which is the one colour
 * that cannot come from a single Material role.
 */
@Composable
fun InsetCard(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(14.dp),
        color = MaterialTheme.colorScheme.card,
    ) {
        Column(content = content)
    }
}

/** The hairline between two rows of the same card. Never after the last one. */
@Composable
fun RowDivider(startIndent: Dp = RowInset) {
    HorizontalDivider(
        modifier = Modifier.padding(start = startIndent),
        color = MaterialTheme.colorScheme.outlineVariant,
    )
}

/**
 * The sentence under a card that explains what the switch above it does.
 *
 * Below the card rather than inside the row, which is what lets the label itself be two words instead of a
 * sentence that wraps to two lines at any font scale above the default.
 */
@Composable
fun CardFooter(text: String, modifier: Modifier = Modifier) {
    Text(
        text = text,
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = modifier.padding(start = 16.dp, end = 16.dp, top = 8.dp),
    )
}
