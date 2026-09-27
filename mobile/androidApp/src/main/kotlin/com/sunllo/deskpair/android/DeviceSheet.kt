package com.sunllo.deskpair.android

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Key
import androidx.compose.material.icons.outlined.Tag
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.sunllo.deskpair.android.ui.FieldRow
import com.sunllo.deskpair.android.ui.InsetCard
import com.sunllo.deskpair.store.Targets

/**
 * Naming a machine and, if you want, giving it a password to keep.
 *
 * One sheet for both jobs. Adding from a recent connection and editing one already on the list ask for
 * exactly the same two things, and two sheets that ask the same questions drift apart -- the desktop's
 * device list has been through that, which is why its add and its edit are one dialog too.
 *
 * The password is optional and is the point of the whole thing on a phone. A desk shows a fresh one-time
 * password every time; somebody who reaches the same machine every day sets a fixed one on it once and
 * then never types anything again. It goes to the encrypted store rather than into settings.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DeviceSheet(
    target: String,
    initialAlias: String,
    initialGroup: String,
    initialPassword: String,
    onSave: (alias: String, group: String, password: String) -> Unit,
    onDismiss: () -> Unit,
) {
    var alias by remember(target) { mutableStateOf(initialAlias) }
    var group by remember(target) { mutableStateOf(initialGroup) }
    var password by remember(target) { mutableStateOf(initialPassword) }
    val sheet = rememberModalBottomSheetState()

    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = sheet) {
        Column(
            modifier = Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(
                text = stringResource(R.string.devices_edit_title),
                style = MaterialTheme.typography.titleMedium,
                modifier = Modifier.fillMaxWidth(),
                textAlign = TextAlign.Center,
            )
            Text(
                text = Targets.formatId(target),
                style = MaterialTheme.typography.bodyMedium.copy(fontFamily = FontFamily.Monospace),
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.fillMaxWidth(),
                textAlign = TextAlign.Center,
            )

            InsetCard {
                FieldRow(
                    icon = Icons.Outlined.Tag,
                    label = stringResource(R.string.devices_edit_name),
                    value = alias,
                    onValue = { alias = it },
                )
            }

            // Typing makes a group; there is nothing else to create. A group is a name on a device
            // here, the way it is on the desktop.
            InsetCard {
                FieldRow(
                    icon = Icons.Outlined.Folder,
                    label = stringResource(R.string.devices_edit_group),
                    value = group,
                    onValue = { group = it },
                )
            }

            InsetCard {
                FieldRow(
                    icon = Icons.Outlined.Key,
                    label = stringResource(R.string.devices_edit_password),
                    // Not masked, for the same reason the connection prompt is not: this is a password
                    // read off somebody's screen or agreed out loud, and hiding it hides a slipped thumb.
                    value = password,
                    onValue = { password = it },
                    hint = stringResource(R.string.devices_edit_password_hint),
                )
            }

            Button(
                onClick = { onSave(alias.trim(), group.trim(), password) },
                modifier = Modifier.fillMaxWidth().align(Alignment.CenterHorizontally),
            ) {
                Text(stringResource(R.string.devices_edit_save))
            }
        }
    }
}
