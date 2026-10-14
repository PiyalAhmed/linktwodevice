package dev.piyal.linktwo

import android.content.Context
import android.content.SharedPreferences
import android.net.Uri
import android.util.Base64

/** Pairing info from the Mac's QR code, kept in app-private storage. */
object Prefs {
    private lateinit var sp: SharedPreferences

    fun init(ctx: Context) {
        if (!::sp.isInitialized) sp = ctx.applicationContext.getSharedPreferences("link", Context.MODE_PRIVATE)
    }

    val macId: String? get() = sp.getString("macId", null)
    val macName: String get() = sp.getString("macName", null) ?: "Mac"
    val port: Int get() = sp.getInt("port", 47820)
    val key: ByteArray?
        get() = sp.getString("key", null)?.let { Base64.decode(it, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP) }
    val hosts: List<String> get() = sp.getString("hosts", "")!!.split(",").filter { it.isNotBlank() }
    var lastHost: String?
        get() = sp.getString("lastHost", null)
        set(v) = sp.edit().putString("lastHost", v).apply()
    /** Paused by the user: no connection at all until resumed. Kept across restarts. */
    var paused: Boolean
        get() = sp.getBoolean("paused", false)
        set(v) = sp.edit().putBoolean("paused", v).apply()
    var receiveClipboard: Boolean
        get() = sp.getBoolean("receiveClipboard", true)
        set(v) = sp.edit().putBoolean("receiveClipboard", v).apply()

    val paired: Boolean get() = macId != null && key?.size == 32

    /** "all", "allow" (only [filterApps]) or "block" (everything except [filterApps]). */
    var filterMode: String
        get() = sp.getString("filterMode", "all")!!
        set(v) = sp.edit().putString("filterMode", v).apply()
    var filterApps: Set<String>
        get() = sp.getStringSet("filterApps", emptySet())!!.toSet()
        set(v) = sp.edit().putStringSet("filterApps", v).apply()

    fun allows(pkg: String): Boolean = when (filterMode) {
        "allow" -> pkg in filterApps
        "block" -> pkg !in filterApps
        else -> true
    }

    /** Parses linktwo://pair?v=1&id=…&name=…&key=…&hosts=…&port=… */
    fun savePairing(raw: String): Boolean {
        val uri = Uri.parse(raw)
        if (uri.scheme != "linktwo" || uri.host != "pair") return false
        val id = uri.getQueryParameter("id") ?: return false
        val key = uri.getQueryParameter("key") ?: return false
        val decoded = runCatching { Base64.decode(key, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP) }.getOrNull()
        if (decoded?.size != 32) return false
        sp.edit()
            .putString("macId", id)
            .putString("macName", uri.getQueryParameter("name") ?: "Mac")
            .putString("key", key)
            .putString("hosts", uri.getQueryParameter("hosts") ?: "")
            .putInt("port", uri.getQueryParameter("port")?.toIntOrNull() ?: 47820)
            .remove("lastHost")
            .apply()
        return true
    }

    /** Forget the Mac but keep app preferences. */
    fun clear() {
        sp.edit().remove("macId").remove("macName").remove("key").remove("hosts").remove("port").remove("lastHost").apply()
    }
}
