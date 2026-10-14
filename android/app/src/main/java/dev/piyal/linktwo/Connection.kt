package dev.piyal.linktwo

import android.content.Context
import android.net.Network
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.provider.Settings
import android.util.Base64
import android.util.Log
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import kotlin.concurrent.thread

private const val TAG = "LinkTwo"
private const val SERVICE_TYPE = "_linktwo._tcp"

/** An authenticated, encrypted connection to the paired Mac. */
class Connection private constructor(
    private val socket: Socket,
    private val inp: DataInputStream,
    private val out: DataOutputStream,
    private val sendCipher: FrameCipher,
    private val recvCipher: FrameCipher,
    val host: String,
    val macName: String,
) {
    @Volatile
    var isOpen = true
        private set

    @Volatile
    private var lastReceived = System.currentTimeMillis()

    /** Blocking; safe from any background thread. */
    fun send(m: Message): Boolean = synchronized(out) {
        if (!isOpen) return false
        try {
            out.writeFrame(sendCipher.seal(m.encode()))
            true
        } catch (e: IOException) {
            close()
            false
        }
    }

    fun startReading(onMessage: (Message) -> Unit, onClosed: () -> Unit) {
        thread(name = "link-read", isDaemon = true) {
            try {
                while (isOpen) {
                    val m = Message.decode(recvCipher.open(inp.readFrame()))
                    lastReceived = System.currentTimeMillis()
                    if (m.type == "pong") continue
                    if (m.type == "ping") send(Message.of("pong")) else onMessage(m)
                }
            } catch (e: Exception) {
                Log.i(TAG, "connection ended: $e")
            } finally {
                close()
                onClosed()
            }
        }
    }

    /**
     * A Mac that went away without closing the socket (app restarted, Mac asleep, firewall stealth mode) looks
     * exactly like a quiet one. Before something time-sensitive, ping and give up on the socket if nothing comes back.
     */
    fun probe(timeoutMs: Long, onDead: () -> Unit) {
        if (System.currentTimeMillis() - lastReceived < 15_000) return
        val sentAt = System.currentTimeMillis()
        if (!send(Message.of("ping"))) return onDead()
        thread(name = "link-probe", isDaemon = true) {
            Thread.sleep(timeoutMs)
            if (isOpen && lastReceived < sentAt) {
                Log.i(TAG, "Mac didn't answer ping; reconnecting")
                close()
            }
        }
    }

    fun close() {
        isOpen = false
        runCatching { socket.close() }
    }

    companion object {
        /** Tries the last known address, the QR-code addresses, then Bonjour. Blocking (~a few seconds max). */
        fun find(ctx: Context, net: Network): Connection? {
            val macId = Prefs.macId ?: return null
            val key = Prefs.key ?: return null
            val candidates = (listOfNotNull(Prefs.lastHost) + Prefs.hosts).distinct()
            for (h in candidates) {
                open(net, InetSocketAddress(h, Prefs.port), macId, key, deviceName(ctx))?.let { return it }
            }
            val found = discover(ctx, macId) ?: return null
            return open(net, found, macId, key, deviceName(ctx))
        }

        private fun open(net: Network, addr: InetSocketAddress, macId: String, key: ByteArray, device: String): Connection? {
            val s = net.socketFactory.createSocket()
            try {
                s.connect(addr, 2000)
                s.soTimeout = 5000
                s.tcpNoDelay = true
                s.keepAlive = true
                val inp = DataInputStream(BufferedInputStream(s.getInputStream()))
                val out = DataOutputStream(BufferedOutputStream(s.getOutputStream()))

                val phoneNonce = LinkCrypto.random(16)
                val hello = JSONObject()
                    .put("t", "hello").put("v", 1)
                    .put("nonce", Base64.encodeToString(phoneNonce, Base64.NO_WRAP))
                    .put("device", device)
                out.writeFrame(hello.toString().toByteArray())

                val reply = JSONObject(String(inp.readFrame()))
                if (reply.optString("t") != "hello" || reply.optString("macId") != macId) return null.also { s.close() }
                val macNonce = Base64.decode(reply.getString("nonce"), Base64.DEFAULT)

                val (p2m, m2p) = LinkCrypto.sessionKeys(key, phoneNonce, macNonce)
                val sc = FrameCipher(p2m)
                val rc = FrameCipher(m2p)
                out.writeFrame(sc.seal(Message.of("auth").encode()))
                val ok = Message.decode(rc.open(inp.readFrame()))
                if (ok.type != "authOk") return null.also { s.close() }

                // The Mac pings every 30 s, so silence this long means it's gone.
                s.soTimeout = 75_000
                return Connection(s, inp, out, sc, rc, addr.hostString, ok.str("name").ifBlank { Prefs.macName })
            } catch (e: Exception) {
                Log.i(TAG, "connect $addr failed: $e")
                runCatching { s.close() }
                return null
            }
        }

        @Suppress("DEPRECATION")
        private fun discover(ctx: Context, macId: String, timeoutMs: Long = 6000): InetSocketAddress? {
            val nsd = ctx.getSystemService(NsdManager::class.java)
            val result = AtomicReference<InetSocketAddress?>()
            val latch = CountDownLatch(1)
            val resolver = object : NsdManager.ResolveListener {
                override fun onResolveFailed(info: NsdServiceInfo, code: Int) {}
                override fun onServiceResolved(info: NsdServiceInfo) {
                    val id = info.attributes["id"]?.let { String(it) }
                    val host = info.host
                    if (id == macId && host != null) {
                        result.set(InetSocketAddress(host, info.port))
                        latch.countDown()
                    }
                }
            }
            val listener = object : NsdManager.DiscoveryListener {
                override fun onDiscoveryStarted(type: String) {}
                override fun onDiscoveryStopped(type: String) {}
                override fun onStartDiscoveryFailed(type: String, code: Int) = latch.countDown()
                override fun onStopDiscoveryFailed(type: String, code: Int) {}
                override fun onServiceLost(info: NsdServiceInfo) {}
                override fun onServiceFound(info: NsdServiceInfo) {
                    runCatching { nsd.resolveService(info, resolver) }
                }
            }
            nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)
            latch.await(timeoutMs, TimeUnit.MILLISECONDS)
            runCatching { nsd.stopServiceDiscovery(listener) }
            return result.get()
        }

        /** The user-visible name from Settings → About phone (e.g. "Galaxy A34 5G"), not the model code. */
        private fun deviceName(ctx: Context): String =
            Settings.Global.getString(ctx.contentResolver, Settings.Global.DEVICE_NAME)?.takeIf { it.isNotBlank() }
                ?: Build.MODEL ?: "Android"
    }
}
