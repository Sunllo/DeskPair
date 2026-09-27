package com.sunllo.deskpair.android.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.KeyboardArrowRight
import androidx.compose.material.icons.outlined.RadioButtonChecked
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

// The four shapes a settings row can take. Before this the screen had no rows at all: it was a flat
// scrolling Column of labels, switches and one bordered text field per number, with section headings in the
// accent colour and current values as accent text floating on the right.
//
// The rule every one of them follows: the label is a short name, and the sentence that explains it goes
// underneath in grey. A label that is itself a sentence — "Show the remote pointer as well as yours" —
// wraps to two lines on a normal phone and to three at a larger font scale, and it pushes the control it
// belongs to out of line with every other control on the screen.

/** The row every other row is built from: icon, a name, an explanation, and whatever sits on the right. */
@Composable
private fun BaseRow(
    icon: ImageVector,
    label: String,
    hint: String?,
    onClick: (() -> Unit)?,
    labelColour: Color = Color.Unspecified,
    iconTint: Color = Color.Unspecified,
    monospacedHint: Boolean = false,
    trailing: @Composable RowScope.() -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(start = 16.dp, end = 16.dp, top = 12.dp, bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            imageVector = icon,
            contentDescription = null,
            tint = if (iconTint == Color.Unspecified) MaterialTheme.colorScheme.onSurfaceVariant else iconTint,
            modifier = Modifier.size(24.dp),
        )
        Column(modifier = Modifier.weight(1f).padding(start = 16.dp, end = 12.dp)) {
            Text(
                text = label,
                style = MaterialTheme.typography.bodyLarge,
                color = labelColour,
            )
            if (hint != null) {
                Text(
                    text = hint,
                    style = MaterialTheme.typography.bodySmall,
                    fontFamily = if (monospacedHint) FontFamily.Monospace else null,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        trailing()
    }
}

/**
 * A setting whose current value is worth seeing without opening anything.
 *
 * The value is grey and on the right, with a chevron after it — the shape the reference screenshots use for
 * every choice they have. It used to be a TextButton in the accent colour, which made every value on the
 * screen look like a link and made the screen look like a list of actions rather than a list of settings.
 */
@Composable
fun ValueRow(
    icon: ImageVector,
    label: String,
    hint: String? = null,
    value: String,
    valueColour: Color = Color.Unspecified,
    monospacedHint: Boolean = false,
    onClick: () -> Unit,
) {
    BaseRow(icon = icon, label = label, hint = hint, onClick = onClick, monospacedHint = monospacedHint) {
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = if (valueColour == Color.Unspecified) MaterialTheme.colorScheme.onSurfaceVariant else valueColour,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(end = 4.dp),
        )
        Icon(
            imageVector = Icons.AutoMirrored.Outlined.KeyboardArrowRight,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.size(20.dp),
        )
    }
}

/** A setting that is on or off. Tapping anywhere on the row flips it, not only the switch. */
@Composable
fun ToggleRow(
    icon: ImageVector,
    label: String,
    hint: String? = null,
    checked: Boolean,
    onChange: (Boolean) -> Unit,
) {
    BaseRow(icon = icon, label = label, hint = hint, onClick = { onChange(!checked) }) {
        Switch(checked = checked, onCheckedChange = onChange)
    }
}

/**
 * A bounded integer.
 *
 * Nothing out of range ever reaches the settings, which is what `NumericField` is for on the desktop: a
 * half-typed "1" on the way to "120" must not be stored as a frame rate of one. The field used to be a
 * full-width bordered box of its own, which was the only form control on the screen and looked like one.
 */
@Composable
fun NumberRow(
    icon: ImageVector,
    label: String,
    hint: String? = null,
    value: Int,
    range: IntRange,
    invalid: String,
    onValue: (Int) -> Unit,
) {
    var text by remember(value) { mutableStateOf(value.toString()) }
    val parsed = text.toIntOrNull()
    val bad = parsed == null || parsed !in range

    BaseRow(icon = icon, label = label, hint = if (bad) invalid else hint, onClick = null) {
        TextField(
            value = text,
            onValueChange = {
                text = it.filter { c -> c.isDigit() }.take(6)
                text.toIntOrNull()?.takeIf { n -> n in range }?.let(onValue)
            },
            singleLine = true,
            isError = bad,
            textStyle = MaterialTheme.typography.bodyLarge.copy(textAlign = TextAlign.End),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
            colors = transparentField(),
            modifier = Modifier.width(72.dp),
        )
    }
}

/**
 * A setting that is a piece of text: a server name, a key.
 *
 * The field's own floating label does the naming, so this needs no second one. What it does need is the
 * transparent container: three bordered boxes stacked inside one card is what the network section used to
 * look like.
 */
@Composable
fun FieldRow(
    icon: ImageVector,
    label: String,
    value: String,
    onValue: (String) -> Unit,
    hint: String? = null,
    isError: Boolean = false,
    monospaced: Boolean = false,
    /**
     * Hides what is typed, for a password.
     *
     * Only for a password somebody is remembering. The one-time password a desk shows is deliberately not
     * hidden: it is read off somebody else's screen, it is worth nothing a moment later, and masking it
     * only makes it harder to tell whether a thumb slipped.
     */
    secret: Boolean = false,
) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            imageVector = icon,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.size(24.dp),
        )
        TextField(
            value = value,
            onValueChange = onValue,
            label = { Text(label) },
            singleLine = true,
            isError = isError,
            textStyle = MaterialTheme.typography.bodyLarge.copy(
                fontFamily = if (monospaced) FontFamily.Monospace else null,
            ),
            visualTransformation = if (secret) {
                PasswordVisualTransformation()
            } else {
                VisualTransformation.None
            },
            supportingText = if (hint != null) {
                { Text(hint) }
            } else {
                null
            },
            colors = transparentField(),
            modifier = Modifier.weight(1f).padding(start = 8.dp),
        )
    }
}

/**
 * A row that does something rather than holding a value.
 *
 * The old screen used bordered buttons for these, sitting loose in the middle of the list — "Clear recent
 * connections", "Forget all" — which broke the column in half wherever one appeared.
 */
@Composable
fun ActionRow(
    icon: ImageVector,
    label: String,
    destructive: Boolean = false,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    BaseRow(
        icon = icon,
        label = label,
        hint = null,
        // Greyed and inert rather than hidden: a control that vanishes leaves the user wondering whether
        // they misremembered it, where one that is visibly unavailable answers the question.
        onClick = if (enabled) onClick else null,
        labelColour = when {
            !enabled -> MaterialTheme.colorScheme.outline
            destructive -> MaterialTheme.colorScheme.error
            else -> MaterialTheme.colorScheme.primary
        },
        iconTint = if (enabled) Color.Unspecified else MaterialTheme.colorScheme.outline,
    ) {
    }
}

/**
 * One option in a choice sheet.
 *
 * A radio rather than a tick: these are exclusive, and a row of ticks would suggest they are not.
 */
@Composable
fun OptionRow(label: String, selected: Boolean, onClick: () -> Unit) {
    BaseRow(
        icon = if (selected) Icons.Outlined.RadioButtonChecked else Icons.Outlined.RadioButtonUnchecked,
        label = label,
        hint = null,
        onClick = onClick,
        iconTint = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline,
    ) {
    }
}

/**
 * Something to read rather than to change: a version, a path, a fingerprint.
 *
 * A short value sits on the right in grey. A long one — a settings-file path is sixty characters — goes
 * under the label instead, because right-aligned it wraps into a ragged column four lines deep.
 */
@Composable
fun InfoRow(
    icon: ImageVector,
    label: String,
    value: String = "",
    detail: String? = null,
    monospacedDetail: Boolean = false,
) {
    BaseRow(
        icon = icon,
        label = label,
        hint = detail,
        onClick = null,
        monospacedHint = monospacedDetail,
    ) {
        if (value.isNotEmpty()) {
            Text(
                text = value,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.End,
            )
        }
    }
}

/** The card is the box; a control with a box of its own inside it is two borders around one thing. */
@Composable
private fun transparentField() = TextFieldDefaults.colors(
    focusedContainerColor = Color.Transparent,
    unfocusedContainerColor = Color.Transparent,
    disabledContainerColor = Color.Transparent,
    errorContainerColor = Color.Transparent,
    focusedIndicatorColor = Color.Transparent,
    unfocusedIndicatorColor = Color.Transparent,
    errorIndicatorColor = Color.Transparent,
)
