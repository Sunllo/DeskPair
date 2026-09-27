package com.sunllo.deskpair.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import javax.xml.parsers.DocumentBuilderFactory

/**
 * Every translated strings.xml is held to the English one: same names, same positional placeholders.
 *
 * Android resolves a missing string to English silently, and a `%1$s` that a translator dropped throws at
 * run time in exactly one language. Neither shows up in an English emulator; this does, on every build.
 */
class StringsParityTest {

    private val res = File("src/main/res")

    private fun strings(folder: String): Map<String, String> {
        val doc = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(File(res, "$folder/strings.xml"))
        val nodes = doc.getElementsByTagName("string")
        return (0 until nodes.length).associate { i ->
            val node = nodes.item(i)
            node.attributes.getNamedItem("name").nodeValue to node.textContent
        }
    }

    private fun placeholders(text: String): List<String> =
        Regex("%(\\d+\\$)?[sd]").findAll(text).map { it.value }.sorted().toList()

    private val translations: List<File>
        get() = res.listFiles { f -> f.isDirectory && f.name.startsWith("values-") && File(f, "strings.xml").exists() }!!.toList()

    @Test
    fun there_are_translations_to_check() {
        assertTrue("no values-*/strings.xml found from ${res.absolutePath}", translations.isNotEmpty())
    }

    @Test
    fun every_language_has_every_english_string() {
        val english = strings("values")
        for (folder in translations) {
            val theirs = strings(folder.name)
            val missing = english.keys.filter { !theirs.containsKey(it) || theirs[it].isNullOrBlank() }
            assertEquals("${folder.name} is missing $missing", emptyList<String>(), missing)
            val stale = theirs.keys.filter { !english.containsKey(it) }
            assertEquals("${folder.name} carries strings English no longer has: $stale", emptyList<String>(), stale)
        }
    }

    @Test
    fun every_translation_takes_the_same_placeholders_as_the_english() {
        val english = strings("values")
        for (folder in translations) {
            val theirs = strings(folder.name)
            val wrong = english.filter { (k, v) -> theirs[k] != null && placeholders(v) != placeholders(theirs[k]!!) }.keys
            assertEquals("${folder.name}: placeholders differ for $wrong", emptySet<String>(), wrong)
        }
    }
}
