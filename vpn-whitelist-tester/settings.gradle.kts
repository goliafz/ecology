pluginManagement {
    repositories {
        mavenCentral()
        gradlePluginPortal()
    }
}

// -PcoreOnly=true собирает только модуль core (без Android SDK) — удобно для быстрых тестов логики.
val coreOnly = providers.gradleProperty("coreOnly").orNull == "true"

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        if (!coreOnly) {
            google {
                content {
                    includeGroupByRegex("com\\.android.*")
                    includeGroupByRegex("com\\.google.*")
                    includeGroupByRegex("androidx.*")
                }
            }
        }
        mavenCentral()
    }
}

rootProject.name = "wl-tester"
include(":core")
if (!coreOnly) include(":app")
