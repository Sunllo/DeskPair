package com.sunllo.deskpair

/**
 * Which logo stands for a peer, decided once for both phones.
 *
 * The desktop draws these from `Assets/PlatformIcons.axaml`, the phones from an asset catalogue and a
 * drawable folder, but all three carry the same vector paths -- `tools/platform-icons.py` writes the mobile
 * copies from the desktop's. A peer calling itself "Ubuntu 24.04" must therefore land on the same shape
 * whichever of the three is looking at it, and the only way that stays true is to decide it in one place.
 *
 * [asset] is the name both phones build their file name from: `platform-ubuntu.svg` on iOS,
 * `ic_platform_ubuntu.xml` on Android, `icon.ubuntu` on the desktop.
 */
public enum class PlatformLogo(public val asset: String) {
    WINDOWS("windows"),
    MACOS("macos"),
    IOS("ios"),
    ANDROID("android"),
    LINUX("linux"),
    UBUNTU("ubuntu"),
    DEBIAN("debian"),
    REDHAT("redhat"),
}

/**
 * The platform string a peer reported, turned into a logo.
 *
 * An `object` rather than a top-level function because Swift reaches an object by name and a top-level
 * function through a class named after the file, which is the sort of thing that only breaks when the file
 * is renamed. Everything else the phones share is already reached this way.
 */
public object PlatformLogos {

    /**
     * The logo for what a peer called itself, or null when nothing fits.
     *
     * Nothing recognisable gets no icon rather than a generic monitor: a wrong logo is a worse answer than
     * no logo, and both lists keep the space so the names stay in a column.
     *
     * The order matches `PlatformIcons.KeyFor` on the desktop line for line, and two lines of it are load
     * bearing. The distributions come before plain Linux, so "Ubuntu 24.04 Linux" keeps its own mark and
     * "Linux 6.8" gets the penguin. And "ios" is tried before "mac", because these are substring tests, not
     * prefixes -- "macOS" does not contain "ios", but the reverse order would still be a trap worth
     * avoiding, and matching the desktop exactly is the point.
     */
    public fun of(platform: String?): PlatformLogo? {
        val p = platform?.lowercase() ?: return null
        return when {
            p.isBlank() -> null
            p.contains("windows") -> PlatformLogo.WINDOWS
            p.contains("android") -> PlatformLogo.ANDROID
            p.contains("ios") || p.contains("iphone") || p.contains("ipad") -> PlatformLogo.IOS
            p.contains("mac") || p.contains("darwin") || p.contains("osx") -> PlatformLogo.MACOS
            p.contains("ubuntu") -> PlatformLogo.UBUNTU
            p.contains("debian") || p.contains("raspbian") -> PlatformLogo.DEBIAN
            p.contains("red hat") || p.contains("redhat") || p.contains("rhel") || p.contains("fedora") ||
                p.contains("centos") || p.contains("rocky") || p.contains("almalinux") -> PlatformLogo.REDHAT
            p.contains("linux") -> PlatformLogo.LINUX
            else -> null
        }
    }

    /**
     * The same answer as [of], as the bare asset name or null.
     *
     * Kotlin enums cross into Swift as classes, not as Swift enums, so a `switch` over them there does not
     * compile and comparing eight class properties by identity would be worse than this. iOS asks for the
     * name and builds `platform-<name>`; Android switches over the enum, which reads better in Kotlin.
     */
    public fun assetFor(platform: String?): String? = of(platform)?.asset
}
