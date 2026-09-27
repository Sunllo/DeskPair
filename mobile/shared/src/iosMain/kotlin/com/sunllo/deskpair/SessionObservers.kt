package com.sunllo.deskpair

import com.sunllo.deskpair.terminal.RemoteTerminal
import com.sunllo.deskpair.terminal.TerminalSnapshot
import com.sunllo.deskpair.terminal.TerminalStatus
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch

/**
 * StateFlows, as callbacks Swift can actually use.
 *
 * A Kotlin flow does not reach Swift as an AsyncSequence — the generated header exposes a `collect` that
 * takes a collector object, which is unpleasant to write at every call site and impossible to write at
 * all inside a SwiftUI view. One small bridge here is cheaper than that, and it keeps the cancellation in
 * one place: the returned handle stops the observation, and the app stops it when the session ends.
 */
public class Observation internal constructor(private val scope: CoroutineScope) {
    public fun stop() {
        scope.cancel()
    }
}

/**
 * An object rather than extension functions, because an extension's exported name depends on where the
 * compiler decides to put it and a Swift call site should not have to guess.
 */
public object SessionObservers {

    /** Calls [onEach] whenever the media path changes, starting with what it is now. */
    public fun mediaPath(session: RemoteSession, onEach: (String?) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { session.mediaPath.collect { onEach(it) } }
        return Observation(scope)
    }

    /** Calls [onEach] whenever the session's state changes, starting with what it is now. */
    public fun state(session: RemoteSession, onEach: (SessionState) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { session.state.collect { onEach(it) } }
        return Observation(scope)
    }

    /** Calls [onEach] with the host's round-trip estimate as it arrives. */
    public fun roundTrip(session: RemoteSession, onEach: (Int?) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { session.roundTripMillis.collect { onEach(it) } }
        return Observation(scope)
    }

    /** Calls [onEach] with what the host allows, starting with what it allows now. */
    public fun permissions(session: RemoteSession, onEach: (HostPermissions) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { session.permissions.collect { onEach(it) } }
        return Observation(scope)
    }

    /** Calls [onEach] with the display the host confirmed, not the one that was asked for. */
    public fun display(session: RemoteSession, onEach: (Int) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { session.display.collect { onEach(it) } }
        return Observation(scope)
    }

    /** Calls [onEach] with the host's displays as they are now, starting with what they are at login. */
    public fun displays(session: RemoteSession, onEach: (List<RemoteDisplay>) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { session.displays.collect { onEach(it) } }
        return Observation(scope)
    }

    /** Calls [onEach] with why the last resolution request was refused, and with null once it is cleared. */
    public fun resolutionFailure(session: RemoteSession, onEach: (String?) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { session.resolutionFailure.collect { onEach(it) } }
        return Observation(scope)
    }

    /** Calls [onEach] with the host's word on its displays, and with null once it has none. */
    public fun hostNotice(session: RemoteSession, onEach: (String?) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { session.hostNotice.collect { onEach(it) } }
        return Observation(scope)
    }

    /**
     * Calls [onEach] when the picture goes stale, and again when it recovers.
     *
     * Both values matter. The app used to infer this from the session having failed, which is a different
     * thing and has no way back — so the warning, once shown, stayed up over a healthy reconnection.
     */
    public fun stalled(session: RemoteSession, onEach: (Boolean) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { session.stalled.collect { onEach(it) } }
        return Observation(scope)
    }

    /** Calls [onEach] with what a terminal shows, at most once a frame, starting with what it shows now. */
    public fun terminalSnapshot(terminal: RemoteTerminal, onEach: (TerminalSnapshot) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { terminal.snapshot.collect { onEach(it) } }
        return Observation(scope)
    }

    /** Calls [onEach] as a terminal's shell opens, ends or fails, and when its connection closes. */
    public fun terminalStatus(terminal: RemoteTerminal, onEach: (TerminalStatus) -> Unit): Observation {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
        scope.launch { terminal.status.collect { onEach(it) } }
        return Observation(scope)
    }
}
