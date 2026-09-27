plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.compose.compiler)
}

android {
    namespace = "com.sunllo.deskpair.android"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.sunllo.deskpair"
        // API 26 is where MediaCodec's asynchronous mode and modern TLS are both dependable.
        minSdk = 26
        targetSdk = 36
        // Play refuses an upload whose versionCode it has seen before, so the release pipeline passes it in
        // rather than this file being edited for every build.
        versionCode = (findProperty("deskpair.versionCode") as String?)?.toInt() ?: 1
        versionName = "0.4.0"
    }

    // buildConfig for the version string the About section shows; off by default in AGP 8.
    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // The upload key, never in the repo. Play App Signing holds the key users' installs are verified
    // against; this one only proves an upload came from us, and Google can rotate it if it leaks.
    // A build that cannot find it produces an unsigned release rather than failing, so a contributor with
    // no key can still check that the release build compiles.
    val keystore = (findProperty("deskpair.keystore") as String?) ?: System.getenv("DESKPAIR_KEYSTORE")
    signingConfigs {
        if (keystore != null) {
            create("upload") {
                storeFile = file(keystore)
                storePassword = (findProperty("deskpair.keystorePassword") as String?)
                    ?: System.getenv("DESKPAIR_KEYSTORE_PASSWORD")
                keyAlias = (findProperty("deskpair.keyAlias") as String?)
                    ?: System.getenv("DESKPAIR_KEY_ALIAS") ?: "upload"
                keyPassword = (findProperty("deskpair.keyPassword") as String?)
                    ?: System.getenv("DESKPAIR_KEY_PASSWORD")
            }
        }
    }

    androidResources {
        // The languages the app ships, so a bundle carries exactly these and Play lists them.
        localeFilters += listOf("en", "zh-rTW", "b+zh+Hans", "ja", "ko", "de", "fr", "es", "pt-rBR", "ru")
    }

    buildTypes {
        getByName("debug") { isMinifyEnabled = false }
        getByName("release") {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = signingConfigs.findByName("upload")
        }
    }
}

kotlin {
    compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) }
}

dependencies {
    implementation(project(":shared"))
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.extended)
    implementation(libs.play.services.code.scanner)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.appcompat)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.kotlinx.coroutines.core)
    testImplementation(libs.junit)
}
