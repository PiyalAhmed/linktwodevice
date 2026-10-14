package dev.piyal.linktwo

import android.Manifest
import android.annotation.SuppressLint
import android.app.ActivityOptions
import android.app.Notification
import android.app.PendingIntent
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.service.notification.StatusBarNotification
import android.telecom.TelecomManager
import android.telephony.TelephonyCallback
import android.telephony.TelephonyManager
import android.util.Log

/**
 * Call state comes from TelephonyCallback (reliable); the caller's name comes from the dialer's own call
 * notification (no contacts/call-log permission needed). Answer/decline prefers the dialer notification's
 * own buttons, then falls back to TelecomManager.
 */
class CallMonitor(private val ctx: Context) {
    private val tm = ctx.getSystemService(TelephonyManager::class.java)
    private val telecom = ctx.getSystemService(TelecomManager::class.java)
    private val main = Handler(Looper.getMainLooper())

    private var state = "idle"
    private var name = ""
    private var number = ""
    private var callNotif: StatusBarNotification? = null
    private var registered = false

    private val callback = object : TelephonyCallback(), TelephonyCallback.CallStateListener {
        override fun onCallStateChanged(s: Int) {
            state = when (s) {
                TelephonyManager.CALL_STATE_RINGING -> "ringing"
                TelephonyManager.CALL_STATE_OFFHOOK -> "offhook"
                else -> "idle"
            }
            if (state == "idle") {
                name = ""
                number = ""
                callNotif = null
            }
            push()
        }
    }

    fun start() {
        if (!granted(Manifest.permission.READ_PHONE_STATE)) return
        tm.registerTelephonyCallback(ctx.mainExecutor, callback)
        registered = true
    }

    fun stop() {
        if (registered) tm.unregisterTelephonyCallback(callback)
        registered = false
    }

    fun onCallNotification(sbn: StatusBarNotification) {
        callNotif = sbn
        val ex = sbn.notification.extras
        val title = ex.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        val text = ex.getCharSequence(Notification.EXTRA_TEXT)?.toString().orEmpty()
        if (title.isNotBlank()) name = title
        // Dialers usually put the number (or "Mobile 012…") in the text line; keep it only if it looks like one.
        number = if (text.count { it.isDigit() } >= 5) text else ""
        if (state == "ringing") push()
    }

    fun onCallNotificationRemoved(sbn: StatusBarNotification) {
        if (callNotif?.key == sbn.key) callNotif = null
    }

    /** Ringing is only sent to a Mac in use; "call over" always goes out so the Mac never shows a stale panel. */
    private fun push() {
        if (state == "ringing" && !Link.live) return
        if (!Link.connected) return
        Link.sendLive(Message.of("call", "state" to state, "name" to name.ifBlank { "Unknown caller" }, "number" to number))
    }

    /** The Mac was just unlocked while the phone is ringing — that's still live, so show it. */
    fun onMacActive() {
        if (state == "ringing") push()
    }

    fun perform(action: String) {
        if (state != "ringing") return
        val extraKey = if (action == "answer") Notification.EXTRA_ANSWER_INTENT else Notification.EXTRA_DECLINE_INTENT
        val pi = callNotif?.notification?.extras?.getParcelable(extraKey, PendingIntent::class.java)
        if (pi != null) {
            try {
                val opts = ActivityOptions.makeBasic()
                if (Build.VERSION.SDK_INT >= 34) {
                    opts.setPendingIntentBackgroundActivityStartMode(ActivityOptions.MODE_BACKGROUND_ACTIVITY_START_ALLOWED)
                }
                pi.send(ctx, 0, null, null, null, null, opts.toBundle())
            } catch (e: Exception) {
                Log.w("LinkTwo", "call notification action failed: $e")
            }
            // If the dialer ignored it, the call is still ringing a moment later.
            main.postDelayed({ if (state == "ringing") viaTelecom(action) }, 800)
        } else {
            viaTelecom(action)
        }
    }

    @SuppressLint("MissingPermission")
    @Suppress("DEPRECATION")
    private fun viaTelecom(action: String) {
        if (!granted(Manifest.permission.ANSWER_PHONE_CALLS)) return
        try {
            if (action == "answer") telecom.acceptRingingCall() else telecom.endCall()
        } catch (e: Exception) {
            Log.w("LinkTwo", "telecom $action failed: $e")
        }
    }

    private fun granted(p: String) = ctx.checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED
}
