package com.sunllo.deskpair

/**
 * A test's view of the environment. Kotlin has no common way to read one, and the live tests need to know
 * which host to talk to without that host's password being committed anywhere.
 */
internal expect fun testEnv(name: String): String?
