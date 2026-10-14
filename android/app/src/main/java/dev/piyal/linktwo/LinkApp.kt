package dev.piyal.linktwo

import android.app.Application
import com.google.android.material.color.DynamicColors

class LinkApp : Application() {
    override fun onCreate() {
        super.onCreate()
        // Material You: take the colours from the wallpaper, like the system apps.
        DynamicColors.applyToActivitiesIfAvailable(this)
        Link.init(this)
    }
}
