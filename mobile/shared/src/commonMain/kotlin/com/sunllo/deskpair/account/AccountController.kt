package com.sunllo.deskpair.account

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * What the account screen has to say, as a thing rather than as a sentence.
 *
 * An enum because the shared layer has no string table. Connection failures in `RemoteSession` are English
 * literals in common code and have been flagged as a gap for a while; this is new code and there is no
 * reason for it to join them. Each app maps these to its own resources, so the screen is translated even
 * though the logic is shared.
 *
 * [AccountMessage.PORTAL_SAID] is the exception, and deliberately: the portal's own errors already arrive as
 * sentences, and inventing an enum case per server error would leave the app unable to report one it had
 * never heard of.
 */
public enum class AccountMessage {
    NONE,
    CODE_REQUIRED,
    EMAIL_REQUIRED,
    PASSWORD_REQUIRED,

    /** The account was made, and the portal wants its address confirmed before it can be used. */
    VERIFY_EMAIL,
    LINKED,
    UNLINKED,
    UNLINKED_LOCALLY,

    // ---- what the portal refused, as a thing rather than as its English sentence ----
    //
    // The portal answers in one language, and it is not always the reader's. Every refusal a person can
    // do something about is a case here, so the app can say it in theirs; PORTAL_SAID stays for the ones
    // nobody anticipated, because being mute about an error is worse than being mute in English.

    /** The address and the password did not match -- or there is no such account, which answers the same. */
    BAD_CREDENTIALS,

    /** The address exists but has not been confirmed yet. */
    NOT_VERIFIED,

    /** The account has been turned off. */
    DISABLED,

    /** This portal does not take new accounts. */
    REGISTRATION_CLOSED,

    /** That address already has an account. */
    EMAIL_TAKEN,

    /** The password is too short for this portal. */
    WEAK_PASSWORD,

    /** That is not an address. */
    BAD_EMAIL,

    /** Too many attempts, too quickly. */
    TOO_MANY_ATTEMPTS,

    PORTAL_SAID,
}

/**
 * What the account screen shows.
 *
 * [detail] carries whatever the message needs: the account's address for [AccountMessage.LINKED], the
 * portal's own words for the other two.
 */
public data class AccountUiState(
    val link: LinkState = LinkState.UNLINKED,
    val busy: Boolean = false,
    val message: AccountMessage = AccountMessage.NONE,
    val detail: String = "",
    /**
     * Whether this portal takes new accounts.
     *
     * False until asked, so a screen that has not heard back offers to sign in and nothing else. Offering
     * to create an account and then being refused is worse than not offering, and a self-hoster who turned
     * sign-ups off should not have to explain the button to anybody.
     */
    val selfRegistration: Boolean = false,
) {
    public val isGood: Boolean
        get() = message == AccountMessage.LINKED ||
            message == AccountMessage.UNLINKED ||
            message == AccountMessage.VERIFY_EMAIL
}

/**
 * The account screen's logic, shared by both apps.
 *
 * Here rather than twice, because these two settings screens have drifted before: the key bar reached
 * twenty-seven keys of eighty partly because each app owned its own idea of what was on it. This is smaller
 * and the same trap, and a device that behaves differently on a phone from on a tablet is not something a
 * user would ever think to suspect.
 *
 * The UI supplies the coroutine scope: Android has `viewModelScope`, iOS has a `Task`, and neither belongs
 * in shared code.
 */
public class AccountController(private val link: AccountLink) {

    private val _state = MutableStateFlow(AccountUiState(link = link.current()))

    public val state: StateFlow<AccountUiState> = _state.asStateFlow()

    /** The current state without collecting a flow: a generic `StateFlow` loses its element type in Swift. */
    public val snapshot: AccountUiState get() = _state.value

    /** What is stored, with no network call. Safe offline, and what the screen opens with. */
    public fun reload() {
        _state.value = _state.value.copy(link = link.current())
    }

    /**
     * Asks the portal whether this device is still linked, and what it may sync.
     *
     * A failure leaves the stored link on screen rather than claiming the device is unlinked: the portal
     * being unreachable and the device being revoked are different things, and reporting the second when
     * only the first happened would send someone to re-link a device that was fine.
     */
    public suspend fun refresh() {
        if (!_state.value.link.isLinked) {
            return
        }

        guarded {
            _state.value = _state.value.copy(link = link.refresh())
        }
    }

    public suspend fun link(portalUrl: String, code: String, alias: String) {
        if (DeviceLink.normalise(code).length != DeviceLink.CODE_LENGTH) {
            _state.value = _state.value.copy(message = AccountMessage.CODE_REQUIRED, detail = "")
            return
        }

        guarded {
            val state = link.link(portalUrl, code, alias)
            _state.value = AccountUiState(link = state, message = AccountMessage.LINKED, detail = state.account)
        }
    }

    /**
     * Signs in with an email and a password, and links this device.
     *
     * The address and the password are arguments and nothing else: neither is kept, here or anywhere below
     * this. What survives is the device token, the same one a code from the website produces.
     */
    public suspend fun signIn(portalUrl: String, email: String, password: String, alias: String) {
        if (email.isBlank()) {
            _state.value = _state.value.copy(message = AccountMessage.EMAIL_REQUIRED, detail = "")
            return
        }

        if (password.isEmpty()) {
            _state.value = _state.value.copy(message = AccountMessage.PASSWORD_REQUIRED, detail = "")
            return
        }

        guarded {
            val state = link.signIn(portalUrl, email, password, alias)
            _state.value = AccountUiState(link = state, message = AccountMessage.LINKED, detail = state.account)
        }
    }

    /** Makes an account and links this device, or says to go and confirm the address first. */
    public suspend fun register(
        portalUrl: String,
        email: String,
        password: String,
        name: String,
        alias: String,
        /** What to write to this person in, which only the app knows. Kept on the account by the portal. */
        language: String,
    ) {
        if (email.isBlank()) {
            _state.value = _state.value.copy(message = AccountMessage.EMAIL_REQUIRED, detail = "")
            return
        }

        if (password.isEmpty()) {
            _state.value = _state.value.copy(message = AccountMessage.PASSWORD_REQUIRED, detail = "")
            return
        }

        guarded {
            val state = link.register(portalUrl, email, password, name, alias, language)
            _state.value = if (state.isLinked) {
                AccountUiState(link = state, message = AccountMessage.LINKED, detail = state.account)
            } else {
                AccountUiState(message = AccountMessage.VERIFY_EMAIL, detail = email.trim())
            }
        }
    }

    /**
     * Asks the portal what it will let an app do. Quiet on failure: a screen that cannot reach the portal
     * has bigger news to give than which buttons it would have shown.
     */
    public suspend fun loadOptions(portalUrl: String) {
        val allowed = try {
            link.options(portalUrl).selfRegistration
        } catch (_: PortalException) {
            return
        }

        _state.value = _state.value.copy(selfRegistration = allowed)
    }

    public suspend fun unlink() {
        guarded {
            val failure = link.unlink()
            _state.value = AccountUiState(
                link = LinkState.UNLINKED,
                // The local token is gone either way. Say so plainly when the portal was not told, so the
                // person knows to revoke it from the console as well.
                message = if (failure == null) AccountMessage.UNLINKED else AccountMessage.UNLINKED_LOCALLY,
                detail = failure?.message ?: "",
            )
        }
    }

    /**
     * Runs one account action, turning a portal failure into a notice rather than an exception.
     *
     * Every entry point goes through here, so no caller has to remember to catch: a settings screen that
     * threw on a mistyped code would take the app with it. Private, and the entry points are plain suspend
     * functions rather than one that takes a lambda, because a suspending function-typed parameter is
     * close to unusable from Swift.
     */
    /**
     * The portal's error code as something the app can translate.
     *
     * The codes are the contract, not the sentences: `ClientApi.Refused` chooses them, and it answers an
     * unknown address and a wrong password with the same one on purpose, so this cannot tell them apart
     * either.
     */
    private fun messageFor(e: PortalException): AccountMessage = when (e.code) {
        "bad_credentials" -> AccountMessage.BAD_CREDENTIALS
        "not_verified" -> AccountMessage.NOT_VERIFIED
        "disabled" -> AccountMessage.DISABLED
        "closed" -> AccountMessage.REGISTRATION_CLOSED
        "email_taken" -> AccountMessage.EMAIL_TAKEN
        "weak_password" -> AccountMessage.WEAK_PASSWORD
        "bad_email" -> AccountMessage.BAD_EMAIL
        "too_many" -> AccountMessage.TOO_MANY_ATTEMPTS
        else -> AccountMessage.PORTAL_SAID
    }

    private suspend fun guarded(work: suspend () -> Unit) {
        if (_state.value.busy) {
            return
        }

        _state.value = _state.value.copy(busy = true, message = AccountMessage.NONE, detail = "")
        try {
            work()
        } catch (e: PortalException) {
            _state.value = _state.value.copy(message = messageFor(e), detail = e.message ?: "")
        } finally {
            _state.value = _state.value.copy(busy = false)
        }
    }
}
