package com.sunllo.deskpair.store

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import com.sunllo.deskpair.RemoteResolution
import com.sunllo.deskpair.ResolutionMemory
import kotlinx.serialization.json.Json

/**
 * Where the settings file actually lives.
 *
 * The platform supplies this rather than the shared module writing a path itself: Android needs a Context
 * to know its own `filesDir`, and iOS needs the Application Support directory. Both are two lines on their
 * own side and neither is expressible here.
 */
public interface SettingsStorage {
    /** The stored document, or null if there is not one yet. Returning null is normal on first launch. */
    public fun read(): String?

    public fun write(text: String)
}

/** Keeps settings in memory only. Used by tests, and by an app that has not supplied storage yet. */
public class EphemeralSettingsStorage(private var text: String? = null) : SettingsStorage {
    override fun read(): String? = text
    override fun write(text: String) {
        this.text = text
    }
}

/**
 * The settings, loaded once and written back a moment after they change.
 *
 * The delay is the desktop's: `SettingsViewModel.SaveDelay` is 500 ms so that typing a server name is one
 * write rather than one per keystroke. A phone has even less reason to have a Save button than a desktop
 * does, so the same rule applies here and the UI never has to think about it.
 */
public class SettingsStore(
    private val storage: SettingsStorage,
    private val saveDelayMillis: Long = 500,
) {
    // Created here rather than taken as a parameter: Kotlin's default arguments do not cross into Swift,
    // so a constructor parameter would force every iOS call site to build a CoroutineScope by hand.
    private val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private val _settings = MutableStateFlow(read())
    private var pendingSave: Job? = null

    public val settings: StateFlow<AppSettings> = _settings.asStateFlow()

    /**
     * The settings as they stand.
     *
     * A plain property as well as the flow because a generic `StateFlow<AppSettings>` loses its element
     * type crossing into Swift, and a SwiftUI view that has to cast every read is a view that will one day
     * cast wrongly.
     */
    public val current: AppSettings get() = _settings.value

    /** Applies a change and schedules the write. Safe to call on every keystroke. */
    public fun update(transform: (AppSettings) -> AppSettings) {
        val next = transform(_settings.value)
        if (next == _settings.value) {
            return
        }

        _settings.value = next
        pendingSave?.cancel()
        pendingSave = scope.launch {
            delay(saveDelayMillis)
            writeNow(next)
        }
    }

    /**
     * This file as the place a session keeps the resolution chosen for [peer]'s displays. Built per
     * connection so the session never has to know what the person typed.
     */
    public fun resolutionsFor(peer: String): ResolutionMemory = object : ResolutionMemory {
        override fun remembered(display: String): RemoteResolution? =
            current.resolutionFor(peer, display)?.let { RemoteResolution(it.width, it.height, it.scale) }

        override fun remember(display: String, resolution: RemoteResolution?) {
            update {
                it.withPeerResolution(
                    peer,
                    display,
                    resolution?.let { r -> PeerResolution(peer, display, r.width, r.height, r.scale) },
                )
            }
        }
    }

    /**
     * Writes anything outstanding immediately.
     *
     * Called when the app goes to the background: a debounce that is still waiting when the process is
     * killed has quietly lost the change it was holding.
     */
    public fun flush() {
        pendingSave?.cancel()
        pendingSave = null
        writeNow(_settings.value)
    }

    private fun writeNow(value: AppSettings) {
        try {
            storage.write(json.encodeToString(value))
        } catch (e: Exception) {
            // Losing a settings write is not worth taking the app down for; the value stays in memory and
            // the next change tries again.
            lastError = e.message
        }
    }

    /** Why the last write failed, for a settings screen that wants to say so. Null when all is well. */
    public var lastError: String? = null
        private set

    private fun read(): AppSettings {
        val text = try {
            storage.read()
        } catch (_: Exception) {
            null
        } ?: return AppSettings()

        return try {
            json.decodeFromString<AppSettings>(text)
        } catch (_: Exception) {
            // A corrupt file must not stop the app starting, which is why DesktopConfig.Load swallows the
            // same three exceptions and hands back defaults.
            AppSettings()
        }
    }

    internal companion object {
        /**
         * `ignoreUnknownKeys` lets an older build read a newer file, and leaving defaults out keeps the
         * file to what was actually chosen — the same shape `DefaultIgnoreCondition.WhenWritingDefault`
         * gives `desktop.json`.
         */
        val json: Json = Json {
            ignoreUnknownKeys = true
            encodeDefaults = false
            prettyPrint = true
        }
    }
}
