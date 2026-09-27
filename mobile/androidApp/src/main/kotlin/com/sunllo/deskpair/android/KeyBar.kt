package com.sunllo.deskpair.android

import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sunllo.deskpair.KeyGroups
import com.sunllo.deskpair.RemoteKey
import com.sunllo.deskpair.RemoteModifier

/**
 * The keys a touchscreen cannot otherwise reach.
 *
 * A phone has no way to hold Ctrl and press C at the same time, so the modifier chips latch instead: tap
 * Ctrl, then type the next key, and the modifier is released afterwards. That is how sticky keys works on a
 * desktop, and it is what people already expect from a remote desktop client on a phone.
 *
 * Ctrl+Alt+Del is one entry rather than three chips, because it is one thing the host performs — on Windows
 * nothing can assemble it from separate keystrokes at all. It is on the bar now; this comment described it
 * for some time while the chip existed only on iOS.
 *
 * Everything past the first row is grouped and folded away. Eighty keys on one scrolling row would be a
 * worse answer than the twenty-seven this had, and the groups are in the shared layer so the two platforms
 * cannot offer different keyboards again.
 */
@Composable
fun KeyBar(
    model: SessionViewModel,
    pinned: Boolean,
    onPinnedChange: (Boolean) -> Unit,
    visible: Boolean,
    modifier: Modifier = Modifier,
) {
    val held by model.modifiers.collectAsStateWithLifecycle()
    val permissions by model.permissions.collectAsStateWithLifecycle()
    var typing by remember { mutableStateOf("") }
    var open by remember { mutableStateOf<KeyGroup?>(null) }

    // Never uninvited, and never gone while a modifier is still held: a bar that vanished mid-latch is
    // how a user ends up wondering why everything types as Ctrl+letter.
    if (!visible) {
        return
    }

    val allowed = permissions.keyboard

    Column(
        modifier = modifier
            .fillMaxWidth()
            // Android had no background here at all, so the keys sat illegibly over the picture.
            .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.9f)),
    ) {
        KeyRow {
            ModifierChip("Ctrl", RemoteModifier.CONTROL, held, model, allowed)
            ModifierChip("Alt", RemoteModifier.ALT, held, model, allowed)
            ModifierChip("Shift", RemoteModifier.SHIFT, held, model, allowed)
            ModifierChip("Cmd", RemoteModifier.META, held, model, allowed)

            for (cap in KeyGroups.basic) {
                KeyChip(cap.label, cap.key, model, allowed)
            }

            KeyChip(
                label = stringResource(R.string.session_ctrl_alt_del),
                key = RemoteKey.CTRL_ALT_DELETE,
                model = model,
                enabled = allowed,
            )

            for (group in KeyGroup.entries) {
                FilterChip(
                    selected = open == group,
                    onClick = { open = if (open == group) null else group },
                    label = { Text(stringResource(group.label)) },
                )
            }

            // Keeps the bar up across everything else. Without it the bar is only ever as long-lived as
            // the reason it appeared.
            FilterChip(
                selected = pinned,
                onClick = { onPinnedChange(!pinned) },
                label = { Text(stringResource(R.string.keyboard_pin)) },
            )
        }

        open?.let { group ->
            KeyRow {
                for (cap in group.caps) {
                    KeyChip(cap.label, cap.key, model, allowed)
                }
            }
        }

        // The only way to type without a hardware keyboard. Sent on Done rather than per keystroke: a soft
        // keyboard's autocorrect rewrites what is already there, and forwarding each edit would reach the
        // host as a stream of corrections rather than as the sentence someone meant.
        OutlinedTextField(
            value = typing,
            onValueChange = { typing = it },
            singleLine = true,
            enabled = allowed,
            label = { Text(stringResource(R.string.keyboard_type)) },
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(
                onDone = {
                    model.typeText(typing)
                    typing = ""
                },
            ),
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 8.dp, vertical = 4.dp)
                // While this has focus, keystrokes belong here rather than to the host.
                .onFocusChanged { model.typingFieldFocused = it.isFocused },
        )
    }
}

/** A foldable group of key caps, named on the bar. */
private enum class KeyGroup(val label: Int, val caps: List<KeyGroups.Cap>) {
    FUNCTION(R.string.keyboard_function, KeyGroups.function),
    NUMPAD(R.string.keyboard_numpad, KeyGroups.numpad),
    MORE(R.string.keyboard_more, KeyGroups.more),
    IME(R.string.keyboard_ime, KeyGroups.ime),
}

/** One scrolling row of caps. Scrolls rather than wraps: a bar that reflows moves keys under a thumb. */
@Composable
private fun KeyRow(content: @Composable (androidx.compose.foundation.layout.RowScope.() -> Unit)) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 8.dp, vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        content = content,
    )
}

@Composable
private fun ModifierChip(
    label: String,
    modifier: RemoteModifier,
    held: Set<RemoteModifier>,
    model: SessionViewModel,
    enabled: Boolean,
) {
    FilterChip(
        selected = modifier in held,
        enabled = enabled,
        onClick = { model.toggleModifier(modifier) },
        label = { Text(label) },
    )
}

/**
 * A key that fires on a tap, as against a modifier that latches.
 *
 * It used to paint its label white on a 35 %-transparent surface, which was all but unreadable: the bar sits
 * on the app's own background, not on the black video, so "white text" was white on light grey. Both colours
 * now come from the scheme, which keeps the pair legible in either theme. The chip is still quieter than a
 * [ModifierChip] — it carries no selected state to show — but quiet is not the same as invisible.
 */
@Composable
private fun KeyChip(label: String, key: RemoteKey, model: SessionViewModel, enabled: Boolean) {
    FilterChip(
        selected = false,
        enabled = enabled,
        onClick = { model.pressKey(key) },
        label = { Text(label) },
        colors = FilterChipDefaults.filterChipColors(
            containerColor = MaterialTheme.colorScheme.surfaceVariant,
            labelColor = MaterialTheme.colorScheme.onSurfaceVariant,
        ),
    )
}
