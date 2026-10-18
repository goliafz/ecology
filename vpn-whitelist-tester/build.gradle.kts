buildscript {
    val coreOnly = gradle.startParameter.projectProperties["coreOnly"] == "true"
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
        gradlePluginPortal()
    }
    dependencies {
        classpath("org.jetbrains.kotlin:kotlin-gradle-plugin:2.2.0")
        classpath("org.jetbrains.kotlin:kotlin-serialization:2.2.0")
        if (!coreOnly) {
            classpath("org.jetbrains.kotlin:compose-compiler-gradle-plugin:2.2.0")
            classpath("com.android.tools.build:gradle:8.10.1")
        }
    }
}
