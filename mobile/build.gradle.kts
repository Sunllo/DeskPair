// Plugins are declared once here and applied in the modules that need them.
//
// Not cosmetic: applied straight in two sibling modules, the Kotlin plugin loads twice under different
// classloaders and its shared build service cannot be handed between them, which fails the iOS framework
// link with a message about InstrumentingVisitableURLClassLoader that says nothing about the real cause.
plugins {
    alias(libs.plugins.kotlin.multiplatform) apply false
    alias(libs.plugins.kotlin.android) apply false
    alias(libs.plugins.kotlin.serialization) apply false
    alias(libs.plugins.compose.compiler) apply false
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.android.kmp.library) apply false
    alias(libs.plugins.wire) apply false
}
