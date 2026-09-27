package com.sunllo.deskpair.android

import androidx.activity.compose.BackHandler
import androidx.compose.animation.Crossfade
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DesktopWindows
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.outlined.DesktopWindows
import androidx.compose.material.icons.outlined.FavoriteBorder
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sunllo.deskpair.android.ui.card
import com.sunllo.deskpair.store.AppSettings
import com.sunllo.deskpair.store.Targets
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlinx.coroutines.delay

/**
 * Everything before a session: the three tabs, and the one prompt they share.
 *
 * The tab bar lives here rather than around the whole app because a session takes the window. There is no
 * navigating away from a remote desk to look at settings — the picture is the point, and the controls that
 * belong to it are its own.
 *
 * Starting a connection is the app's job rather than a screen's, which is why it sits at this level. Both
 * the connect tab and the favourites tab open sessions, and a password prompt owned by one of them would
 * disappear the moment the user changed tab underneath it.
 */
@Composable
fun PreSession(
    model: SessionViewModel,
    settings: AppSettings,
    screen: Screen,
    error: String?,
    onScan: (onDone: (target: String?, message: String?) -> Unit) -> Unit,
) {
    var asking by remember { mutableStateOf<String?>(null) }

    /// Whether the password being asked for opens a terminal rather than the desktop.
    var askingTerminal by remember { mutableStateOf(false) }

    /// The machine being named, if the sheet is open. Owned here rather than by either screen: both open
    /// it, and a sheet owned by one tab would vanish the moment somebody changed tab underneath it.
    var naming by remember { mutableStateOf<String?>(null) }

    /// The group being renamed, or the empty string while one is being made; null when neither.
    var renamingGroup by remember { mutableStateOf<String?>(null) }
    var groupName by remember { mutableStateOf("") }
    val canRetry by model.canReconnect.collectAsStateWithLifecycle()
    val keyChange by model.keyChanged.collectAsStateWithLifecycle()
    val account by model.store.account.state.collectAsStateWithLifecycle()

    /**
     * Connects, or asks for a password first.
     *
     * A password that came out of a QR code is used first and used once: the whole reason the code carries
     * one is so that scanning is the entire ceremony, and the host throws it away as soon as it is used, so
     * there would be nothing to ask for a second time. After that a remembered password is used without a
     * prompt — the point of remembering it — and a target nobody has connected to yet asks, because the
     * host decides whether it needs one and we cannot know before trying.
     */
    fun start(target: String, terminal: Boolean = false) {
        val normalised = Targets.normalise(target)
        if (normalised.isEmpty()) {
            return
        }

        val scanned = model.store.takeScannedPassword(normalised)
        val known = scanned ?: model.store.rememberedPassword(normalised)
        if (known != null) {
            if (terminal) model.openTerminal(normalised, known) else model.connect(normalised, known)
        } else {
            askingTerminal = terminal
            asking = normalised
        }
    }

    // The saved list follows the account, when there is one.
    //
    // Once at start-up, three seconds after the list changes here (so a burst of edits is one exchange), and
    // every fifteen minutes — the timer is the only thing that can notice what somebody else did. A pull
    // changes the list and so triggers the second effect once more, which then finds nothing and stops.
    LaunchedEffect(Unit) {
        while (true) {
            runCatching { model.store.bookSync.sync() }
            delay(15.minutes)
        }
    }

    // Which desks are reachable, asked every fifteen seconds while these lists are on screen -- the same
    // cadence as the desktop's device list -- and again as soon as the lists change.
    val presence by model.store.presence.collectAsStateWithLifecycle()
    LaunchedEffect(settings.favourites, settings.recent) {
        while (true) {
            model.store.refreshPresence()
            kotlinx.coroutines.delay(15_000)
        }
    }

    LaunchedEffect(settings.favourites) {
        delay(3.seconds)
        runCatching { model.store.bookSync.sync() }
    }

    // Back means "the tab I started on", and only from somewhere else. On the connect tab it falls through
    // and leaves the app, which is what back means from the first screen.
    BackHandler(enabled = screen != Screen.CONNECT) { model.show(Screen.CONNECT) }

    Scaffold(
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.card) {
                Tab(Screen.CONNECT, screen, model, R.string.nav_connect, Icons.Filled.DesktopWindows, Icons.Outlined.DesktopWindows)
                Tab(Screen.FAVOURITES, screen, model, R.string.nav_favourites, Icons.Filled.Favorite, Icons.Outlined.FavoriteBorder)
                Tab(Screen.SETTINGS, screen, model, R.string.nav_settings, Icons.Filled.Settings, Icons.Outlined.Settings)
            }
        },
    ) { inner ->
        // Crossfade rather than an instant swap: the three tabs have nothing in common visually, and a hard
        // cut between them reads as the app having been replaced rather than as having moved.
        Crossfade(targetState = screen, label = "tab") { current ->
            Box(modifier = Modifier.fillMaxSize()) {
                when (current) {
                    Screen.CONNECT -> ConnectScreen(
                        settings = settings,
                        presence = presence,
                        error = error,
                        canRetry = canRetry,
                        onStart = { start(it) },
                        onTerminal = { start(it, terminal = true) },
                        onRetry = model::reconnect,
                        onToggleFavourite = { naming = it },
                        onForgetRecent = { target -> model.store.update { it.withoutRecent(target) } },
                        onScan = onScan,
                        contentPadding = inner,
                    )

                    Screen.FAVOURITES -> FavouritesScreen(
                        settings = settings,
                        presence = presence,
                        onStart = { start(it) },
                        // Takes the saved password with it: keeping a secret for a machine the app no
                        // longer admits to knowing is not tidiness.
                        onRemove = { model.store.removeDevice(it) },
                        onEdit = { naming = it },
                        onRenameGroup = { renamingGroup = it; groupName = it },
                        onRemoveGroup = { model.store.removeGroup(it) },
                        signedIn = account.link.isLinked,
                        contentPadding = inner,
                    )

                    Screen.SETTINGS -> SettingsTab(model, settings, inner)
                }
            }
        }
    }

    // Asked where it happened, rather than answered later in a list of fingerprints somebody would have
    // to know to go and find. Two answers, and the quiet one is the safe one: not trusting it leaves
    // everything as it was.
    keyChange?.let { change ->
        AlertDialog(
            onDismissRequest = model::dismissKeyChange,
            title = { Text(stringResource(R.string.key_changed_title)) },
            text = {
                Text(
                    stringResource(
                        R.string.key_changed_body,
                        Targets.formatId(change.target),
                        change.was.take(16),
                        change.now.take(16),
                    ),
                )
            },
            confirmButton = {
                TextButton(onClick = { model.trustChangedKey(change) }) {
                    Text(
                        text = stringResource(R.string.key_changed_trust),
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            },
            dismissButton = {
                TextButton(onClick = model::dismissKeyChange) {
                    Text(stringResource(R.string.key_changed_cancel))
                }
            },
        )
    }

    renamingGroup?.let { was ->
        AlertDialog(
            onDismissRequest = { renamingGroup = null },
            title = {
                Text(stringResource(
                    if (was.isEmpty()) R.string.devices_group_add else R.string.devices_group_rename,
                ))
            },
            text = {
                OutlinedTextField(
                    value = groupName,
                    onValueChange = { groupName = it },
                    label = { Text(stringResource(R.string.devices_group_name)) },
                    singleLine = true,
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    val name = groupName.trim()
                    if (was.isEmpty()) model.store.addGroup(name) else model.store.renameGroup(was, name)
                    renamingGroup = null
                }) {
                    Text(stringResource(R.string.devices_edit_save))
                }
            },
            dismissButton = {
                TextButton(onClick = { renamingGroup = null }) {
                    Text(stringResource(R.string.devices_group_cancel))
                }
            },
        )
    }

    naming?.let { target ->
        DeviceSheet(
            target = target,
            initialAlias = settings.favourites.firstOrNull { it.target.equals(target, true) }?.alias
                ?: settings.recent.firstOrNull { it.id.equals(target, true) }?.name.orEmpty(),
            initialGroup = settings.favourites.firstOrNull { it.target.equals(target, true) }?.group.orEmpty(),
            initialPassword = model.store.rememberedPassword(target).orEmpty(),
            onSave = { alias, group, password ->
                model.store.saveDevice(target, alias, group, password)
                naming = null
            },
            onDismiss = { naming = null },
        )
    }

    asking?.let { target ->
        PasswordSheet(
            target = target,
            offerToRemember = settings.rememberPasswords,
            onDismiss = { asking = null },
            onConnect = { password, remember ->
                asking = null
                if (askingTerminal) {
                    model.openTerminal(target, password, remember)
                } else {
                    model.connect(target, password, remember)
                }
            },
        )
    }
}

@Composable
private fun androidx.compose.foundation.layout.RowScope.Tab(
    tab: Screen,
    current: Screen,
    model: SessionViewModel,
    label: Int,
    selectedIcon: ImageVector,
    icon: ImageVector,
) {
    val selected = tab == current
    NavigationBarItem(
        selected = selected,
        onClick = { model.show(tab) },
        icon = {
            // Filled when it is where you are, outlined when it is somewhere you could go. That is the
            // convention every other app on the phone uses, so it needs no explaining.
            Icon(if (selected) selectedIcon else icon, contentDescription = null)
        },
        label = { Text(stringResource(label)) },
    )
}

/** The settings tab. */
@Composable
private fun SettingsTab(model: SessionViewModel, settings: AppSettings, contentPadding: PaddingValues) {
    SettingsScreen(
        settings = settings,
        version = BuildConfig.VERSION_NAME,
        onChange = model.store::update,
        onForgetAccountData = model.store::forgetAccountData,
        accountController = model.store.account,
        contentPadding = contentPadding,
    )
}
