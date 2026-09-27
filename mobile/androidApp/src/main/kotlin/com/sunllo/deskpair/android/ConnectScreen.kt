package com.sunllo.deskpair.android

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.FavoriteBorder
import androidx.compose.material.icons.outlined.QrCodeScanner
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.sunllo.deskpair.android.ui.BrandMark
import com.sunllo.deskpair.android.ui.InsetCard
import com.sunllo.deskpair.android.ui.card
import com.sunllo.deskpair.android.ui.RowDivider
import com.sunllo.deskpair.android.ui.SectionHeader
import com.sunllo.deskpair.store.AppSettings
import com.sunllo.deskpair.store.RecentPeer
import com.sunllo.deskpair.transport.OnlineState
import com.sunllo.deskpair.store.Targets

/**
 * Where a session starts.
 *
 * Laid out after the desktop's second home card (`Views/HomeView.axaml`), which is the only one that
 * applies: the first is "this computer", and this app is not one. One text box takes both an id and an
 * address, exactly as the desktop's does, and [Targets.isDirect] decides which it was.
 *
 * Three buttons used to sit in a row under the field, all of them the same size: Connect, Scan a code, and
 * sometimes Try again. Three is one too many for a phone — at a larger font scale the third was squeezed
 * into a column one letter wide — and none of them looked like the main thing to do. Connecting is the main
 * thing to do, so it is the only filled button on the screen; scanning is how you fill the field in, so it
 * has moved into the field; and retrying is offered as plain text under it, when there is something to
 * retry.
 */
@Composable
fun ConnectScreen(
    settings: AppSettings,
    presence: Map<String, OnlineState> = emptyMap(),
    error: String?,
    canRetry: Boolean,
    onStart: (target: String) -> Unit,
    onTerminal: (target: String) -> Unit = {},
    onRetry: () -> Unit,
    onToggleFavourite: (String) -> Unit,
    onForgetRecent: (String) -> Unit,
    onScan: (onDone: (target: String?, message: String?) -> Unit) -> Unit,
    contentPadding: PaddingValues,
) {
    var typed by remember { mutableStateOf("") }
    var scanNotice by remember { mutableStateOf<String?>(null) }

    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        // The scaffold hands down the system bars. Edge to edge means the list really does start under
        // the status bar and end under the gesture bar, so both have to be added rather than assumed.
        contentPadding = PaddingValues(
            top = contentPadding.calculateTopPadding(),
            bottom = 24.dp + contentPadding.calculateBottomPadding(),
        ),
    ) {
        item { Masthead() }

        item {
            InsetCard(modifier = Modifier.padding(horizontal = 16.dp)) {
                TextField(
                    value = typed,
                    onValueChange = { typed = it },
                    placeholder = { Text(stringResource(R.string.connect_target)) },
                    singleLine = true,
                    textStyle = MaterialTheme.typography.titleMedium.copy(fontFamily = FontFamily.Monospace),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Go),
                    keyboardActions = KeyboardActions(onGo = { onStart(typed) }),
                    trailingIcon = {
                        IconButton(
                            onClick = {
                                scanNotice = null
                                onScan { target, message ->
                                    scanNotice = message
                                    if (target != null) {
                                        // Scanned, so dialled. This used to only fill the field, on the
                                        // grounds that connecting to whatever a camera happened to see
                                        // was not a good default -- but a code now carries the desk's
                                        // one-time password, so scanning it is the whole ceremony and
                                        // stopping to ask for one more tap is asking for nothing. The
                                        // field is filled in as well, so a failure leaves something to
                                        // try again with.
                                        typed = Targets.formatId(target)
                                        onStart(target)
                                    }
                                }
                            },
                        ) {
                            Icon(
                                imageVector = Icons.Outlined.QrCodeScanner,
                                contentDescription = stringResource(R.string.home_scan),
                            )
                        }
                    },
                    // The card is the box. A text field with a box of its own inside it is two borders
                    // around one thing.
                    colors = TextFieldDefaults.colors(
                        focusedContainerColor = Color.Transparent,
                        unfocusedContainerColor = Color.Transparent,
                        disabledContainerColor = Color.Transparent,
                        focusedIndicatorColor = Color.Transparent,
                        unfocusedIndicatorColor = Color.Transparent,
                    ),
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        }

        item {
            Column(modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 16.dp)) {
                Button(
                    onClick = { onStart(typed) },
                    enabled = Targets.normalise(typed).isNotEmpty(),
                    modifier = Modifier.fillMaxWidth().height(50.dp),
                ) {
                    Text(stringResource(R.string.connect_action))
                }

                // A shell rather than a picture. Plain text, not a second filled button: connecting is
                // still the main thing to do, and a host only allows this if its owner has turned it on.
                if (Targets.normalise(typed).isNotEmpty()) {
                    TextButton(
                        onClick = { onTerminal(typed) },
                        modifier = Modifier.fillMaxWidth().padding(top = 4.dp),
                    ) {
                        Text(stringResource(R.string.terminal_open))
                    }
                }

                if (canRetry) {
                    // A phone loses its network in lifts and tunnels. Retyping an address every time is
                    // what turns a tool someone keeps into one they uninstall.
                    TextButton(
                        onClick = onRetry,
                        modifier = Modifier.fillMaxWidth().padding(top = 4.dp),
                    ) {
                        Text(stringResource(R.string.connect_retry))
                    }
                }

                Notices(settings = settings, scanNotice = scanNotice, error = error)
            }
        }

        item { SectionHeader(stringResource(R.string.home_recent)) }

        if (settings.recent.isEmpty()) {
            item {
                InsetCard(modifier = Modifier.padding(horizontal = 16.dp)) {
                    Text(
                        text = stringResource(R.string.home_no_recent),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(16.dp),
                    )
                }
            }
        } else {
            item {
                InsetCard(modifier = Modifier.padding(horizontal = 16.dp)) {
                    settings.recent.forEachIndexed { index, peer ->
                        if (index > 0) {
                            RowDivider()
                        }

                        key(peer.id) {
                            RecentRow(
                                peer = peer,
                                online = presence[peer.id] ?: OnlineState.UNKNOWN,
                                saved = settings.isFavourite(peer.id),
                                onConnect = { onStart(peer.id) },
                                onToggleFavourite = { onToggleFavourite(peer.id) },
                                onForget = { onForgetRecent(peer.id) },
                            )
                        }
                    }
                }
            }
        }
    }
}

/**
 * The mark and the product's name.
 *
 * The screen used to open with the sentence "Control a remote desk", which describes what the app is for but
 * never says what it is. A first-time user should be able to tell which of the apps on their phone this is.
 */
@Composable
private fun Masthead() {
    Column(
        modifier = Modifier.fillMaxWidth().padding(top = 16.dp, bottom = 20.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        BrandMark(size = 56.dp)
        Spacer(Modifier.height(10.dp))
        Text(
            text = stringResource(R.string.app_name),
            style = MaterialTheme.typography.headlineSmall,
        )
        Text(
            text = stringResource(R.string.home_control_remote),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
        )
    }
}

/** Everything the screen might need to say, in the one place someone is already looking. */
@Composable
private fun Notices(settings: AppSettings, scanNotice: String?, error: String?) {
    scanNotice?.let {
        Text(
            text = it,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(top = 12.dp),
        )
    }

    // Said here rather than only in settings: this is the moment someone types an id and finds out it
    // cannot work.
    //
    // An empty address is the normal state now, not a broken one -- it means the portal is asked. Only
    // somebody who turned that off and then named nothing has nowhere to go.
    if (settings.rendezvousServer.isEmpty() && !settings.useDirectoryServers) {
        Text(
            text = stringResource(R.string.home_no_server),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(top = 12.dp),
        )
    }

    error?.let {
        Text(
            text = it,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.error,
            modifier = Modifier.padding(top = 12.dp),
        )
    }
}

/**
 * One recent connection.
 *
 * Two trailing buttons used to sit here, a heart and an overflow, and between them they took enough width
 * that the subtitle was ellipsised on a normal phone: "Studio-Mac-mini · 12 mi…". Forgetting a row is rare
 * and destructive, so it moved to a swipe, which is where the iOS list has always had it. The heart stays
 * because it is frequent and because it is also the only sign of whether this machine is saved.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun RecentRow(
    peer: RecentPeer,
    online: OnlineState,
    saved: Boolean,
    onConnect: () -> Unit,
    onToggleFavourite: () -> Unit,
    onForget: () -> Unit,
) {
    val dismiss = rememberSwipeToDismissBoxState(
        // Confirming here rather than in an effect: the callback removes the row from settings, and the box
        // is then gone with it, so there is nothing left to animate back.
        confirmValueChange = { value ->
            if (value == SwipeToDismissBoxValue.EndToStart) {
                onForget()
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
                    contentDescription = stringResource(R.string.home_forget_recent),
                    tint = MaterialTheme.colorScheme.onErrorContainer,
                )
            }
        },
    ) {
        Surface(color = MaterialTheme.colorScheme.card) {
            PeerRow(
                title = Targets.formatId(peer.id),
                subtitle = listOfNotNull(peer.name.takeIf { it.isNotEmpty() }, ago(peer.lastConnectedMs))
                    .joinToString("  ·  "),
                platform = peer.platform,
                monospacedTitle = true,
                online = online,
                onClick = onConnect,
            ) {
                IconButton(onClick = onToggleFavourite) {
                    Icon(
                        imageVector = if (saved) Icons.Filled.Favorite else Icons.Outlined.FavoriteBorder,
                        contentDescription = stringResource(
                            if (saved) R.string.home_remove_favourite else R.string.home_add_favourite,
                        ),
                        tint = if (saved) {
                            MaterialTheme.colorScheme.primary
                        } else {
                            MaterialTheme.colorScheme.onSurfaceVariant
                        },
                    )
                }
            }
        }
    }
}
