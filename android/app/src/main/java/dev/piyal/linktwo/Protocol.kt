package dev.piyal.linktwo

import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.nio.ByteBuffer
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

// Wire format: see docs/PROTOCOL.md. Must stay byte-compatible with mac/Sources/LinkTwo/Protocol.swift.

private val EMPTY = ByteArray(0)
private const val MAX_FRAME = 4 * 1024 * 1024

/** One application message: a JSON header plus an optional binary payload. */
class Message(val header: JSONObject, val payload: ByteArray = EMPTY) {
    val type: String get() = header.optString("t")
    fun str(key: String): String = header.optString(key)

    fun encode(): ByteArray {
        val json = header.toString().toByteArray()
        return ByteBuffer.allocate(4 + json.size + payload.size).putInt(json.size).put(json).put(payload).array()
    }

    companion object {
        fun of(type: String, vararg fields: Pair<String, Any?>, payload: ByteArray = EMPTY): Message {
            val h = JSONObject().put("t", type)
            for ((k, v) in fields) h.put(k, v)
            return Message(h, payload)
        }

        fun decode(b: ByteArray): Message {
            if (b.size < 4) throw IOException("short message")
            val h = ByteBuffer.wrap(b).int
            if (h < 0 || 4 + h > b.size) throw IOException("bad header length")
            return Message(JSONObject(String(b, 4, h)), b.copyOfRange(4 + h, b.size))
        }
    }
}

object LinkCrypto {
    private val INFO = "linktwo v1".toByteArray()
    private val rng = SecureRandom()

    fun random(n: Int) = ByteArray(n).also { rng.nextBytes(it) }

    /** RFC 5869 HKDF-SHA256. */
    private fun hkdf(ikm: ByteArray, salt: ByteArray, info: ByteArray, len: Int): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(salt, "HmacSHA256"))
        val prk = mac.doFinal(ikm)
        mac.init(SecretKeySpec(prk, "HmacSHA256"))
        val out = ByteArrayOutputStream()
        var t = EMPTY
        var i = 1
        while (out.size() < len) {
            mac.update(t)
            mac.update(info)
            mac.update(i.toByte())
            t = mac.doFinal()
            out.write(t)
            i++
        }
        return out.toByteArray().copyOf(len)
    }

    /** Returns (phone→mac key, mac→phone key). */
    fun sessionKeys(pairingKey: ByteArray, phoneNonce: ByteArray, macNonce: ByteArray): Pair<ByteArray, ByteArray> {
        val okm = hkdf(pairingKey, phoneNonce + macNonce, INFO, 64)
        return okm.copyOfRange(0, 32) to okm.copyOfRange(32, 64)
    }
}

/** AES-256-GCM for one direction of one session; nonce = 4 zero bytes + 8-byte counter. */
class FrameCipher(key: ByteArray) {
    private val key = SecretKeySpec(key, "AES")
    private var counter = 0L

    private fun nonce(): ByteArray = ByteBuffer.allocate(12).putInt(0).putLong(counter).array()

    @Synchronized
    fun seal(plain: ByteArray): ByteArray {
        val n = nonce()
        counter++
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key, GCMParameterSpec(128, n))
        return n + c.doFinal(plain)
    }

    @Synchronized
    fun open(frame: ByteArray): ByteArray {
        if (frame.size < 28) throw IOException("short frame")
        val n = nonce()
        if (!frame.copyOfRange(0, 12).contentEquals(n)) throw IOException("unexpected nonce")
        counter++
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, n))
        return c.doFinal(frame, 12, frame.size - 12)
    }
}

internal fun DataInputStream.readFrame(): ByteArray {
    val len = readInt()
    if (len <= 0 || len > MAX_FRAME) throw IOException("bad frame length $len")
    return ByteArray(len).also { readFully(it) }
}

internal fun DataOutputStream.writeFrame(body: ByteArray) {
    writeInt(body.size)
    write(body)
    flush()
}
