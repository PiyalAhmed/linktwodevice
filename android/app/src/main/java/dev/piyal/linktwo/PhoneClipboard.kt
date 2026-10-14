package dev.piyal.linktwo

import android.content.ClipboardManager
import android.content.Context
import android.net.Uri
import android.os.Handler
import android.os.Looper

/**
 * Reads the phone clipboard and sends it to the Mac. Reading only works while a LinkTwo window has focus
 * (Android 10+), so callers are the main screen, the Quick Settings tile and the "Send clipboard" shortcut.
 */
object PhoneClipboard {
    sealed class Content {
        data class Text(val text: String) : Content()
        data class Image(val uri: Uri, val mime: String) : Content()
    }

    private val main = Handler(Looper.getMainLooper())

    fun read(ctx: Context): Content? {
        val item = ctx.getSystemService(ClipboardManager::class.java).primaryClip
            ?.takeIf { it.itemCount > 0 }?.getItemAt(0) ?: return null
        val uri = item.uri
        if (uri != null) {
            val mime = runCatching { ctx.contentResolver.getType(uri) }.getOrNull()
            if (mime?.startsWith("image/") == true) return Content.Image(uri, mime)
        }
        val text = (if (uri == null) item.coerceToText(ctx) else item.text)?.toString()
        return text?.takeIf { it.isNotEmpty() }?.let { Content.Text(it) }
    }

    /** Why sending isn't possible right now, or null if it is. */
    fun blockedReason(): String? = when {
        Prefs.paused -> "LinkTwo is paused"
        !Link.connected -> "Your ${Prefs.kind} isn't reachable"
        !Link.macActive -> "${Link.macName} is locked"
        else -> null
    }

    /** Text goes to the Mac clipboard; an image goes there too (and into the save folder). [done] runs on main. */
    fun send(ctx: Context, content: Content, done: (String) -> Unit) {
        when (content) {
            is Content.Text -> {
                Link.send(Message.of("clip", "text" to content.text))
                done("Clipboard sent to ${Link.macName}")
            }
            is Content.Image -> {
                val app = ctx.applicationContext
                Link.io.execute {
                    val ok = FileSender.send(app, content.uri, toClipboard = true)
                    main.post { done(if (ok) "Image is on ${Link.macName}'s clipboard" else "Couldn't send the image") }
                }
            }
        }
    }
}
