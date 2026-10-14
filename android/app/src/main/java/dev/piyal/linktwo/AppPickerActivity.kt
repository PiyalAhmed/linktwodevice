package dev.piyal.linktwo

import android.content.Intent
import android.graphics.drawable.Drawable
import android.os.Bundle
import android.view.View
import android.view.ViewGroup
import androidx.activity.enableEdgeToEdge
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding
import androidx.core.widget.doAfterTextChanged
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import dev.piyal.linktwo.databinding.ActivityAppPickerBinding
import dev.piyal.linktwo.databinding.ItemAppBinding

/** Pick the apps for the allow list or the block list (whichever mode is active). */
class AppPickerActivity : AppCompatActivity() {
    private class AppInfo(val pkg: String, val label: String, val icon: Drawable)

    private lateinit var b: ActivityAppPickerBinding
    private var all: List<AppInfo> = emptyList()
    private var shown: List<AppInfo> = emptyList()
    private val selected = Prefs.filterApps.toMutableSet()
    private val adapter = Adapter()

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        b = ActivityAppPickerBinding.inflate(layoutInflater)
        setContentView(b.root)
        ViewCompat.setOnApplyWindowInsetsListener(b.list) { v, insets ->
            v.updatePadding(bottom = insets.getInsets(WindowInsetsCompat.Type.navigationBars()).bottom)
            insets
        }

        b.toolbar.title = if (Prefs.filterMode == "allow") "Apps to send" else "Apps to keep on phone"
        b.toolbar.setNavigationOnClickListener { finish() }
        updateSubtitle()

        b.list.layoutManager = LinearLayoutManager(this)
        b.list.adapter = adapter
        b.search.doAfterTextChanged { filter(it?.toString().orEmpty()) }
        load()
    }

    private fun load() {
        Link.io.execute {
            val pm = packageManager
            val launcher = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
            val apps = pm.queryIntentActivities(launcher, 0)
                .map { it.activityInfo.applicationInfo }
                .distinctBy { it.packageName }
                .filter { it.packageName != packageName }
                .map { AppInfo(it.packageName, pm.getApplicationLabel(it).toString(), pm.getApplicationIcon(it)) }
                .sortedWith(compareBy({ it.pkg !in selected }, { it.label.lowercase() }))
            runOnUiThread {
                all = apps
                b.loading.visibility = View.GONE
                filter(b.search.text?.toString().orEmpty())
            }
        }
    }

    private fun filter(q: String) {
        shown = if (q.isBlank()) all else all.filter { it.label.contains(q, true) || it.pkg.contains(q, true) }
        @Suppress("NotifyDataSetChanged")
        adapter.notifyDataSetChanged()
    }

    private fun updateSubtitle() {
        b.toolbar.subtitle = if (selected.isEmpty()) "None selected" else "${selected.size} selected"
    }

    private inner class Holder(val v: ItemAppBinding) : RecyclerView.ViewHolder(v.root)

    private inner class Adapter : RecyclerView.Adapter<Holder>() {
        override fun getItemCount() = shown.size

        override fun onCreateViewHolder(parent: ViewGroup, type: Int) =
            Holder(ItemAppBinding.inflate(layoutInflater, parent, false))

        override fun onBindViewHolder(h: Holder, i: Int) {
            val app = shown[i]
            h.v.icon.setImageDrawable(app.icon)
            h.v.label.text = app.label
            h.v.pkg.text = app.pkg
            h.v.toggle.isChecked = app.pkg in selected
            h.v.root.setOnClickListener {
                if (!selected.add(app.pkg)) selected.remove(app.pkg)
                Prefs.filterApps = selected
                h.v.toggle.isChecked = app.pkg in selected
                updateSubtitle()
            }
        }
    }
}
