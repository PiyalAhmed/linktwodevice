package dev.piyal.linktwo

import android.app.Notification
import android.content.Context
import android.graphics.Bitmap
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.service.notification.StatusBarNotification
import android.util.Log

/**
 * "Now playing" from any player that shows a media notification (YouTube, Spotify, YT Music, podcasts…).
 * The notification carries the player's MediaSession token; through it we follow the player directly
 * (track, play state, position, volume) and let the Mac control it.
 */
class MediaForwarder(private val ctx: Context, private val label: (String) -> String) {
    private inner class Player(var sbn: StatusBarNotification, val controller: MediaController) {
        var track = ""
        var lastSig = ""
        val callback = object : MediaController.Callback() {
            override fun onPlaybackStateChanged(state: PlaybackState?) = send(this@Player)
            override fun onMetadataChanged(metadata: MediaMetadata?) = send(this@Player)
            override fun onAudioInfoChanged(info: MediaController.PlaybackInfo) = send(this@Player)
            override fun onSessionDestroyed() = removed(sbn.key).let { }
        }
    }

    private val players = HashMap<String, Player>()
    private val main = Handler(Looper.getMainLooper())

    fun isMedia(sbn: StatusBarNotification) = token(sbn) != null

    /** A media notification was posted or updated. [quiet] = never show a banner (resend after unlock). */
    fun posted(sbn: StatusBarNotification, quiet: Boolean = false) {
        val token = token(sbn) ?: return
        val p = players[sbn.key]?.also { it.sbn = sbn } ?: Player(sbn, MediaController(ctx, token)).also {
            players[sbn.key] = it
            it.controller.registerCallback(it.callback, main)
        }
        send(p, quiet)
    }

    /** Returns true if the removed notification was a player we were following. */
    fun removed(key: String): Boolean {
        val p = players.remove(key) ?: return false
        runCatching { p.controller.unregisterCallback(p.callback) }
        return true
    }

    fun reset() {
        players.values.forEach { runCatching { it.controller.unregisterCallback(it.callback) } }
        players.clear()
    }

    private fun send(p: Player, quiet: Boolean = false) {
        // Live only, like every other notification.
        if (!Link.live || !Prefs.allows(p.sbn.packageName)) return
        val c = p.controller
        val md = c.metadata
        val ex = p.sbn.notification.extras
        val title = md?.getString(MediaMetadata.METADATA_KEY_TITLE)
            ?: ex.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        val artist = md?.getString(MediaMetadata.METADATA_KEY_ARTIST)
            ?: md?.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE)
            ?: ex.getCharSequence(Notification.EXTRA_TEXT)?.toString().orEmpty()
        if (title.isBlank()) return

        val state = c.playbackState
        val playing = state?.state == PlaybackState.STATE_PLAYING || state?.state == PlaybackState.STATE_BUFFERING
        val speed = if (playing) (state?.playbackSpeed ?: 1f) else 0f
        // PlaybackState reports position at lastPositionUpdateTime; project it to "now".
        val position = state?.let {
            if (playing && it.lastPositionUpdateTime > 0)
                it.position + ((SystemClock.elapsedRealtime() - it.lastPositionUpdateTime) * it.playbackSpeed).toLong()
            else it.position
        }?.coerceAtLeast(0) ?: 0
        val duration = md?.getLong(MediaMetadata.METADATA_KEY_DURATION)?.coerceAtLeast(0) ?: 0
        val canSeek = (state?.actions ?: 0) and PlaybackState.ACTION_SEEK_TO != 0L
        val info = c.playbackInfo
        val volume = info?.currentVolume ?: 0
        val maxVolume = info?.maxVolume ?: 0

        val track = "$title\u0000$artist"
        val newTrack = p.track != track
        // Position keeps moving while playing, so dedupe on the *reported* state, not the projected one.
        val sig = "$track|$playing|${state?.position}|${state?.lastPositionUpdateTime}|$volume|$duration"
        if (!quiet && !newTrack && sig == p.lastSig) return
        p.track = track
        p.lastSig = sig

        // Artwork only travels with a new track (or a resend); the Mac keeps it per player.
        val art = if (newTrack || quiet) artwork(md, p.sbn)?.let(NotifImages::jpeg) else null
        Link.send(
            Message.of(
                "media",
                "key" to p.sbn.key, "pkg" to p.sbn.packageName, "app" to label(p.sbn.packageName),
                "title" to title, "artist" to artist, "playing" to playing,
                "position" to position, "duration" to duration, "speed" to speed.toDouble(), "canSeek" to canSeek,
                "volume" to volume, "maxVolume" to maxVolume,
                "banner" to (newTrack && !quiet), "time" to System.currentTimeMillis(),
                payload = art ?: ByteArray(0),
            )
        )
    }

    /** From the Mac: play / pause / next / previous / seek (value = ms) / volume (value = step). */
    fun control(key: String, action: String, value: Long) {
        val c = players[key]?.controller ?: return
        val tc = c.transportControls
        try {
            when (action) {
                "play" -> tc.play()
                "pause" -> tc.pause()
                "next" -> tc.skipToNext()
                "previous" -> tc.skipToPrevious()
                "seek" -> tc.seekTo(value)
                "volume" -> c.setVolumeTo(value.toInt(), 0)
            }
        } catch (e: Exception) {
            Log.w("LinkTwo", "media $action failed: $e")
        }
    }

    private fun artwork(md: MediaMetadata?, sbn: StatusBarNotification): Bitmap? =
        md?.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
            ?: md?.getBitmap(MediaMetadata.METADATA_KEY_ART)
            ?: md?.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
            ?: sbn.notification.getLargeIcon()?.let { NotifImages.fromIcon(ctx, it) }

    @Suppress("DEPRECATION")
    private fun token(sbn: StatusBarNotification): MediaSession.Token? =
        sbn.notification.extras.get(Notification.EXTRA_MEDIA_SESSION) as? MediaSession.Token
}
