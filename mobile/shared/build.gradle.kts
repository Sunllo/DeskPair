import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.kotlin.multiplatform)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.wire)
    alias(libs.plugins.android.kmp.library)
}

// protos/sunllo/*.proto is shared with the desktop and is the single source of truth for the wire format.
// Wire parses it itself, so nothing here needs protoc installed, and a proto change reaches Kotlin the same
// way it reaches C#: by being recompiled, not by being transcribed.
wire {
    kotlin { }

    sourcePath {
        srcDir(rootProject.file("../protos").absolutePath)
        // ipc.proto is the local UI-to-engine channel and has no business on a phone.
        include("sunllo/message.proto")
        include("sunllo/rendezvous.proto")
    }
}

kotlin {
    // jvm() is not a shipping target. It exists because the same tests run there in seconds rather than
    // minutes, which is where the protocol work actually gets done.
    jvm {
        compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
    }

    androidLibrary {
        namespace = "com.sunllo.deskpair.shared"
        compileSdk = 36
        minSdk = 26
    }

    listOf(iosArm64(), iosSimulatorArm64()).forEach { target ->
        target.binaries.framework {
            baseName = "DeskPairShared"
            isStatic = true
        }
    }

    // Android and the JVM share a socket implementation: hole punching needs to bind a local port before
    // connecting, which ktor's client cannot do, and java.net.Socket can. Without this source set the same
    // file would have to exist twice, and the two copies would drift.
    applyDefaultHierarchyTemplate()
    sourceSets {
        val jvmAndroidMain by creating { dependsOn(commonMain.get()) }
        jvmMain.get().dependsOn(jvmAndroidMain)
        androidMain.get().dependsOn(jvmAndroidMain)

        commonMain.dependencies {
            implementation(libs.kotlinx.coroutines.core)
            implementation(libs.cryptography.core)
            implementation(libs.wire.runtime)
            implementation(libs.ktor.network)
            implementation(libs.ktor.client.core)
            implementation(libs.ktor.client.content.negotiation)
            implementation(libs.ktor.serialization.kotlinx.json)
            // Settings are stored as JSON so a support request can ask someone to read the file out.
            implementation(libs.kotlinx.serialization.json)
        }
        commonTest.dependencies {
            implementation(kotlin("test"))
            implementation(libs.kotlinx.serialization.json)
            implementation(libs.kotlinx.coroutines.test)
        }
        jvmMain.dependencies {
            implementation(libs.cryptography.provider.jdk)
            implementation(libs.ktor.client.cio)
        }
        androidMain.dependencies {
            implementation(libs.cryptography.provider.jdk)
            implementation(libs.ktor.client.cio)
        }
        iosMain.dependencies {
            implementation(libs.cryptography.provider.cryptokit)
            implementation(libs.cryptography.provider.apple)
            implementation(libs.ktor.client.darwin)
        }
    }
}

// The live test needs to know which host to talk to. On the simulator that is not simply an environment
// variable: the test binary is spawned by simctl, which forwards only names prefixed with SIMCTL_CHILD_.
tasks.withType<org.jetbrains.kotlin.gradle.targets.native.tasks.KotlinNativeSimulatorTest>().configureEach {
    listOf(
        "SUNLLO_TEST_HOST",
        "SUNLLO_TEST_PASSWORD",
        "SUNLLO_TEST_ID",
        "SUNLLO_TEST_RENDEZVOUS",
        "SUNLLO_TEST_SERVER_KEY",
    ).forEach { name ->
        providers.environmentVariable(name).orNull?.let { environment("SIMCTL_CHILD_$name", it) }
    }
}

// The conformance vectors are a committed JSON file generated from the C# protocol
// (tools/DeskPair.Tools.Vectors). Kotlin/Native has no common way to read a test resource, so the
// file is baked into a generated source rather than plumbed through resources on every target.
val generateVectors by tasks.registering {
    val input = layout.projectDirectory.file("src/commonTest/resources/protocol-vectors.json")
    val outputDir = layout.buildDirectory.dir("generated/vectors/kotlin")
    inputs.file(input)
    outputs.dir(outputDir)
    doLast {
        val target = outputDir.get().file("com/sunllo/deskpair/ProtocolVectorsJson.kt").asFile
        target.parentFile.mkdirs()
        target.writeText(
            buildString {
                appendLine("// Generated from src/commonTest/resources/protocol-vectors.json. Do not edit.")
                appendLine("package com.sunllo.deskpair")
                appendLine()
                appendLine("internal const val PROTOCOL_VECTORS_JSON: String = \"\"\"")
                append(input.asFile.readText().replace("$", "\${'$'}"))
                appendLine("\"\"\"")
            },
        )
    }
}

// The terminal vectors live beside the desktop's tests, in tests/fixtures/vt, because both parsers answer
// to them: that is the only way two implementations of one terminal stay one terminal. Baked the same way.
val generateVtVectors by tasks.registering {
    val inputDir = rootProject.layout.projectDirectory.dir("../tests/fixtures/vt")
    val outputDir = layout.buildDirectory.dir("generated/vt/kotlin")
    inputs.dir(inputDir)
    outputs.dir(outputDir)
    doLast {
        val target = outputDir.get().file("com/sunllo/deskpair/terminal/VtVectorsJson.kt").asFile
        target.parentFile.mkdirs()
        val files = inputDir.asFile.listFiles { f -> f.extension == "json" }!!.sortedBy { it.name }
        target.writeText(
            buildString {
                appendLine("// Generated from tests/fixtures/vt/*.json. Do not edit.")
                appendLine("package com.sunllo.deskpair.terminal")
                appendLine()
                appendLine("internal val VT_VECTORS_JSON: Map<String, String> = mapOf(")
                for (file in files) {
                    append("    \"").append(file.name).append("\" to \"\"\"")
                    append(file.readText().replace("$", "\${'$'}"))
                    appendLine("\"\"\",")
                }
                appendLine(")")
            },
        )
    }
}

kotlin.sourceSets.commonTest.configure {
    kotlin.srcDir(generateVectors)
    kotlin.srcDir(generateVtVectors)
}
