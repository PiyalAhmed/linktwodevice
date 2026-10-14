package dev.piyal.linktwo

import android.content.Context
import android.net.Network
import android.os.Looper
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

/**
 * Owns the (single) connection to the Mac. It only tries to connect while some Wi-Fi network is up;
 * "home" is simply the network where the paired Mac answers, so no location permission is needed.
 *
 * Everything is live-only: nothing is queued while the Mac is away or locked, and nothing is replayed later.
 */
object Link {
    interface Handler {
        fun onLinkConnected()
        fun onMacPresence(active: Boolean)
        fun onMacMessage(m: Message)
    }

    enum class State { OFF, NOT_PAIRED, PAUSED, NO_WIFI, SEARCHING, NOT_FOUND, CONNECTED }

    private lateinit var app: Context
    private val ctl = Executors.newSingleThreadScheduledExecutor { Thread(it, "link-ctl") }
    private val sender = Executors.newSingleThreadExecutor { Thread(it, "link-send") }
    val io = Executors.newCachedThreadPool()
    private val main = android.os.Handler(Looper.getMainLooper())
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private lateinit var files: FileReceiver

    // Touched only on ctl, except the @Volatile reads.
    @Volatile private var wifi: Network? = null
    @Volatile private var conn: Connection? = null
    private var retry: ScheduledFuture<*>? = null
    private var attempt = 0
    /** The Mac answered on the current Wi-Fi session, so this is home: retry briskly (e.g. while the Mac sleeps). */
    private var homeWifi = false

    /** Back-off on a Wi-Fi where the Mac has never answered (seconds). */
    private val backoff = longArrayOf(10, 30, 60, 120)
    private const val SLOW_RETRY = 300L
    private const val HOME_RETRY = 20L

    @Volatile var handler: Handler? = null

    @Volatile var state = State.OFF
        private set

    /** The Mac is unlocked and awake. Only then does anything get sent. */
    @Volatile var macActive = false
        private set

    /** Why the Mac isn't active: "locked" or "paused" (paused from the Mac). */
    @Volatile var macInactiveReason = ""
        private set

    val connected: Boolean get() = conn?.isOpen == true
    val live: Boolean get() = connected && macActive
    val macName: String get() = conn?.macName ?: Prefs.macName
    val macHost: String? get() = conn?.host

    fun init(ctx: Context) {
        if (::app.isInitialized) return
        app = ctx.applicationContext
        Prefs.init(app)
        files = FileReceiver(app)
    }

    fun addListener(l: () -> Unit) = listeners.add(l)
    fun removeListener(l: () -> Unit) = listeners.remove(l)

    fun onWifiAvailable(n: Network) = ctl.execute {
        wifi = n
        attempt = 0
        homeWifi = false
        schedule(0)
    }

    fun onWifiLost(n: Network) = ctl.execute {
        if (wifi != n) return@execute
        wifi = null
        retry?.cancel(false)
        drop()
        setState(State.NO_WIFI)
    }

    /** Listener service went away: drop everything until it's rebound. */
    fun shutdown() = ctl.execute {
        wifi = null
        retry?.cancel(false)
        drop()
        setState(State.OFF)
    }

    /** Manual retry: drops any existing (possibly stale) connection and searches again right away. */
    fun reconnectNow() = ctl.execute {
        if (Prefs.paused) return@execute
        attempt = 0
        drop()
        if (wifi != null) schedule(0) else setState(State.NO_WIFI)
    }

    /** Pause = disconnect and stay disconnected (across restarts) until resumed. */
    fun setPaused(p: Boolean) = ctl.execute {
        Prefs.paused = p
        if (p) {
            retry?.cancel(false)
            conn?.send(Message.of("bye", "reason" to "paused"))
            drop()
            setState(State.PAUSED)
        } else {
            attempt = 0
            if (wifi != null) schedule(0) else setState(State.NO_WIFI)
        }
    }

    fun disconnect() = ctl.execute { drop() }

    private fun drop() {
        conn?.close()
        conn = null
        macActive = false
    }

    private fun schedule(delaySec: Long) {
        retry?.cancel(false)
        retry = ctl.schedule({ tryConnect() }, delaySec, TimeUnit.SECONDS)
    }

    private fun tryConnect() {
        val net = wifi ?: return
        if (connected) return
        if (!Prefs.paired) {
            setState(State.NOT_PAIRED)
            return
        }
        if (Prefs.paused) {
            setState(State.PAUSED)
            return
        }
        setState(State.SEARCHING)
        val c = Connection.find(app, net)
        if (c == null) {
            setState(State.NOT_FOUND)
            schedule(if (homeWifi) HOME_RETRY else backoff.getOrElse(attempt) { SLOW_RETRY })
            attempt++
            return
        }
        attempt = 0
        homeWifi = true
        conn = c
        macActive = false // until the Mac tells us
        Prefs.lastHost = c.host
        setState(State.CONNECTED)
        main.post { handler?.onLinkConnected() }
        c.startReading(::onMessage) {
            files.abortAll()
            ctl.execute {
                if (conn !== c) return@execute
                conn = null
                macActive = false
                setState(State.NOT_FOUND)
                if (wifi != null) schedule(3)
            }
        }
    }

    private fun onMessage(m: Message) {
        when (m.type) {
            // File data stays on the reader thread so it doesn't clog the main thread.
            "fileStart", "fileChunk", "fileEnd", "fileAbort" -> files.handle(m)
            // The Mac's Reconnect button: drop this socket and dial again immediately.
            "reconnect" -> ctl.execute {
                attempt = 0
                drop()
                setState(State.SEARCHING)
                if (wifi != null) schedule(1)
            }
            "presence" -> {
                macActive = m.header.optBoolean("active")
                macInactiveReason = m.header.optString("reason")
                notifyListeners()
                main.post { handler?.onMacPresence(macActive) }
            }
            else -> main.post { handler?.onMacMessage(m) }
        }
    }

    /** Fire-and-forget; safe from the main thread. Dropped if not connected. */
    fun send(m: Message) {
        val c = conn ?: return
        sender.execute { c.send(m) }
    }

    /**
     * For notifications and calls: also checks the socket is really alive, so a Mac that vanished
     * (sleep, restart) is noticed within seconds. Nothing is replayed afterwards.
     */
    fun sendLive(m: Message) {
        val c = conn ?: return
        sender.execute {
            c.send(m)
            c.probe(5_000) { c.close() }
        }
    }

    /** Blocking; for background threads (file transfers). */
    fun sendBlocking(m: Message): Boolean = conn?.send(m) ?: false

    private fun setState(s: State) {
        state = s
        notifyListeners()
    }

    private fun notifyListeners() = main.post { listeners.forEach { it() } }
}
