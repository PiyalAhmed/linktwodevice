package dev.piyal.linktwo

import android.app.Notification
import android.app.RemoteInput
import android.content.ClipData
import android.content.ClipboardManager
import android.content.ComponentName
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Canvas
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.Bundle
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.util.Log
import java.io.ByteArrayOutputStream

/**
 * The hub of the phone side. Android keeps a granted notification listener bound all the time at near-zero cost,
 * so this is what "wakes up" networking when Wi-Fi appears — no always-on foreground service needed.
 */
class NotifService : NotificationListenerService(), Link.Handler {
    companion object {
        @Volatile var instance: NotifService? = null
            private set

        private val SKIP_CATEGORIES = setOf(
            Notification.CATEGORY_CALL, Notification.CATEGORY_PROGRESS, Notification.CATEGORY_TRANSPORT,
            Notification.CATEGORY_SERVICE, Notification.CATEGORY_SYSTEM, Notification.CATEGORY_NAVIGATION,
        )
    }

    private val sentIcons = HashSet<String>()
    private val lastContent = HashMap<String, String>()
    private val labels = HashMap<String, String>()
    private var calls: CallMonitor? = null
    private val media by lazy { MediaForwarder(this, ::appLabel) }
    private var listening = false

    private val wifiCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = Link.onWifiAvailable(network)
        override fun onLost(network: Network) = Link.onWifiLost(network)
    }

    override fun onListenerConnected() {
        instance = this
        Link.init(this)
        Link.handler = this
        if (!listening) {
            val req = NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build()
            getSystemService(ConnectivityManager::class.java).registerNetworkCallback(req, wifiCallback)
            listening = true
        }
        restartCallMonitor()
    }

    override fun onListenerDisconnected() {
        teardown()
        // Samsung's battery manager sometimes unbinds listeners; ask to come back.
        requestRebind(ComponentName(this, NotifService::class.java))
    }

    override fun onDestroy() {
        teardown()
        super.onDestroy()
    }

    private fun teardown() {
        if (instance === this) instance = null
        if (Link.handler === this) Link.handler = null
        if (listening) {
            runCatching { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(wifiCallback) }
            listening = false
        }
        calls?.stop()
        calls = null
        Link.shutdown()
    }

    /** Called after the user grants phone permissions. */
    fun restartCallMonitor() {
        calls?.stop()
        calls = CallMonitor(this).also { it.start() }
    }

    // MARK: Phone → Mac

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (sbn.notification.category == Notification.CATEGORY_CALL) calls?.onCallNotification(sbn)
        // Live only: if the Mac isn't in use right now, the notification simply stays on the phone.
        if (!Link.live) return
        if (media.isMedia(sbn)) {
            media.posted(sbn)
        } else {
            forward(sbn)
        }
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        if (sbn.notification.category == Notification.CATEGORY_CALL) calls?.onCallNotificationRemoved(sbn)
        if (lastContent.remove(sbn.key) != null && Link.connected) Link.send(Message.of("notifRemoved", "key" to sbn.key))
        if (media.removed(sbn.key) && Link.connected) Link.send(Message.of("mediaRemoved", "key" to sbn.key))
    }

    private fun forward(sbn: StatusBarNotification) {
        val n = sbn.notification
        if (sbn.packageName == packageName || sbn.isOngoing) return
        if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return
        if (n.category in SKIP_CATEGORIES) return
        if (!Prefs.allows(sbn.packageName)) return

        val ex = n.extras
        val title = ex.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        val text = (ex.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: ex.getCharSequence(Notification.EXTRA_TEXT))
            ?.toString().orEmpty()
        if (title.isBlank() && text.isBlank()) return

        // Apps re-post the same notification constantly (progress, "only alert once" updates); send real changes only.
        val sig = "$title\u0000$text"
        if (lastContent[sbn.key] == sig) return
        lastContent[sbn.key] = sig

        val pkg = sbn.packageName
        val icon = if (sentIcons.add(pkg)) appIconPng(pkg) else null
        val image = NotifImages.best(this, n)
        // Payload = app icon (first time per app) followed by the notification's own picture, if any.
        Link.sendLive(
            Message.of(
                "notif",
                "key" to sbn.key, "pkg" to pkg, "app" to appLabel(pkg), "time" to sbn.postTime,
                "title" to title, "text" to text.take(4000),
                "canReply" to (replyAction(n) != null),
                "iconLen" to (icon?.size ?: 0), "imageLen" to (image?.size ?: 0),
                payload = (icon ?: ByteArray(0)) + (image ?: ByteArray(0)),
            )
        )
    }

    private fun appLabel(pkg: String): String = labels.getOrPut(pkg) {
        runCatching { packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString() }
            .getOrDefault(pkg)
    }

    private fun appIconPng(pkg: String): ByteArray? = runCatching {
        val d = packageManager.getApplicationIcon(pkg)
        val bmp = Bitmap.createBitmap(96, 96, Bitmap.Config.ARGB_8888)
        d.setBounds(0, 0, 96, 96)
        d.draw(Canvas(bmp))
        ByteArrayOutputStream().also { bmp.compress(Bitmap.CompressFormat.PNG, 100, it) }.toByteArray()
    }.getOrNull()

    private fun replyAction(n: Notification): Notification.Action? =
        n.actions?.firstOrNull { a -> a.remoteInputs?.any { it.allowFreeFormInput } == true }

    // MARK: Mac → phone

    override fun onLinkConnected() {
        sentIcons.clear()
        lastContent.clear()
        media.reset()
    }

    override fun onMacPresence(active: Boolean) {
        // Whatever was shown before the lock is gone on the Mac; start dedupe fresh.
        lastContent.clear()
        media.reset()
        if (active) {
            calls?.onMacActive()
            // What's playing right now is still live: show it in the Mac's Now Playing, without a banner.
            runCatching { activeNotifications }.getOrNull()
                ?.filter { media.isMedia(it) }
                ?.forEach { media.posted(it, quiet = true) }
        }
    }

    override fun onMacMessage(m: Message) {
        when (m.type) {
            "notifDismiss" -> runCatching { cancelNotification(m.str("key")) }
            "notifReply" -> reply(m.str("key"), m.str("text"))
            "callAction" -> calls?.perform(m.str("action"))
            "mediaAction" -> media.control(m.str("key"), m.str("action"), m.header.optLong("value"))
            "clip" -> if (Prefs.receiveClipboard) {
                getSystemService(ClipboardManager::class.java)
                    .setPrimaryClip(ClipData.newPlainText("From ${Prefs.macName}", m.str("text")))
            }
        }
    }

    private fun reply(key: String, text: String) {
        val sbn = runCatching { activeNotifications }.getOrNull()?.firstOrNull { it.key == key } ?: return
        val action = replyAction(sbn.notification) ?: return
        val inputs = action.remoteInputs
        val results = Bundle().apply { inputs.forEach { putCharSequence(it.resultKey, text) } }
        val intent = Intent().addFlags(Intent.FLAG_RECEIVER_FOREGROUND)
        RemoteInput.addResultsToIntent(inputs, intent, results)
        try {
            action.actionIntent.send(this, 0, intent)
        } catch (e: Exception) {
            Log.w("LinkTwo", "reply failed: $e")
        }
    }
}
