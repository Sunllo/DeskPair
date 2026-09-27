package com.sunllo.deskpair.android

import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.outlined.CreateNewFolder
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Edit
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.sunllo.deskpair.android.ui.BrandMark
import com.sunllo.deskpair.android.ui.InsetCard
import com.sunllo.deskpair.android.ui.RowDivider
import com.sunllo.deskpair.store.AppSettings
import com.sunllo.deskpair.store.Favourite
import com.sunllo.deskpair.transport.OnlineState
import com.sunllo.deskpair.store.Targets

/**
 * The machines someone chose to keep, as against the ones they happen to have visited.
 *
 * A tab of its own rather than a section under the connect form. The desktop gives favourites a whole page
 * for the same reason: a list people curate is the one they come back to, and it should not have to share
 * the screen with a text box.
 */
@Composable
fun FavouritesScreen(
    settings: AppSettings,
    presence: Map<String, OnlineState> = emptyMap(),
    onStart: (target: String) -> Unit,
    onRemove: (target: String) -> Unit,
    onEdit: (target: String) -> Unit,
    onRenameGroup: (group: String) -> Unit,
    onRemoveGroup: (group: String) -> Unit,
    signedIn: Boolean,
    contentPadding: PaddingValues,
) {
    // The list belongs to an account, so without one there is nothing to show and nothing that would
    // survive this phone. Said plainly rather than by showing an empty list, which would read as a list
    // somebody had not filled in yet.
    if (!signedIn) {
        Empty(
            contentPadding,
            title = R.string.devices_sign_in_required,
            hint = R.string.devices_sign_in_required_hint,
        )
        return
    }

    if (settings.favourites.isEmpty() && settings.groupNames.isEmpty()) {
        Empty(contentPadding)
        return
    }

    // Folders come from the desktop, where they are called groups, and arrive with a synced list. Sorted by
    // name with the ungrouped ones last, which is where somebody looking for "the ones I have not filed yet"
    // expects them -- and a list with no folders at all is one unnamed section, exactly as it was before.
    // Built from the named groups as well as the devices, so a group made a moment ago is on screen
    // before anything is filed in it. A group somebody cannot see is a group they make twice.
    val filed = settings.favourites.groupBy { it.group.trim() }
    val named = settings.groupNames.map { it.trim() }.filter { it.isNotEmpty() }
    val sections = (filed.keys + named)
        .distinctBy { it.lowercase() }
        .map { it to filed[it].orEmpty() }
        // An empty unfiled section is not a section: it is the absence of one.
        .filter { (name, entries) -> name.isNotEmpty() || entries.isNotEmpty() }
        .sortedWith(compareBy({ it.first.isEmpty() }, { it.first.lowercase() }))

    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(
            top = 16.dp + contentPadding.calculateTopPadding(),
            bottom = 24.dp + contentPadding.calculateBottomPadding(),
        ),
    ) {
        // The one thing on this screen that is not a device. A button rather than a gesture, because
        // making a folder is not something anybody guesses at.
        item(key = "new-group") {
            Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                horizontalArrangement = Arrangement.End,
            ) {
                TextButton(onClick = { onRenameGroup("") }) {
                    Icon(
                        imageVector = Icons.Outlined.CreateNewFolder,
                        contentDescription = null,
                        modifier = Modifier.size(18.dp),
                    )
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(R.string.devices_group_add))
                }
            }
        }

        for ((group, entries) in sections) {
            // Always, including when the unfiled section is the only one: hidden, it read as a list with
            // no folders at all, and somebody who has just made a group and seen nothing change cannot
            // tell that the machines they already had are the ones sitting outside it.
            run {
                item(key = "header-$group") {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(start = 28.dp, end = 12.dp, top = 12.dp, bottom = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            text = group.ifEmpty { stringResource(R.string.favourites_ungrouped) },
                            style = MaterialTheme.typography.labelLarge,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.weight(1f),
                        )

                        // On the header rather than behind a swipe: a header is not a row, and a gesture
                        // it does not answer is worse than a button that is plainly there.
                        if (group.isNotEmpty()) {
                            IconButton(onClick = { onRenameGroup(group) }) {
                                Icon(
                                    imageVector = Icons.Outlined.Edit,
                                    contentDescription = stringResource(R.string.devices_group_rename),
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            IconButton(onClick = { onRemoveGroup(group) }) {
                                Icon(
                                    imageVector = Icons.Outlined.Delete,
                                    contentDescription = stringResource(R.string.devices_group_delete),
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }
            }

            item(key = "group-$group") {
                InsetCard(modifier = Modifier.padding(horizontal = 16.dp)) {
                    entries.forEachIndexed { index, favourite ->
                        if (index > 0) {
                            RowDivider()
                        }

                        key(favourite.target) {
                            SwipeToRemove(onRemove = { onRemove(favourite.target) }) {
                                FavouriteRow(
                                    favourite = favourite,
                                    online = presence[favourite.target] ?: OnlineState.UNKNOWN,
                                    onConnect = { onStart(favourite.target) },
                                    onEdit = { onEdit(favourite.target) },
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

/**
 * Nothing saved yet.
 *
 * The mark, faded, rather than an icon borrowed from somewhere: an empty screen is the one place there is
 * room for it, and it says which app is empty.
 */
@Composable
private fun Empty(
    contentPadding: PaddingValues,
    @StringRes title: Int = R.string.home_no_favourites,
    @StringRes hint: Int = R.string.home_favourites_empty_hint,
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(top = contentPadding.calculateTopPadding(), bottom = contentPadding.calculateBottomPadding())
            .padding(horizontal = 40.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        BrandMark(size = 56.dp, modifier = Modifier.alpha(0.25f))
        Spacer(Modifier.height(20.dp))
        Text(
            text = stringResource(title),
            style = MaterialTheme.typography.titleMedium,
        )
        Spacer(Modifier.height(6.dp))
        Text(
            text = stringResource(hint),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
    }
}

/**
 * A row that is swept away to the left.
 *
 * The same shape the recent list uses, because removing a saved machine and forgetting a recent one are
 * the same gesture to a hand and should not be two different ones on two screens of the same app.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SwipeToRemove(onRemove: () -> Unit, content: @Composable () -> Unit) {
    val dismiss = rememberSwipeToDismissBoxState(
        // Confirmed here rather than in an effect: the callback removes the row from settings, so the box
        // is gone with it and there is nothing left to animate back.
        confirmValueChange = { value ->
            if (value == SwipeToDismissBoxValue.EndToStart) {
                onRemove()
                true
            } else {
                false
            }
        },
    )

    SwipeToDismissBox(
        state = dismiss,
        enableDismissFromStartToEnd = false,
        backgroundContent = {
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .background(MaterialTheme.colorScheme.errorContainer)
                    .padding(horizontal = 20.dp),
                contentAlignment = Alignment.CenterEnd,
            ) {
                Icon(
                    imageVector = Icons.Outlined.Delete,
                    contentDescription = stringResource(R.string.home_remove_favourite),
                    tint = MaterialTheme.colorScheme.onErrorContainer,
                )
            }
        },
        content = { content() },
    )
}

@Composable
private fun FavouriteRow(favourite: Favourite, online: OnlineState, onConnect: () -> Unit, onEdit: () -> Unit) {
    PeerRow(
        title = favourite.title,
        subtitle = when {
            favourite.note.isNotEmpty() -> favourite.note
            favourite.alias.isNotEmpty() -> Targets.formatId(favourite.target)
            else -> ""
        },
        platform = favourite.platform,
        monospacedTitle = favourite.alias.isEmpty(),
        online = online,
        onClick = onConnect,
    ) {
        // Editing is the common one -- renaming a machine, or setting the password it always wants --
        // so it is the button, and removing is behind a swipe where a destructive action belongs.
        IconButton(onClick = onEdit) {
            Icon(
                imageVector = Icons.Outlined.Edit,
                contentDescription = stringResource(R.string.devices_edit),
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}
