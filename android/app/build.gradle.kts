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
        versionCode = 1
        versionName = "0.1"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("debug")
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
