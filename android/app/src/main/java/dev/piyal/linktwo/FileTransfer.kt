package dev.piyal.linktwo

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import java.io.OutputStream
import java.util.UUID

private const val CHUNK = 256 * 1024
private const val CHANNEL = "files"

/** Saves files from the Mac into Download/LinkTwo via MediaStore (no storage permission needed). */
class FileReceiver(private val ctx: Context) {
    private class Incoming(val uri: Uri, val out: OutputStream, val name: String, val mime: String)

    private val files = HashMap<String, Incoming>()

    @Synchronized
    fun handle(m: Message) {
        val id = m.str("id")
        when (m.type) {
            "fileStart" -> {
                val name = m.str("name").ifBlank { "file" }
                val mime = m.str("mime").ifBlank { "application/octet-stream" }
                val values = ContentValues().apply {
                    put(MediaStore.Downloads.DISPLAY_NAME, name)
                    put(MediaStore.Downloads.MIME_TYPE, mime)
                    put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/LinkTwo")
                    put(MediaStore.Downloads.IS_PENDING, 1)
                }
                val uri = ctx.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return
                val out = ctx.contentResolver.openOutputStream(uri) ?: return
                files[id] = Incoming(uri, out, name, mime)
            }
            "fileChunk" -> files[id]?.out?.write(m.payload)
            "fileEnd" -> {
                val f = files.remove(id) ?: return
                f.out.close()
                ctx.contentResolver.update(f.uri, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
                notifyReceived(f)
            }
            "fileAbort" -> abort(id)
        }
    }

    @Synchronized
    fun abortAll() = files.keys.toList().forEach(::abort)

    private fun abort(id: String) {
        val f = files.remove(id) ?: return
        runCatching { f.out.close() }
        runCatching { ctx.contentResolver.delete(f.uri, null, null) }
    }

    private fun notifyReceived(f: Incoming) {
        val nm = ctx.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Files from Mac", NotificationManager.IMPORTANCE_DEFAULT))
        val view = Intent(Intent.ACTION_VIEW).setDataAndType(f.uri, f.mime)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        val pi = PendingIntent.getActivity(ctx, f.uri.hashCode(), view, PendingIntent.FLAG_IMMUTABLE)
        val n = Notification.Builder(ctx, CHANNEL)
            .setSmallIcon(R.drawable.ic_link)
            .setContentTitle("Received from Mac")
            .setContentText(f.name)
            .setContentIntent(pi)
            .setAutoCancel(true)
            .build()
        runCatching { nm.notify(f.uri.hashCode(), n) }
    }
}

object FileSender {
    /** Blocking: streams one shared file to the Mac. [onProgress] gets 0..1. */
    fun send(ctx: Context, uri: Uri, toClipboard: Boolean, onProgress: (Float) -> Unit = {}): Boolean {
        val cr = ctx.contentResolver
        var name = "file"
        var size = -1L
        cr.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
            if (c.moveToFirst()) {
                c.getString(0)?.let { name = it }
                if (!c.isNull(1)) size = c.getLong(1)
            }
        }
        val mime = cr.getType(uri) ?: "application/octet-stream"
        val id = UUID.randomUUID().toString()
        val input = runCatching { cr.openInputStream(uri) }.getOrNull() ?: return false
        input.use { s ->
            if (!Link.sendBlocking(Message.of("fileStart", "id" to id, "name" to name, "size" to size, "mime" to mime, "toClipboard" to toClipboard))) return false
            val buf = ByteArray(CHUNK)
            var sent = 0L
            while (true) {
                val n = s.read(buf)
                if (n < 0) break
                if (n == 0) continue
                if (!Link.sendBlocking(Message.of("fileChunk", "id" to id, payload = buf.copyOf(n)))) return false
                sent += n
                if (size > 0) onProgress(sent.toFloat() / size)
            }
            return Link.sendBlocking(Message.of("fileEnd", "id" to id))
        }
    }
}
