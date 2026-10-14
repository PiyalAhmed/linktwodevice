package dev.piyal.linktwo

import android.Manifest
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.net.Uri
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.LayoutInflater
import android.view.View
import android.widget.ImageView
import android.widget.Toast
import androidx.activity.enableEdgeToEdge
import androidx.annotation.AttrRes
import androidx.annotation.DrawableRes
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding
import com.google.android.material.button.MaterialButton
import com.google.android.material.color.MaterialColors
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.google.android.material.materialswitch.MaterialSwitch
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import dev.piyal.linktwo.databinding.ActivityMainBinding
import dev.piyal.linktwo.databinding.ItemRowBinding

/** Status + setup + settings, in the style of Android's own Settings app. */
class MainActivity : AppCompatActivity() {
    private lateinit var b: ActivityMainBinding
    private lateinit var clipSwitch: MaterialSwitch
    private val refresh: () -> Unit = { render() }
    private var clip: PhoneClipboard.Content? = null
    private val clipChanged = android.content.ClipboardManager.OnPrimaryClipChangedListener { refreshClipboard() }

    private val phonePerms = arrayOf(
        Manifest.permission.READ_PHONE_STATE,
        Manifest.permission.ANSWER_PHONE_CALLS,
        Manifest.permission.POST_NOTIFICATIONS,
    )

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        b = ActivityMainBinding.inflate(layoutInflater)
        setContentView(b.root)
        ViewCompat.setOnApplyWindowInsetsListener(b.scroll) { v, insets ->
            v.updatePadding(bottom = insets.getInsets(WindowInsetsCompat.Type.navigationBars()).bottom)
            insets
        }

        b.pairButton.setOnClickListener { scan() }
        b.sendClipButton.setOnClickListener { sendClipboard() }
        b.pauseButton.setOnClickListener { Link.setPaused(!Prefs.paused) }
        b.reconnectButton.setOnClickListener {
            Link.reconnectNow()
            toast("Looking for ${Prefs.macName}…")
        }

        b.filterGroup.addOnButtonCheckedListener { _, id, checked ->
            if (!checked) return@addOnButtonCheckedListener
            Prefs.filterMode = when (id) {
                R.id.filterAllow -> "allow"
                R.id.filterBlock -> "block"
                else -> "all"
            }
            render()
        }

        b.chooseApps.setup(R.drawable.ic_apps, "Choose apps", chevron = true) {
            startActivity(Intent(this, AppPickerActivity::class.java))
        }

        clipSwitch = MaterialSwitch(this).apply {
            setOnCheckedChangeListener { _, on -> Prefs.receiveClipboard = on }
        }
        b.clipReceive.setup(R.drawable.ic_paste, "Receive Mac clipboard", "Text you copy on the Mac is ready to paste here") {
            clipSwitch.toggle()
        }
        b.clipReceive.trailing.addView(clipSwitch)

        b.clipSend.setup(R.drawable.ic_info, "Send to Mac",
            "Select text → To Mac (next to Copy). After a Copy button: the “Clipboard → Mac” tile, or long-press the LinkTwo icon → Send clipboard. Photos and links: Share → your Mac.")
        b.clipSend.root.isClickable = false
        b.clipSend.root.background = null

        b.macRescan.setup(R.drawable.ic_camera, "Scan pairing code again") { scan() }
        b.macUnpair.setup(R.drawable.ic_delete, "Unpair") { confirmUnpair() }
        val error = MaterialColors.getColor(b.root, androidx.appcompat.R.attr.colorError)
        b.macUnpair.title.setTextColor(error)
        b.macUnpair.icon.imageTintList = ColorStateList.valueOf(error)
        b.macInfo.root.isClickable = false
        b.macInfo.root.background = null
    }

    override fun onResume() {
        super.onResume()
        ShareShortcut.publish(this)
        Link.addListener(refresh)
        getSystemService(android.content.ClipboardManager::class.java).addPrimaryClipChangedListener(clipChanged)
        render()
    }

    /** The clipboard is only readable once our window has focus, which comes just after onResume. */
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) refreshClipboard()
    }

    override fun onPause() {
        getSystemService(android.content.ClipboardManager::class.java).removePrimaryClipChangedListener(clipChanged)
        Link.removeListener(refresh)
        super.onPause()
    }

    private fun render() {
        renderStatus()
        renderClipboard()
        renderSetup()

        val paired = Prefs.paired
        b.pairedSections.visibility = if (paired) View.VISIBLE else View.GONE
        if (!paired) return

        val checkedId = when (Prefs.filterMode) {
            "allow" -> R.id.filterAllow
            "block" -> R.id.filterBlock
            else -> R.id.filterAll
        }
        if (b.filterGroup.checkedButtonId != checkedId) b.filterGroup.check(checkedId)
        val count = Prefs.filterApps.size
        b.filterHint.text = when (Prefs.filterMode) {
            "allow" -> if (count == 0) "Nothing is sent until you choose some apps." else "Only the apps you choose are sent."
            "block" -> "Apps you choose stay on your phone. Everything else is sent."
            else -> "Notifications from every app are sent while you're using your Mac."
        }
        b.chooseApps.root.visibility = if (Prefs.filterMode == "all") View.GONE else View.VISIBLE
        b.chooseApps.summary.visibility = View.VISIBLE
        b.chooseApps.summary.text = if (count == 0) "None selected" else "$count selected"

        if (clipSwitch.isChecked != Prefs.receiveClipboard) clipSwitch.isChecked = Prefs.receiveClipboard

        b.macInfo.setup(R.drawable.ic_laptop, Prefs.macName,
            Link.macHost?.let { "Connected · $it" } ?: "Not connected")
    }

    private fun renderStatus() {
        val mac = Link.macName
        data class S(@DrawableRes val icon: Int, @AttrRes val color: Int, val label: String, val title: String, val body: String)
        val primary = androidx.appcompat.R.attr.colorPrimary
        val variant = com.google.android.material.R.attr.colorOnSurfaceVariant
        val s = when {
            !Prefs.paired -> S(R.drawable.ic_laptop, primary, "Not paired",
                "Pair with your Mac", "On your Mac, open LinkTwo from the menu bar and scan the code it shows.")
            !notifAccess() -> S(R.drawable.ic_info, androidx.appcompat.R.attr.colorError, "Off",
                "Notification access needed", "LinkTwo can't run in the background until it's turned on.")
            Prefs.paused -> S(R.drawable.ic_pause_circle, com.google.android.material.R.attr.colorTertiary, "Paused",
                "Syncing is paused", "Nothing is sent to or received from $mac until you resume.")
            Link.live -> S(R.drawable.ic_check_circle, primary, "Connected",
                "Connected to $mac", "New notifications and calls appear on your Mac.")
            Link.connected && Link.macInactiveReason == "paused" -> S(R.drawable.ic_pause_circle,
                com.google.android.material.R.attr.colorTertiary, "Paused",
                "Paused on $mac", "Syncing was paused from your Mac. Notifications stay on your phone.")
            Link.connected -> S(R.drawable.ic_pause_circle, com.google.android.material.R.attr.colorTertiary, "Paused",
                "$mac is locked", "Notifications stay on your phone until you unlock your Mac.")
            Link.state == Link.State.SEARCHING -> S(R.drawable.ic_refresh, variant,
                "Searching", "Looking for $mac", "Checking this Wi-Fi network…")
            Link.state == Link.State.NOT_FOUND -> S(R.drawable.ic_info, variant,
                "Not connected", "$mac isn't available", "It may be asleep or on another network. Nothing is sent.")
            else -> S(R.drawable.ic_info, variant,
                "Not connected", "Not on home Wi-Fi", "LinkTwo will connect when you're on the same Wi-Fi as your Mac.")
        }
        val color = MaterialColors.getColor(b.root, s.color)
        b.statusIcon.setImageResource(s.icon)
        b.statusIcon.imageTintList = ColorStateList.valueOf(color)
        b.statusLabel.text = s.label
        b.statusLabel.setTextColor(color)
        b.statusTitle.text = s.title
        b.statusBody.text = s.body
        b.pairButton.visibility = if (Prefs.paired) View.GONE else View.VISIBLE

        // Pause / Resume and a manual Reconnect, once paired and running.
        val running = Prefs.paired && notifAccess()
        b.statusActions.visibility = if (running) View.VISIBLE else View.GONE
        b.pauseButton.text = if (Prefs.paused) "Resume" else "Pause"
        b.pauseButton.setIconResource(if (Prefs.paused) R.drawable.ic_check_circle else R.drawable.ic_pause_circle)
        b.reconnectButton.visibility = if (Prefs.paused) View.GONE else View.VISIBLE
    }

    private fun refreshClipboard() {
        if (!hasWindowFocus()) return
        clip = PhoneClipboard.read(this)
        renderClipboard()
    }

    private fun renderClipboard() {
        val show = Prefs.paired
        b.clipboardHeader.visibility = if (show) View.VISIBLE else View.GONE
        b.clipboardCard.visibility = if (show) View.VISIBLE else View.GONE
        if (!show) return

        when (val c = clip) {
            is PhoneClipboard.Content.Text -> {
                b.clipImage.visibility = View.GONE
                b.clipPreview.text = c.text.trim().replace(Regex("\\s+"), " ").take(300)
                b.clipMeta.text = "Text · ${c.text.length} characters"
            }
            is PhoneClipboard.Content.Image -> {
                b.clipImage.visibility = View.VISIBLE
                val thumb = runCatching { contentResolver.loadThumbnail(c.uri, android.util.Size(168, 168), null) }.getOrNull()
                if (thumb != null) b.clipImage.setImageBitmap(thumb) else b.clipImage.setImageResource(R.drawable.ic_paste)
                b.clipPreview.text = "Image"
                b.clipMeta.text = c.mime
            }
            null -> {
                b.clipImage.visibility = View.GONE
                b.clipPreview.text = "Nothing copied"
                b.clipMeta.text = "Copy something in any app, then come back here."
            }
        }
        val blocked = PhoneClipboard.blockedReason()
        b.sendClipButton.isEnabled = clip != null && blocked == null
        b.sendClipButton.text = blocked ?: "Send to ${Prefs.macName}"
    }

    private fun sendClipboard() {
        refreshClipboard()
        val c = clip ?: return toast("Clipboard is empty")
        PhoneClipboard.blockedReason()?.let { return toast(it) }
        b.sendClipButton.isEnabled = false
        PhoneClipboard.send(this, c) { msg ->
            toast(msg)
            renderClipboard()
        }
    }

    private fun renderSetup() {
        data class Step(@DrawableRes val icon: Int, val title: String, val summary: String, val action: String, val run: () -> Unit)
        val pm = getSystemService(PowerManager::class.java)
        val steps = buildList {
            if (!notifAccess()) add(Step(R.drawable.ic_notifications, "Notification access",
                "If the switch is greyed out: App info → ⋮ → Allow restricted settings", "Turn on") {
                startActivity(
                    Intent(Settings.ACTION_NOTIFICATION_LISTENER_DETAIL_SETTINGS)
                        .putExtra(Settings.EXTRA_NOTIFICATION_LISTENER_COMPONENT_NAME,
                            ComponentName(this@MainActivity, NotifService::class.java).flattenToString())
                )
            })
            if (!phonePerms.all { checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED }) add(
                Step(R.drawable.ic_phone, "Calls", "Show incoming calls on your Mac and answer from there", "Allow") {
                    requestPermissions(phonePerms, 1)
                })
            if (!pm.isIgnoringBatteryOptimizations(packageName)) add(
                Step(R.drawable.ic_battery, "Unrestricted battery use", "Keeps the connection to your Mac reliable", "Allow") {
                    startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
                })
        }
        val show = steps.isNotEmpty()
        b.setupHeader.visibility = if (show) View.VISIBLE else View.GONE
        b.setupCard.visibility = if (show) View.VISIBLE else View.GONE
        b.setupList.removeAllViews()
        steps.forEachIndexed { i, step ->
            if (i > 0) LayoutInflater.from(this).inflate(R.layout.item_divider, b.setupList, true)
            val row = ItemRowBinding.inflate(layoutInflater, b.setupList, true)
            row.setup(step.icon, step.title, step.summary, onClick = step.run)
            (layoutInflater.inflate(R.layout.item_tonal_button, row.trailing, false) as MaterialButton).apply {
                text = step.action
                setOnClickListener { step.run() }
                row.trailing.addView(this)
            }
        }
    }

    private fun ItemRowBinding.setup(
        @DrawableRes iconRes: Int, title: String, summary: String? = null, chevron: Boolean = false, onClick: (() -> Unit)? = null,
    ) {
        icon.setImageResource(iconRes)
        this.title.text = title
        this.summary.text = summary
        this.summary.visibility = if (summary.isNullOrEmpty()) View.GONE else View.VISIBLE
        if (chevron && trailing.childCount == 0) {
            trailing.addView(ImageView(this@MainActivity).apply {
                setImageResource(R.drawable.ic_chevron_right)
                imageTintList = ColorStateList.valueOf(
                    MaterialColors.getColor(b.root, com.google.android.material.R.attr.colorOnSurfaceVariant))
            })
        }
        if (onClick != null) root.setOnClickListener { onClick() }
    }

    private fun scan() {
        val opts = GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()
        GmsBarcodeScanning.getClient(this, opts).startScan()
            .addOnSuccessListener { code ->
                if (Prefs.savePairing(code.rawValue ?: "")) {
                    Link.disconnect()
                    Link.reconnectNow()
                    ShareShortcut.publish(this)
                    toast("Paired with ${Prefs.macName}")
                } else {
                    toast("That isn't a LinkTwo pairing code")
                }
                render()
            }
            .addOnFailureListener { toast("Scanner unavailable: ${it.message}") }
    }

    private fun confirmUnpair() {
        MaterialAlertDialogBuilder(this)
            .setIcon(R.drawable.ic_delete)
            .setTitle("Unpair from ${Prefs.macName}?")
            .setMessage("You'll need to scan the pairing code on your Mac again to reconnect.")
            .setNegativeButton("Cancel", null)
            .setPositiveButton("Unpair") { _, _ ->
                Link.disconnect()
                Prefs.clear()
                ShareShortcut.remove(this)
                render()
            }
            .show()
    }

    @Deprecated("Deprecated in Java")
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        NotifService.instance?.restartCallMonitor()
        render()
    }

    private fun notifAccess(): Boolean =
        Settings.Secure.getString(contentResolver, "enabled_notification_listeners")
            ?.contains(ComponentName(this, NotifService::class.java).flattenToString()) == true

    private fun toast(s: String) = Toast.makeText(this, s, Toast.LENGTH_SHORT).show()
}
