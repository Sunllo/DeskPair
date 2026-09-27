package com.sunllo.deskpair

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

/** The desktop's mapping, restated as the cases that would break first if the two ever drifted. */
class PlatformLogoTest {

    @Test
    fun recognises_what_the_desktop_recognises() {
        assertEquals(PlatformLogo.WINDOWS, PlatformLogos.of("Windows 11 Pro"))
        assertEquals(PlatformLogo.MACOS, PlatformLogos.of("macOS 15.1"))
        assertEquals(PlatformLogo.MACOS, PlatformLogos.of("Darwin"))
        assertEquals(PlatformLogo.IOS, PlatformLogos.of("iOS 18"))
        assertEquals(PlatformLogo.IOS, PlatformLogos.of("iPad"))
        assertEquals(PlatformLogo.ANDROID, PlatformLogos.of("Android 15"))
        assertEquals(PlatformLogo.LINUX, PlatformLogos.of("Linux 6.8"))
    }

    @Test
    fun a_distribution_beats_plain_linux() {
        assertEquals(PlatformLogo.UBUNTU, PlatformLogos.of("Ubuntu 24.04 Linux"))
        assertEquals(PlatformLogo.DEBIAN, PlatformLogos.of("Debian GNU/Linux 12"))
        assertEquals(PlatformLogo.DEBIAN, PlatformLogos.of("Raspbian"))
        assertEquals(PlatformLogo.REDHAT, PlatformLogos.of("Fedora Linux 41"))
        assertEquals(PlatformLogo.REDHAT, PlatformLogos.of("Red Hat Enterprise Linux 9"))
        assertEquals(PlatformLogo.REDHAT, PlatformLogos.of("Rocky Linux"))
    }

    @Test
    fun nothing_unrecognised_gets_a_logo() {
        assertNull(PlatformLogos.of(null))
        assertNull(PlatformLogos.of(""))
        assertNull(PlatformLogos.of("   "))
        assertNull(PlatformLogos.of("FreeBSD 14"))
    }

    /** iOS builds a file name out of this, so an empty or surprising one would be a missing image. */
    @Test
    fun the_asset_name_is_what_the_files_are_called() {
        assertEquals("redhat", PlatformLogos.assetFor("CentOS Stream 9"))
        assertNull(PlatformLogos.assetFor("FreeBSD 14"))
        for (logo in PlatformLogo.entries) {
            assertEquals(logo.asset, logo.asset.lowercase())
        }
    }
}
