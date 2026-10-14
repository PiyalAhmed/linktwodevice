package dev.piyal.linktwo

import android.content.Context
import android.content.Intent
import android.content.pm.ShortcutInfo
import android.content.pm.ShortcutManager
import android.graphics.drawable.Icon

/** Publishes the paired Mac as a Direct Share target, so it sits in the top row of the share sheet. */
object ShareShortcut {
    private const val ID = "send_to_mac"
    private const val CATEGORY = "dev.piyal.linktwo.category.SEND_TO_MAC"

    fun publish(ctx: Context) {
        if (!Prefs.paired) return remove(ctx)
        val sm = ctx.getSystemService(ShortcutManager::class.java) ?: return
        val info = ShortcutInfo.Builder(ctx, ID)
            .setShortLabel(Prefs.macName)
            .setLongLabel("Send to ${Prefs.macName}")
            .setIcon(Icon.createWithResource(ctx, R.mipmap.ic_launcher))
            .setCategories(setOf(CATEGORY))
            .setLongLived(true)
            // Required for any shortcut; the share sheet itself routes to ShareActivity via <share-target>.
            .setIntent(Intent(ctx, MainActivity::class.java).setAction(Intent.ACTION_MAIN))
            .build()
        runCatching { sm.pushDynamicShortcut(info) }
    }

    fun remove(ctx: Context) {
        val sm = ctx.getSystemService(ShortcutManager::class.java) ?: return
        runCatching { sm.removeLongLivedShortcuts(listOf(ID)) }
        runCatching { sm.removeDynamicShortcuts(listOf(ID)) }
    }
}
