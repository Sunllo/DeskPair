package com.sunllo.deskpair

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertSame

class PasswordTextTest {
    @Test
    fun `letters digits symbols and the space pass through untouched`() {
        val value = "Aa1!@# \$%^&*()_+-=[]{}|;':\",./<>?`~"
        assertSame(value, PasswordText.ascii(value))
    }

    @Test
    fun `anything an input method could slip in is dropped`() {
        assertEquals("abc123", PasswordText.ascii("abc密碼123"))
        assertEquals("", PasswordText.ascii("ａｂｃ"))
        assertEquals("x", PasswordText.ascii("xé’​"))
    }
}
