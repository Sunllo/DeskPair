package com.sunllo.deskpair.store

/** One language the apps are published in: the tag the setting stores and the name the menu shows. */
data class AppLanguage(val code: String, val nativeName: String)

/**
 * The languages the phone apps speak, the same ten in the same order as the desktop and the website, so
 * every language menu in the product reads alike. The tags are BCP-47 as the platforms spell them; each
 * platform resolves its own resources from them (`values-b+zh+Hans`, `zh-Hans.lproj`).
 */
object AppLanguages {
    const val SYSTEM: String = "system"

    val all: List<AppLanguage> = listOf(
        AppLanguage("en", "English"),
        AppLanguage("zh-TW", "繁體中文"),
        AppLanguage("zh-Hans", "简体中文"),
        AppLanguage("ja", "日本語"),
        AppLanguage("ko", "한국어"),
        AppLanguage("de", "Deutsch"),
        AppLanguage("fr", "Français"),
        AppLanguage("es", "Español"),
        AppLanguage("pt-BR", "Português (Brasil)"),
        AppLanguage("ru", "Русский"),
    )

    fun isKnown(code: String): Boolean = all.any { it.code == code }
}
