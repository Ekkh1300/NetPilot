import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.netpilot.mobile"
    compileSdk = 36
    buildToolsVersion = "36.1.0"

    defaultConfig {
        applicationId = "com.netpilot.mobile"
        minSdk = 26
        targetSdk = 36
        versionCode = 3
        versionName = "1.2.1"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    signingConfigs {
        create("release") {
            // Credentials never live in the repository. Either drop a keystore.properties
            // next to this file (format: keystore.properties.example) or export
            // NETPILOT_STORE_FILE / NETPILOT_STORE_PASSWORD / NETPILOT_KEY_ALIAS /
            // NETPILOT_KEY_PASSWORD in CI. Without either, the release build is simply
            // unsigned - a fresh clone still compiles and runs the tests.
            //
            // These used to be hardcoded here, which published the signing key to anyone who
            // cloned the repo: with that keystore anyone can ship an "update" the phone
            // installs as if it were ours.
            val secret = Properties()
            val secretFile = rootProject.file("keystore.properties")
            if (secretFile.exists()) secretFile.inputStream().use { secret.load(it) }

            fun value(key: String, env: String): String? =
                (System.getenv(env) ?: secret.getProperty(key))?.takeIf { it.isNotBlank() }

            value("storeFile", "NETPILOT_STORE_FILE")?.let { storeFile = rootProject.file(it) }
            storePassword = value("storePassword", "NETPILOT_STORE_PASSWORD")
            keyAlias = value("keyAlias", "NETPILOT_KEY_ALIAS")
            keyPassword = value("keyPassword", "NETPILOT_KEY_PASSWORD")
        }
    }

    buildTypes {
        release {
            // No R8/ProGuard: nothing on this machine can verify a minified build, and a
            // reflection call that R8 relocated would cost far more than the saved bytes.
            isMinifyEnabled = false
            // Only sign when a keystore is actually configured; otherwise Gradle fails with a
            // confusing "storeFile missing" instead of producing a plainly unsigned APK.
            val releaseSigning = signingConfigs.getByName("release")
            if (releaseSigning.storeFile != null) signingConfig = releaseSigning
        }
    }

    packaging {
        resources {
            excludes.add("/META-INF/{AL2.0,LGPL2.1}")
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    // compileSdk stays at 36 (the Android 17 / API 37 platform cannot be downloaded on this
    // machine), so every AndroidX artifact below is pinned to the newest release whose
    // `aar-metadata.minCompileSdk` is <= 36.
    val composeBom = platform("androidx.compose:compose-bom:2026.06.01")

    implementation("androidx.core:core-ktx:1.18.0")
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.10.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.10.0")

    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.navigation:navigation-compose:2.9.4")

    implementation("androidx.datastore:datastore-preferences:1.2.1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")

    debugImplementation("androidx.compose.ui:ui-tooling")

    // Host tests: no device is attached to this machine, so the packet builders, the rule
    // engine and the relay itself are verified on the JVM (see app/src/test).
    testImplementation("junit:junit:4.13.2")
}
