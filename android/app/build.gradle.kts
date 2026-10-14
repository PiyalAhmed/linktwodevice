plugins {
    id("com.android.application")
}

android {
    namespace = "dev.piyal.linktwo"
    compileSdk = 36

    defaultConfig {
        applicationId = "dev.piyal.linktwo"
        minSdk = 31
        targetSdk = 36
        // CI passes -PappVersion=1.2.3 -PappVersionCode=<run number>; local builds stay 0.1 / 1.
        versionCode = (project.findProperty("appVersionCode") as String?)?.toIntOrNull() ?: 1
        versionName = (project.findProperty("appVersion") as String?) ?: "0.1"
    }

    signingConfigs {
        // Optional stable release key (GitHub secrets → env). Without it, release builds use the debug key,
        // which is fine for trying the app but changes between CI runs (updates then need an uninstall first).
        val keystore = System.getenv("ANDROID_KEYSTORE_PATH")
        if (keystore != null && file(keystore).exists()) {
            create("release") {
                storeFile = file(keystore)
                storePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("ANDROID_KEY_ALIAS")
                keyPassword = System.getenv("ANDROID_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    buildFeatures {
        viewBinding = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    // Material 3 (Material You) components: the same design system as Samsung's and Google's own apps.
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.appcompat:appcompat:1.7.0")
    // Google's QR scanner UI (runs inside Play services; no camera permission needed in this app).
    implementation("com.google.android.gms:play-services-code-scanner:16.1.0")
}
