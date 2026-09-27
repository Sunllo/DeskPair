package com.sunllo.deskpair.android

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.outlined.VolumeUp
import androidx.compose.material.icons.filled.AccountCircle
import androidx.compose.material.icons.outlined.AccountCircle
import androidx.compose.material.icons.outlined.AutoAwesome
import androidx.compose.material.icons.outlined.Badge
import androidx.compose.material.icons.outlined.Bolt
import androidx.compose.material.icons.outlined.Cloud
import androidx.compose.material.icons.outlined.CloudDownload
import androidx.compose.material.icons.outlined.DeleteSweep
import androidx.compose.material.icons.outlined.Dns
import androidx.compose.material.icons.outlined.Email
import androidx.compose.material.icons.outlined.Fullscreen
import androidx.compose.material.icons.outlined.HighQuality
import androidx.compose.material.icons.outlined.History
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Key
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material.icons.outlined.Link
import androidx.compose.material.icons.outlined.LinkOff
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Login
import androidx.compose.material.icons.outlined.Logout
import androidx.compose.material.icons.outlined.Mouse
import androidx.compose.material.icons.outlined.Password
import androidx.compose.material.icons.outlined.PersonAdd
import androidx.compose.material.icons.outlined.Pin
import androidx.compose.material.icons.outlined.Route
import androidx.compose.material.icons.outlined.Smartphone
import androidx.compose.material.icons.outlined.Speed
import androidx.compose.material.icons.outlined.TouchApp
import androidx.compose.material.icons.outlined.VerifiedUser
import androidx.compose.material.icons.outlined.Videocam
import androidx.compose.material.icons.outlined.Visibility
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.runtime.Composable
import androidx.core.os.LocaleListCompat
import com.sunllo.deskpair.store.AppLanguages
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sunllo.deskpair.account.AccountController
import com.sunllo.deskpair.account.AccountMessage
import com.sunllo.deskpair.account.AccountUiState
import com.sunllo.deskpair.android.ui.ActionRow
import com.sunllo.deskpair.android.ui.CardFooter
import com.sunllo.deskpair.android.ui.FieldRow
import com.sunllo.deskpair.android.ui.InfoRow
import com.sunllo.deskpair.android.ui.InsetCard
import com.sunllo.deskpair.android.ui.NumberRow
import com.sunllo.deskpair.android.ui.OptionRow
import com.sunllo.deskpair.android.ui.RowDivider
import com.sunllo.deskpair.android.ui.SectionHeader
import com.sunllo.deskpair.android.ui.ToggleRow
import com.sunllo.deskpair.android.ui.ValueRow
import com.sunllo.deskpair.crypto.PinnedHost
import com.sunllo.deskpair.store.AppSettings
import com.sunllo.deskpair.store.ServerKey
import com.sunllo.deskpair.store.Targets
import kotlinx.coroutines.launch

/**
 * Settings, in five sections rather than the desktop's seven.
 *
 * Five of the desktop's tabs are `HostConfig` — approve mode, temporary passwords, direct-access ports,
 * capture permissions — and a controller-only client has no host config at all. What is left is what this
 * phone decides for itself, plus the two lists that are the only reason a security section exists here:
 * what it trusts, and what it holds a password for.
 *
 * Saved as typed, debounced in [com.sunllo.deskpair.store.SettingsStore] the way `SettingsViewModel` does
 * it. There is no Save button and no restart banner: nothing here is baked in at start-up the way the
 * desktop's engine settings are.
 *
 * This was the worst screen in the app: one flat scrolling Column with no grouping but a horizontal rule,
 * no icons, labels that were whole sentences and wrapped, current values as accent-coloured text floating
 * on the right, section headings in the accent colour, and a bordered form field for every number. It is
 * now cards of rows — see `ui/Rows.kt` for the four shapes a row can take.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    settings: AppSettings,
    version: String,
    onChange: ((AppSettings) -> AppSettings) -> Unit,
    /// Empties the device list, which belongs to the account rather than to this phone.
    onForgetAccountData: () -> Unit,
    accountController: AccountController,
    contentPadding: PaddingValues,
) {
    val scope = rememberCoroutineScope()
    var notice by remember { mutableStateOf<String?>(null) }

    // Kept on screen even when it will not be stored, so a half-pasted key is visible rather than silently
    // discarded — the same thing `StorableKey` does on the desktop.
    var keyText by remember(settings.serverPublicKeyBase64) {
        mutableStateOf(settings.serverPublicKeyBase64)
    }
    var fetching by remember { mutableStateOf(false) }

    val serverRequired = stringResource(R.string.settings_server_required)
    val fetched = stringResource(R.string.settings_key_fetched)

    // The account card. The code is never stored -- it is spent the moment it is used -- and the name
    // defaults to something recognisable rather than to nothing.
    val account by accountController.state.collectAsStateWithLifecycle()
    val accountNotice = accountMessage(account)
    var accountOpen by remember { mutableStateOf(false) }
    var email by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }

    // What this handset goes by in an account's device list. A setting now, so it can be corrected later
    // and seen afterwards; the model name stands in until somebody types something.
    val deviceName = settings.deviceName.ifBlank { android.os.Build.MODEL ?: "Android" }

    // What the app resolved to, after the user's own language setting and the system's list.
    val language = androidx.compose.ui.platform.LocalConfiguration.current.locales[0]?.toLanguageTag() ?: "en"

    // Says whether the link is still good, without blocking the screen on it.
    LaunchedEffect(Unit) {
        accountController.refresh()
    }

    // And what it will let the app offer. Quiet on failure; the sheet works either way.
    LaunchedEffect(accountOpen) {
        if (accountOpen) {
            accountController.loadOptions(settings.portalServer)
        }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        SettingsBar(contentPadding)

        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(bottom = 24.dp + contentPadding.calculateBottomPadding()),
        ) {
            // The account comes first, and it says what an account says: who you are and how many
            // machines are saved. What used to be here instead was the machinery of linking -- a portal
            // address, a name for this handset, an eight-character code -- none of which is a fact about
            // an account, and all of which is typed once and never looked at again. It is behind this row.
            item {
                Card(top = 8.dp) {
                    AccountRow(
                        title = if (account.link.isLinked) {
                            account.link.account
                        } else {
                            stringResource(R.string.settings_account_signed_out)
                        },
                        detail = if (account.link.isLinked) {
                            stringResource(R.string.settings_account_devices, settings.favourites.size)
                        } else {
                            stringResource(R.string.settings_account_signed_out_hint)
                        },
                        linked = account.link.isLinked,
                        onClick = { accountOpen = true },
                    )
                }
            }

            item { SectionHeader(stringResource(R.string.settings_section_general)) }
            item {
                Card {
                    // First, because it is the one thing on this screen that is about this handset rather
                    // than about how it behaves -- and because it used to be collected in the middle of
                    // signing in, which is the one moment nobody is thinking about what to call their phone.
                    FieldRow(
                        icon = Icons.Outlined.Smartphone,
                        label = stringResource(R.string.settings_device_name),
                        value = settings.deviceName,
                        onValue = { value -> onChange { it.copy(deviceName = value) } },
                        hint = deviceName,
                    )
                    RowDivider()
                    Choice(
                        icon = Icons.Outlined.Language,
                        label = stringResource(R.string.settings_language),
                        options = listOf(stringResource(R.string.settings_language_system) to AppLanguages.SYSTEM) +
                            AppLanguages.all.map { it.nativeName to it.code },
                        selected = settings.language,
                        onSelect = { value ->
                            onChange { it.copy(language = value) }
                            // Applied, not merely remembered: this used to be stored and read by nothing.
                            // AppCompat recreates the activity in the new language and keeps the choice
                            // where Android 13's own per-app language setting keeps it.
                            AppCompatDelegate.setApplicationLocales(
                                if (value == AppLanguages.SYSTEM) LocaleListCompat.getEmptyLocaleList() else LocaleListCompat.forLanguageTags(value),
                            )
                        },
                    )
                    RowDivider()
                    NumberRow(
                        icon = Icons.Outlined.History,
                        label = stringResource(R.string.settings_recent_limit),
                        hint = stringResource(R.string.settings_recent_limit_hint),
                        value = settings.recentLimit,
                        range = 1..200,
                        invalid = stringResource(R.string.settings_recent_limit_invalid),
                        onValue = { value -> onChange { it.copy(recentLimit = value) } },
                    )

                    if (settings.recent.isNotEmpty()) {
                        RowDivider()
                        ActionRow(
                            icon = Icons.Outlined.DeleteSweep,
                            label = stringResource(R.string.settings_clear_recent),
                            destructive = true,
                            onClick = { onChange { it.copy(recent = emptyList()) } },
                        )
                    }
                }
            }

            item { SectionHeader(stringResource(R.string.settings_section_session)) }
            item {
                Card {
                    Choice(
                        icon = Icons.Outlined.HighQuality,
                        label = stringResource(R.string.settings_default_quality),
                        hint = stringResource(R.string.settings_default_quality_hint),
                        options = listOf(
                            stringResource(R.string.quality_low) to "low",
                            stringResource(R.string.quality_balanced) to "balanced",
                            stringResource(R.string.quality_best) to "best",
                            stringResource(R.string.settings_quality_custom) to "custom",
                        ),
                        selected = settings.defaultQuality,
                        onSelect = { value -> onChange { it.copy(defaultQuality = value) } },
                    )

                    if (settings.defaultQuality == "custom") {
                        RowDivider()
                        NumberRow(
                            icon = Icons.Outlined.Speed,
                            label = stringResource(R.string.settings_bitrate),
                            value = settings.customBitrateKbps,
                            range = 100..200_000,
                            invalid = stringResource(R.string.settings_bitrate_invalid),
                            onValue = { value -> onChange { it.copy(customBitrateKbps = value) } },
                        )
                        RowDivider()
                        NumberRow(
                            icon = Icons.Outlined.Videocam,
                            label = stringResource(R.string.settings_fps),
                            value = settings.customFps,
                            range = 5..120,
                            invalid = stringResource(R.string.settings_fps_invalid),
                            onValue = { value -> onChange { it.copy(customFps = value) } },
                        )
                    }

                    RowDivider()
                    Choice(
                        icon = Icons.Outlined.TouchApp,
                        label = stringResource(R.string.settings_pointer_mode),
                        options = listOf(
                            stringResource(R.string.mode_touch) to "touch",
                            stringResource(R.string.mode_mouse) to "mouse",
                        ),
                        selected = settings.pointerMode,
                        onSelect = { value -> onChange { it.copy(pointerMode = value) } },
                    )
                }
            }

            item {
                Card(top = 12.dp) {
                    ToggleRow(
                        icon = Icons.Outlined.Mouse,
                        label = stringResource(R.string.session_remote_cursor),
                        hint = stringResource(R.string.session_remote_cursor_hint),
                        checked = settings.showRemoteCursor,
                        onChange = { on -> onChange { it.copy(showRemoteCursor = on) } },
                    )
                    RowDivider()
                    ToggleRow(
                        icon = Icons.Outlined.Fullscreen,
                        label = stringResource(R.string.settings_fit_to_window),
                        hint = stringResource(R.string.settings_fit_to_window_hint),
                        checked = settings.fitToWindow,
                        onChange = { on -> onChange { it.copy(fitToWindow = on) } },
                    )
                    RowDivider()
                    ToggleRow(
                        icon = Icons.Outlined.AutoAwesome,
                        label = stringResource(R.string.settings_lossless),
                        hint = stringResource(R.string.settings_lossless_hint),
                        checked = settings.losslessRefinement,
                        onChange = { on -> onChange { it.copy(losslessRefinement = on) } },
                    )
                    RowDivider()
                    ToggleRow(
                        icon = Icons.AutoMirrored.Outlined.VolumeUp,
                        label = stringResource(R.string.settings_audio),
                        hint = stringResource(R.string.settings_audio_hint),
                        checked = settings.audioEnabled,
                        onChange = { on -> onChange { it.copy(audioEnabled = on) } },
                    )
                    RowDivider()
                    // Both of these the host has always honoured and no client here has ever asked for.
                    ToggleRow(
                        icon = Icons.Outlined.Visibility,
                        label = stringResource(R.string.settings_view_only),
                        hint = stringResource(R.string.settings_view_only_hint),
                        checked = settings.viewOnly,
                        onChange = { on -> onChange { it.copy(viewOnly = on) } },
                    )
                    RowDivider()
                    ToggleRow(
                        icon = Icons.Outlined.Lock,
                        label = stringResource(R.string.settings_lock_after_end),
                        hint = stringResource(R.string.settings_lock_after_end_hint),
                        checked = settings.lockAfterSessionEnd,
                        onChange = { on -> onChange { it.copy(lockAfterSessionEnd = on) } },
                    )
                }
            }

            item { SectionHeader(stringResource(R.string.settings_section_network)) }
            item {
                Card {
                    // Off, there is nothing here about where the servers are, because there is nothing
                    // here for that person to decide. On, the two fields below are theirs.
                    ToggleRow(
                        icon = Icons.Outlined.Dns,
                        label = stringResource(R.string.settings_self_hosted),
                        hint = if (settings.useDirectoryServers) {
                            stringResource(R.string.settings_directory_hint)
                        } else {
                            null
                        },
                        checked = !settings.useDirectoryServers,
                        onChange = { own ->
                            onChange {
                                // Clearing on the way back matters: a stored address would silently win
                                // over whatever the portal answers, from a field no longer on screen.
                                if (own) {
                                    it.copy(useDirectoryServers = false)
                                } else {
                                    it.copy(
                                        useDirectoryServers = true,
                                        rendezvousServer = "",
                                        serverPublicKeyBase64 = "",
                                    )
                                }
                            }
                        },
                    )
                }
            }

            if (!settings.useDirectoryServers) {
                item {
                Card {
                    FieldRow(
                        icon = Icons.Outlined.Dns,
                        label = stringResource(R.string.settings_server),
                        value = settings.rendezvousServer,
                        onValue = { value -> onChange { it.copy(rendezvousServer = value.trim()) } },
                    )
                    RowDivider()
                    FieldRow(
                        icon = Icons.Outlined.Key,
                        label = stringResource(R.string.settings_server_key),
                        value = keyText,
                        onValue = { value ->
                            keyText = value
                            // Only a key that could be used is written; a half-pasted one stays on screen.
                            if (ServerKey.isValid(value)) {
                                onChange { it.copy(serverPublicKeyBase64 = ServerKey.clean(value)) }
                            }
                        },
                        isError = keyText.isNotEmpty() && !ServerKey.isValid(keyText),
                        monospaced = true,
                        hint = when {
                            keyText.isNotEmpty() && !ServerKey.isValid(keyText) ->
                                stringResource(R.string.settings_key_invalid)
                            keyText.isEmpty() -> stringResource(R.string.settings_key_missing)
                            else -> null
                        },
                    )
                    RowDivider()
                    // Here rather than on the account sheet. It is a server address, like the one above it,
                    // and the only thing a person signing in wants to see is where to type their email. It
                    // stays at all because the front page says every server can be your own, and this is
                    // where somebody who took that up points the app at theirs.
                    FieldRow(
                        icon = Icons.Outlined.Cloud,
                        label = stringResource(R.string.settings_portal),
                        value = settings.portalServer,
                        onValue = { value -> onChange { it.copy(portalServer = value.trim()) } },
                        hint = stringResource(R.string.settings_portal_hint),
                    )
                    RowDivider()
                    // Close to mandatory on a phone: nobody is pasting a ninety-one byte SPKI with thumbs.
                    ActionRow(
                        icon = Icons.Outlined.CloudDownload,
                        label = stringResource(R.string.settings_fetch_key),
                        onClick = {
                            if (fetching) {
                                return@ActionRow
                            }

                            val server = settings.rendezvousServer
                            if (server.isEmpty()) {
                                notice = serverRequired
                                return@ActionRow
                            }

                            fetching = true
                            notice = null
                            scope.launch {
                                try {
                                    val key = ServerKey.fetch(server)
                                    keyText = key
                                    onChange { it.copy(serverPublicKeyBase64 = key) }
                                    notice = fetched
                                } catch (e: Throwable) {
                                    notice = e.message
                                } finally {
                                    fetching = false
                                }
                            }
                        },
                    )
                }

                // Said where the button is. A notice at the top of a column the user has scrolled past is
                // the same as no notice at all, which is exactly how the first cleartext failure was missed.
                notice?.let {
                    Text(
                        text = it,
                        style = MaterialTheme.typography.bodySmall,
                        color = if (it == fetched) {
                            MaterialTheme.colorScheme.primary
                        } else {
                            MaterialTheme.colorScheme.error
                        },
                        modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 8.dp),
                    )
                }
                }
            }

            item {
                Card(top = 12.dp) {
                    ToggleRow(
                        icon = Icons.Outlined.Bolt,
                        label = stringResource(R.string.settings_udp_media),
                        hint = stringResource(R.string.settings_udp_media_hint),
                        checked = settings.udpMedia,
                        onChange = { on -> onChange { it.copy(udpMedia = on) } },
                    )
                    RowDivider()
                    ToggleRow(
                        icon = Icons.Outlined.Route,
                        label = stringResource(R.string.settings_force_relay),
                        hint = stringResource(R.string.settings_force_relay_hint),
                        checked = settings.forceRelay,
                        onChange = { on -> onChange { it.copy(forceRelay = on) } },
                    )
                }
            }

            // The trusted computers and the remembered passwords used to be listed here.
            //
            // Passwords belong to the machine they open, and are edited on it now, in the device list.
            // Pinned keys were only ever here as a way out of one situation -- a host reinstalled, so its
            // key no longer matches and every connection is refused -- and a list of fingerprints is a
            // strange place to solve that. It is solved where it happens: the connection that fails says
            // the key changed and offers to trust the new one.

            item { SectionHeader(stringResource(R.string.settings_section_about)) }
            item {
                Card {
                    // No path on screen. Where this app keeps its settings is not something anybody
                    // reading this screen needs, and on a phone it is not even somewhere they can go.
                    InfoRow(
                        icon = Icons.Outlined.Info,
                        label = stringResource(R.string.settings_version, version),
                    )
                }
            }
        }
    }

    if (accountOpen) {
        val sheet = rememberModalBottomSheetState()
        ModalBottomSheet(onDismissRequest = { accountOpen = false }, sheetState = sheet) {
            SectionHeader(stringResource(R.string.settings_section_account))
            Card {
                if (account.link.isLinked) {
                    InfoRow(
                        icon = Icons.Outlined.AccountCircle,
                        label = stringResource(R.string.settings_linked_to),
                        value = account.link.account,
                        detail = account.link.alias.ifEmpty { null },
                    )
                    RowDivider()
                    ActionRow(
                        icon = Icons.Outlined.Logout,
                        label = stringResource(R.string.settings_unlink),
                        destructive = true,
                        enabled = !account.busy,
                        onClick = {
                            scope.launch {
                                accountController.unlink()
                                // The list goes with the account it belonged to.
                                onForgetAccountData()
                            }
                        },
                    )
                } else {
                    // An address and a password, which is what somebody expects to be asked for. Nothing
                    // else: the portal's address is a server setting and lives with the other one, and the
                    // name this handset goes by is a setting too rather than something typed here once.
                    FieldRow(
                        icon = Icons.Outlined.Email,
                        label = stringResource(R.string.settings_account_email),
                        value = email,
                        onValue = { email = it },
                    )
                    RowDivider()
                    FieldRow(
                        icon = Icons.Outlined.Key,
                        label = stringResource(R.string.settings_account_password),
                        value = password,
                        onValue = { password = it },
                        secret = true,
                    )
                    RowDivider()
                    ActionRow(
                        icon = Icons.Outlined.Login,
                        label = stringResource(R.string.settings_account_sign_in),
                        enabled = !account.busy,
                        onClick = {
                            scope.launch {
                                // Whatever is on this phone belonged to whoever was signed in last. The
                                // list that matters comes down from the portal a moment later.
                                onForgetAccountData()
                                accountController.signIn(
                                    settings.portalServer, email, password, deviceName)
                                if (accountController.state.value.link.isLinked) {
                                    password = ""
                                    accountOpen = false
                                }
                            }
                        },
                    )

                    if (account.selfRegistration) {
                        RowDivider()
                        ActionRow(
                            icon = Icons.Outlined.PersonAdd,
                            label = stringResource(R.string.settings_account_register),
                            enabled = !account.busy,
                            onClick = {
                                scope.launch {
                                    onForgetAccountData()
                                    accountController.register(
                                        settings.portalServer,
                                        email,
                                        password,
                                        "",
                                        deviceName,
                                        // The language this app is running in, not the phone's region:
                                        // somebody reading DeskPair in English should not be mailed in
                                        // Chinese because of where they live.
                                        language,
                                    )
                                    if (accountController.state.value.link.isLinked) {
                                        password = ""
                                        accountOpen = false
                                    }
                                }
                            },
                        )
                    }
                }
            }

            accountNotice?.let {
                Text(
                    text = it,
                    style = MaterialTheme.typography.bodySmall,
                    color = if (account.isGood) {
                        MaterialTheme.colorScheme.primary
                    } else {
                        MaterialTheme.colorScheme.error
                    },
                    modifier = Modifier.padding(horizontal = 28.dp, vertical = 8.dp),
                )
            }

            Spacer(Modifier.height(24.dp))
        }
    }
}

/**
 * Who this install belongs to, as the first thing on the settings screen.
 *
 * Taller than the rows under it and drawn with a mark rather than a line icon, because it is a person
 * rather than a setting, and because it is the one row here somebody is looking for rather than reading
 * past.
 */
@Composable
private fun AccountRow(title: String, detail: String, linked: Boolean, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            imageVector = if (linked) Icons.Filled.AccountCircle else Icons.Outlined.AccountCircle,
            contentDescription = null,
            tint = if (linked) {
                MaterialTheme.colorScheme.primary
            } else {
                MaterialTheme.colorScheme.onSurfaceVariant
            },
            modifier = Modifier.size(44.dp),
        )
        Column(modifier = Modifier.weight(1f).padding(start = 16.dp)) {
            Text(
                text = title,
                style = MaterialTheme.typography.titleMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                text = detail,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Icon(
            imageVector = Icons.AutoMirrored.Outlined.KeyboardArrowRight,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/** The card every section is made of, indented from the page edge. */
@Composable
private fun Card(top: Dp = 0.dp, content: @Composable ColumnScope.() -> Unit) {
    InsetCard(modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = top), content = content)
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SettingsBar(contentPadding: PaddingValues) {
    TopAppBar(
        title = { Text(stringResource(R.string.settings_title)) },
        // The scaffold below already owns the insets; taking them again here would double the gap.
        windowInsets = WindowInsets(0, 0, 0, 0),
        colors = TopAppBarDefaults.topAppBarColors(
            containerColor = MaterialTheme.colorScheme.background,
        ),
        modifier = Modifier
            .fillMaxWidth()
            .padding(top = contentPadding.calculateTopPadding()),
    )
}

/**
 * A choice, opened as a sheet rather than a menu.
 *
 * A DropdownMenu anchored to a TextButton on the right of a row is fiddly on a touchscreen and puts the
 * options wherever the row happens to be. A sheet comes up from the bottom, where the thumb already is, and
 * shows every option at full width with the current one marked.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun Choice(
    icon: ImageVector,
    label: String,
    hint: String? = null,
    options: List<Pair<String, String>>,
    selected: String,
    onSelect: (String) -> Unit,
) {
    var open by remember { mutableStateOf(false) }
    val shown = options.firstOrNull { it.second == selected }?.first ?: selected

    ValueRow(icon = icon, label = label, hint = hint, value = shown, onClick = { open = true })

    if (open) {
        val state = rememberModalBottomSheetState()
        ModalBottomSheet(onDismissRequest = { open = false }, sheetState = state) {
            SectionHeader(label)
            options.forEach { (text, value) ->
                OptionRow(
                    label = text,
                    selected = value == selected,
                    onClick = {
                        open = false
                        onSelect(value)
                    },
                )
            }
            Spacer(Modifier.height(24.dp))
        }
    }
}

/**
 * Turns what the shared controller reports into a sentence in the user's language.
 *
 * The shared layer has no string table, so it reports an [AccountMessage] instead of English. The one
 * exception is [AccountMessage.PORTAL_SAID], where the portal's own words are passed through: inventing a
 * case per server error would leave the app mute about any error it had not been taught.
 */
@Composable
private fun accountMessage(state: AccountUiState): String? = when (state.message) {
    AccountMessage.NONE -> null
    // Unreachable from this app now that the code field is gone; the shared layer still reports it
    // because the desktop still has that route.
    AccountMessage.CODE_REQUIRED -> null
    AccountMessage.EMAIL_REQUIRED -> stringResource(R.string.settings_account_email_required)
    AccountMessage.PASSWORD_REQUIRED -> stringResource(R.string.settings_account_password_required)
    AccountMessage.VERIFY_EMAIL -> stringResource(R.string.settings_account_verify, state.detail)
    AccountMessage.BAD_CREDENTIALS -> stringResource(R.string.account_error_bad_credentials)
    AccountMessage.NOT_VERIFIED -> stringResource(R.string.account_error_not_verified)
    AccountMessage.DISABLED -> stringResource(R.string.account_error_disabled)
    AccountMessage.REGISTRATION_CLOSED -> stringResource(R.string.account_error_closed)
    AccountMessage.EMAIL_TAKEN -> stringResource(R.string.account_error_email_taken)
    AccountMessage.WEAK_PASSWORD -> stringResource(R.string.account_error_weak_password)
    AccountMessage.BAD_EMAIL -> stringResource(R.string.account_error_bad_email)
    AccountMessage.TOO_MANY_ATTEMPTS -> stringResource(R.string.account_error_too_many)
    AccountMessage.LINKED -> stringResource(R.string.settings_linked, state.detail)
    AccountMessage.UNLINKED -> stringResource(R.string.settings_unlinked)
    AccountMessage.UNLINKED_LOCALLY -> stringResource(R.string.settings_unlinked_locally, state.detail)
    AccountMessage.PORTAL_SAID -> state.detail.ifEmpty { null }
}
