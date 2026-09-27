// The mobile build is its own Gradle build, deliberately separate from DeskPair.slnx. The two share
// protos/sunllo/*.proto and the conformance vectors, and nothing else.

pluginManagement {
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "deskpair-mobile"

include(":shared")
include(":androidApp")
