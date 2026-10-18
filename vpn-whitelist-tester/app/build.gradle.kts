import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

// Номер сборки приходит из GitHub Actions (run_number) — так каждое обновление ставится поверх старого.
val buildNumber = (System.getenv("BUILD_NUMBER") ?: "1").toInt()

android {
    namespace = "ru.khodakovsky.wltester"
    compileSdk = 35

    defaultConfig {
        applicationId = "ru.khodakovsky.wltester"
        minSdk = 26
        targetSdk = 35
        versionCode = buildNumber
        versionName = "1.0.$buildNumber"
    }

    signingConfigs {
        create("release") {
            // Ключ лежит в репозитории, чтобы любая сборка CI ставилась поверх предыдущей.
            // Для своего ключа задайте переменные окружения WL_KEYSTORE_* (см. README).
            storeFile = file(System.getenv("WL_KEYSTORE_FILE") ?: rootProject.file("signing/wltester.jks").path)
            storePassword = System.getenv("WL_KEYSTORE_PASSWORD") ?: "wltester"
            keyAlias = System.getenv("WL_KEY_ALIAS") ?: "wltester"
            keyPassword = System.getenv("WL_KEY_PASSWORD") ?: "wltester"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("release")
        }
        debug {
            signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    lint {
        checkReleaseBuilds = false
        abortOnError = false
    }

    packaging {
        // xray запускается как исполняемый файл из nativeLibraryDir — библиотеки должны распаковываться.
        jniLibs { useLegacyPackaging = true }
    }
}

kotlin {
    compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
}

dependencies {
    implementation(project(":core"))

    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.7")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")

    implementation(platform("androidx.compose:compose-bom:2024.12.01"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-core")
}
