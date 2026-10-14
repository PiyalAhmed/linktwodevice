package dev.piyal.linktwo

import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager

/** Shows "To Mac" or "To PC" in the text-selection menu, matching the paired computer. */
object ProcessTextLabel {
    fun apply(ctx: Context) {
        val pc = Prefs.computerOs == "windows"
        set(ctx, ".SendSelectedText", enabled = !pc)
        set(ctx, ".SendSelectedTextPc", enabled = pc)
    }

    private fun set(ctx: Context, alias: String, enabled: Boolean) {
        val component = ComponentName(ctx, ctx.packageName + alias)
        val state = if (enabled) PackageManager.COMPONENT_ENABLED_STATE_ENABLED else PackageManager.COMPONENT_ENABLED_STATE_DISABLED
        if (ctx.packageManager.getComponentEnabledSetting(component) == state) return
        ctx.packageManager.setComponentEnabledSetting(component, state, PackageManager.DONT_KILL_APP)
    }
}
