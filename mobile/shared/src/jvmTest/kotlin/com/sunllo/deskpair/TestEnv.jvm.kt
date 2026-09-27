package com.sunllo.deskpair

internal actual fun testEnv(name: String): String? = System.getenv(name)
