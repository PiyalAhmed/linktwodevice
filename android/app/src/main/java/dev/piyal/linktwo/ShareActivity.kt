package dev.piyal.linktwo

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import dev.piyal.linktwo.databinding.DialogSendBinding

/**
 * "Send to computer" in the share sheet: an invisible activity hosting a standard progress dialog. It stays alive
 * until the transfer finishes, because the read permission for shared files only lasts while it's open.
 */
class ShareActivity : AppCompatActivity() {
    private lateinit var d: DialogSendBinding
    private var dialog: AlertDialog? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        when {
            Prefs.paused -> return bail("LinkTwo is paused. Resume it in the app first.")
            !Link.connected -> return bail("Your ${Prefs.kind} isn't reachable. Are you on home Wi-Fi?")
            !Link.macActive -> return bail("${Link.macName} is locked. Unlock it, then try again.")
        }

        val uris = sharedUris(intent)
        // Share sheet sends EXTRA_TEXT; the text-selection menu sends EXTRA_PROCESS_TEXT.
        val text = (intent.getCharSequenceExtra(Intent.EXTRA_TEXT)
            ?: intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT))?.toString()
        when {
            uris.isNotEmpty() -> sendFiles(uris)
            !text.isNullOrEmpty() -> {
                Link.send(Message.of("clip", "text" to text))
                bail("Copied to ${Link.macName}")
            }
            else -> finish()
        }
    }

    private fun sendFiles(uris: List<Uri>) {
        d = DialogSendBinding.inflate(layoutInflater)
        dialog = MaterialAlertDialogBuilder(this)
            .setTitle("Sending to ${Link.macName}")
            .setView(d.root)
            .setCancelable(false)
            .show()

        // A single shared image also lands on the Mac clipboard, ready to paste.
        val toClipboard = uris.size == 1 && (intent.type ?: "").startsWith("image/")
        Link.io.execute {
            var ok = 0
            uris.forEachIndexed { i, uri ->
                val name = displayName(uri)
                runOnUiThread {
                    d.fileName.text = name
                    d.detail.text = if (uris.size > 1) "File ${i + 1} of ${uris.size}" else ""
                }
                val sent = FileSender.send(this, uri, toClipboard) { p ->
                    runOnUiThread {
                        if (d.progress.isIndeterminate) {
                            d.progress.hide()
                            d.progress.isIndeterminate = false
                            d.progress.show()
                        }
                        d.progress.setProgressCompat((p * 100).toInt(), true)
                    }
                }
                if (sent) ok++
            }
            runOnUiThread {
                bail(when {
                    ok < uris.size -> "Sent $ok of ${uris.size}. The connection was lost."
                    toClipboard -> "Sent. It's also on your ${Prefs.kind}'s clipboard."
                    else -> "Sent to ${Link.macName}"
                })
            }
        }
    }

    private fun displayName(uri: Uri): String =
        contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        } ?: "File"

    @Suppress("DEPRECATION")
    private fun sharedUris(i: Intent): List<Uri> = when (i.action) {
        Intent.ACTION_SEND -> listOfNotNull(i.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java))
        Intent.ACTION_SEND_MULTIPLE -> i.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
        else -> emptyList()
    }

    private fun bail(msg: String) {
        dialog?.dismiss()
        Toast.makeText(applicationContext, msg, Toast.LENGTH_SHORT).show()
        finish()
    }
}
