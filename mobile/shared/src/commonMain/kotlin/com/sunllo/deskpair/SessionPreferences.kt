package com.sunllo.deskpair

import com.sunllo.deskpair.store.AppSettings

/**
 * What the user has asked a session to look like, as one value the session can restate at any time.
 *
 * It exists because of a bug rather than for tidiness. `SessionOptions` is not a patch: the host replaces
 * its whole copy on every update (`CoreHostHandlers.OptionsHandler`) and then re-derives the session's
 * permissions from it. Sending a partial one — as the old `setQuality` did, with three of ten fields —
 * therefore resets everything it leaves out. Turning the picture down mid-session silently handed the
 * host's audio permission back and it started capturing and sending sound the user had declined:
 *
 *     *** [1] PermissionChanged: PermAudio=True
 *     info: DeskPair.Core.Services.AudioService[0] audio: 48000 Hz, 2 ch
 *
 * So there is one object that knows the whole answer, and every update sends all of it. The desktop's
 * `RemoteSessionViewModel.BuildOptions` works the same way and is the model for which fields go where.
 *
 * Only settings the *host* acts on live here. A pointer mode or a zoom-to-fit never leaves the phone, so
 * those stay in the apps.
 */
public data class SessionPreferences(
    val quality: Quality,

    /** Meaningful only with [Quality.CUSTOM]; sent as zero otherwise, as the desktop does. */
    val customBitrateKbps: Int,
    val customFps: Int,

    /**
     * Whether the host should send where its pointer is.
     *
     * Not only a local drawing switch, which is all either app used it for: declining it stops the host
     * publishing cursor positions at all (`HostMediaModule`), so a session that does not draw the pointer
     * no longer pays for one.
     */
    val showRemoteCursor: Boolean,

    /** Sharpen a still picture to pixel-perfect. The host's QoS decides when it can afford to. */
    val losslessRefinement: Boolean,

    /** Video over datagrams. A host whose offer is declined simply keeps sending video on the session. */
    val udpMedia: Boolean,

    /** Whether the host should play its sound down the session at all. */
    val audioEnabled: Boolean,

    /**
     * Watch without touching.
     *
     * `SessionOptions.disable_keyboard`, which the host has always honoured (`HostPolicy`) and no client
     * has ever sent. Useful for handing someone the phone, and for looking at a machine somebody else is
     * working on without taking it out from under them.
     */
    val viewOnly: Boolean,

    /**
     * Lock the remote machine when this session ends.
     *
     * `SessionOptions.lock_after_session_end`, honoured by `HostMediaModule` and, again, never sent from
     * here. The point of it is the unattended desk: disconnecting should not leave someone signed in.
     */
    val lockAfterSessionEnd: Boolean,
) {
    public companion object {
        /**
         * What a session asks for before anything has been stored.
         *
         * A named factory rather than `from(AppSettings())`: a Kotlin data class whose constructor is all
         * defaults exports to Swift as an unavailable `init()`, because the defaults do not cross the
         * bridge. This is the one spelling that works from both languages.
         */
        public fun defaults(): SessionPreferences = from(AppSettings())

        /**
         * The stored settings, as the parts a session cares about.
         *
         * Here rather than in each app so the two cannot disagree about what "balanced" means — and so
         * that a setting added to [AppSettings] has one obvious place to be connected.
         */
        public fun from(settings: AppSettings): SessionPreferences = SessionPreferences(
            quality = when (settings.defaultQuality) {
                "low" -> Quality.LOW
                "best" -> Quality.BEST
                "custom" -> Quality.CUSTOM
                else -> Quality.BALANCED
            },
            customBitrateKbps = settings.customBitrateKbps,
            customFps = settings.customFps,
            showRemoteCursor = settings.showRemoteCursor,
            losslessRefinement = settings.losslessRefinement,
            udpMedia = settings.udpMedia,
            audioEnabled = settings.audioEnabled,
            viewOnly = settings.viewOnly,
            lockAfterSessionEnd = settings.lockAfterSessionEnd,
        )
    }
}
