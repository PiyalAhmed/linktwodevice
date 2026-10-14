package dev.piyal.linktwo

import android.app.Activity
import android.app.PendingIntent
import android.content.ClipboardManager
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.widget.Toast

/** Quick Settings tile: one tap sends the phone clipboard to the Mac. */
class ClipTileService : TileService() {
    override fun onStartListening() {
        qsTile?.apply {
            state = if (Link.live) Tile.STATE_INACTIVE else Tile.STATE_UNAVAILABLE
            subtitle = when {
                Link.live -> Link.macName
                Prefs.paused -> "Paused"
                Link.connected -> "Mac locked"
                else -> "Mac away"
            }
            updateTile()
        }
    }

    @Suppress("DEPRECATION")
    override fun onClick() {
        val intent = Intent(this, ClipSendActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE))
        } else {
            startActivityAndCollapse(intent)
        }
    }
}

/**
 * Invisible activity: Android 10+ only lets the focused app read the clipboard, so we take focus for a moment,
 * read it, send it, and disappear. Used by the Quick Settings tile and the "Send clipboard" app shortcut.
 * Copied text goes to the Mac clipboard; a copied image goes there too (and into the save folder).
 */
class ClipSendActivity : Activity() {
    private var done = false

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || done) return
        done = true
        val content = PhoneClipboard.read(this)
        val blocked = PhoneClipboard.blockedReason()
        when {
            blocked != null -> toast(blocked)
            content == null -> toast("Clipboard is empty")
            else -> {
                if (content is PhoneClipboard.Content.Image) toast("Sending image to ${Link.macName}…")
                val app = applicationContext
                PhoneClipboard.send(this, content) { Toast.makeText(app, it, Toast.LENGTH_SHORT).show() }
            }
        }
        finish()
    }

    private fun toast(s: String) = Toast.makeText(applicationContext, s, Toast.LENGTH_SHORT).show()
}
