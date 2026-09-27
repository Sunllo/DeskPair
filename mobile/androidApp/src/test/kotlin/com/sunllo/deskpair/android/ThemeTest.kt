package com.sunllo.deskpair.android

import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import javax.xml.parsers.DocumentBuilderFactory

/**
 * Every definition of the activity's theme, in every resource folder, is one of AppCompat's.
 *
 * MainActivity is an AppCompatActivity, and AppCompat refuses to start under any other theme. The launch theme
 * moved to AppCompat's DayNight when that happened, but its copy under values-night kept the framework's
 * Material parent -- so on a phone in dark mode the app crashed before drawing anything ("You need to use a
 * Theme.AppCompat theme"), while every emulator run, in light mode, was fine. The night colour is a resource
 * qualifier on the colour, not a second theme; if a second theme ever comes back, it still has to be AppCompat's.
 */
class ThemeTest {

    private val res = File("src/main/res")

    @Test
    fun the_activity_theme_is_appcompat_in_every_configuration() {
        val definitions = res.listFiles { f -> f.isDirectory && f.name.startsWith("values") }!!
            .flatMap { folder -> folder.listFiles { f -> f.extension == "xml" }!!.map { folder.name to it } }
            .flatMap { (folder, file) -> styles(file).filter { it.first == THEME }.map { folder to it.second } }

        assertTrue("no definition of $THEME found under ${res.absolutePath}", definitions.isNotEmpty())
        for ((folder, parent) in definitions) {
            assertTrue(
                "$folder defines $THEME with parent \"$parent\"; AppCompatActivity crashes at start unless it is Theme.AppCompat.*",
                parent.startsWith("Theme.AppCompat."),
            )
        }
    }

    private fun styles(file: File): List<Pair<String, String>> {
        val nodes = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(file).getElementsByTagName("style")
        return (0 until nodes.length).map { i ->
            val attributes = nodes.item(i).attributes
            attributes.getNamedItem("name").nodeValue to (attributes.getNamedItem("parent")?.nodeValue ?: "")
        }
    }

    private companion object {
        const val THEME = "Theme.DeskPair"
    }
}
