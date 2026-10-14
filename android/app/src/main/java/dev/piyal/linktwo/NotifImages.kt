package dev.piyal.linktwo

import android.app.Notification
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.drawable.BitmapDrawable
import android.graphics.drawable.Icon
import android.net.Uri
import android.os.Bundle
import java.io.ByteArrayOutputStream

/** Pulls the picture a notification shows on the phone, as a JPEG small enough for one frame. */
object NotifImages {
    private const val MAX_SIDE = 1024

    /** Big picture (screenshots, photos) → image in the latest message → large icon (usually the sender's avatar). */
    fun best(ctx: Context, n: Notification): ByteArray? {
        val ex = n.extras
        val bmp = bigPicture(ctx, ex) ?: messageImage(ctx, ex) ?: n.getLargeIcon()?.let { fromIcon(ctx, it) }
        return bmp?.let(::jpeg)
    }

    @Suppress("DEPRECATION")
    private fun bigPicture(ctx: Context, ex: Bundle): Bitmap? =
        (ex.get(Notification.EXTRA_PICTURE_ICON) as? Icon)?.let { fromIcon(ctx, it) }
            ?: ex.get(Notification.EXTRA_PICTURE) as? Bitmap

    /** MessagingStyle messages are bundles; an image message carries a MIME type and a content URI. */
    @Suppress("DEPRECATION")
    private fun messageImage(ctx: Context, ex: Bundle): Bitmap? {
        val messages = ex.getParcelableArray(Notification.EXTRA_MESSAGES) ?: return null
        for (p in messages.reversed()) {
            val b = p as? Bundle ?: continue
            val type = b.getString("type") ?: continue
            val uri = b.get("uri") as? Uri ?: continue
            if (!type.startsWith("image/")) continue
            // The posting app may not have granted us read access; then there's simply no image.
            return runCatching { ctx.contentResolver.openInputStream(uri)?.use { decodeScaled(it.readBytes()) } }.getOrNull()
        }
        return null
    }

    fun fromIcon(ctx: Context, icon: Icon): Bitmap? = runCatching {
        val d = icon.loadDrawable(ctx) ?: return null
        if (d is BitmapDrawable && d.bitmap != null) return d.bitmap
        val w = d.intrinsicWidth.takeIf { it > 0 } ?: 192
        val h = d.intrinsicHeight.takeIf { it > 0 } ?: 192
        Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888).also {
            d.setBounds(0, 0, w, h)
            d.draw(Canvas(it))
        }
    }.getOrNull()

    private fun decodeScaled(bytes: ByteArray): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        var sample = 1
        while (maxOf(bounds.outWidth, bounds.outHeight) / (sample * 2) >= MAX_SIDE) sample *= 2
        return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })
    }

    fun jpeg(src: Bitmap): ByteArray? = runCatching {
        val scale = minOf(1f, MAX_SIDE.toFloat() / maxOf(src.width, src.height))
        val bmp = if (scale < 1f) Bitmap.createScaledBitmap(src, (src.width * scale).toInt(), (src.height * scale).toInt(), true) else src
        // JPEG has no alpha; flatten onto white so transparent avatars don't turn black.
        val flat = Bitmap.createBitmap(bmp.width, bmp.height, Bitmap.Config.ARGB_8888).also {
            Canvas(it).apply { drawColor(android.graphics.Color.WHITE); drawBitmap(bmp, 0f, 0f, null) }
        }
        ByteArrayOutputStream().also { flat.compress(Bitmap.CompressFormat.JPEG, 85, it) }.toByteArray()
    }.getOrNull()
}
