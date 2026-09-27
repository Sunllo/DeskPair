package com.sunllo.deskpair.android

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.text.KeyboardActions
import com.sunllo.deskpair.PasswordText
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import com.sunllo.deskpair.store.Targets

/**
 * The password prompt.
 *
 * Plain text, deliberately, and the same on iOS. What is usually typed here is a six-character one-time
 * password that changes every session, read off another screen and thumbed in: masking it buys very little
 * against a shoulder and costs a retry every time a thumb slips. A host that has set a permanent password
 * is the case this does not suit, and is the reason to revisit it.
 *
 * Whether the password is kept follows the setting; this prompt no longer asks. "Remember" writes to the
 * keystore, never to the settings file, for the same reason the desktop keeps its hashes out of
 * `desktop.json`.
 *
 * It belongs to the app rather than to a screen: two tabs start sessions, and a prompt owned by one of them
 * would vanish if the user changed tab while it was up.
 */
@Composable
fun PasswordSheet(
    target: String,
    offerToRemember: Boolean,
    onDismiss: () -> Unit,
    onConnect: (password: String, remember: Boolean) -> Unit,
) {
    var password by remember { mutableStateOf("") }
    var remember by remember { mutableStateOf(offerToRemember) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(Targets.formatId(target)) },
        text = {
            Column {
                OutlinedTextField(
                    value = password,
                    // ASCII only, as it is typed: a host password has to survive two keyboards, and a
                    // Chinese input method open over this field would send characters the host never saw.
                    // The keyboard type asks for a plain keyboard; the filter is what guarantees it.
                    onValueChange = { password = PasswordText.ascii(it) },
                    label = { Text(stringResource(R.string.connect_password)) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Go),
                    keyboardActions = KeyboardActions(onGo = { onConnect(password, remember) }),
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { onConnect(password, remember) }) {
                Text(stringResource(R.string.connect_action))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.connect_cancel)) }
        },
    )
}
