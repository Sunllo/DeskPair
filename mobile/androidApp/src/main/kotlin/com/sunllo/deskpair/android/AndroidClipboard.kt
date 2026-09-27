package com.sunllo.deskpair.android

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.graphics.ImageDecoder
import android.net.Uri
import android.util.Log
import com.sunllo.deskpair.clipboard.ClipboardBridge
import com.sunllo.deskpair.clipboard.ClipboardSync
import java.io.ByteArrayOutputStream

/**
 * This device's clipboard, in both directions.
 *
 * Android only lets the focused app read the clipboard, which suits a remote desktop client: the copy worth
 * forwarding is one the user made while looking at the session. A clip that arrives from the host while the
 * app is in the background is dropped rather than queued, because pasting it minutes later — over whatever
 * the user has copied since — is worse than not pasting it at all.
 *
 * Pictures go one way only, phone to desk. Nothing pushes the host's screenshots onto this device.
 */
class AndroidClipboard(
    private val context: Context,
    private val onLocalText: (String) -> Unit,
    private val onLocalImage: (ByteArray) -> Unit = {},
) : ClipboardBridge {

    private companion object {
        const val TAG = "DeskPair"

        /**
         * How far a picture is halved before being given up on.
         *
         * Eight halvings turn a 100-megapixel photograph into something under a megapixel, which is well
         * inside the clipboard's budget. If it still does not fit after that, the image is strange enough
         * that saying so is better than looping.
         */
        const val MaxHalvings = 8
    }

    private val resolver = context.applicationContext.contentResolver

    private val manager =
        context.applicationContext.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager

    private val listener = ClipboardManager.OnPrimaryClipChangedListener {
        // This fires for our own writes too. Filtering it here by comparing text would need a second copy
        // of the echo rules that already live in the shared module, so everything is offered and the
        // session decides — it holds the hash of what it last applied.
        offerCurrent()
    }

    fun start() {
        manager.addPrimaryClipChangedListener(listener)
    }

    fun stop() {
        manager.removePrimaryClipChangedListener(listener)
    }

    /**
     * Offers whatever is on the clipboard, as whatever it actually is.
     *
     * A picture is looked for first. `coerceToText` on an image item hands back the `content://` URI as a
     * string — with no ContentResolver it cannot do anything else — so asking for text first would send
     * the host a line of gibberish instead of the picture the user copied.
     */
    fun offerCurrent() {
        val clip = runCatching { manager.primaryClip }.getOrNull() ?: return
        if (clip.itemCount == 0) {
            return
        }

        currentImage(clip)?.let {
            onLocalImage(it)
            return
        }

        currentText(clip)?.let(onLocalText)
    }

    /** What is on the clipboard now, or null when it holds something that is not text. */
    fun currentText(clip: ClipData? = runCatching { manager.primaryClip }.getOrNull()): String? =
        runCatching {
            if (clip == null || clip.itemCount == 0) return null
            if (looksLikeImage(clip)) return null
            clip.getItemAt(0).coerceToText(null)?.toString()?.takeIf { it.isNotEmpty() }
        }.getOrElse {
            // Reading without focus throws on some versions and returns null on others; neither is worth
            // a crash.
            Log.d(TAG, "Clipboard unreadable right now", it)
            null
        }

    /** The clipboard's picture as PNG, or null when it holds none or it could not be read. */
    fun currentImage(clip: ClipData? = runCatching { manager.primaryClip }.getOrNull()): ByteArray? {
        if (clip == null || clip.itemCount == 0 || !looksLikeImage(clip)) {
            return null
        }

        val uri = clip.getItemAt(0).uri ?: return null
        return runCatching { encode(decode(uri)) }.getOrElse {
            // A content URI from another app carries a read grant only while its clip is the primary one
            // and this app has focus. Losing that race is ordinary, not an error.
            Log.d(TAG, "Could not read the copied picture", it)
            null
        }
    }

    /**
     * A picture the user chose from the photo picker, as PNG.
     *
     * The picker hands back a URI with its own read grant, so unlike a copied clip this does not depend on
     * focus or on the clipboard still holding it.
     */
    fun readPicked(uri: Uri): ByteArray? = runCatching { encode(decode(uri)) }.getOrElse {
        Log.w(TAG, "Could not read the chosen picture", it)
        null
    }

    /** Asks the description rather than the item: the MIME types are what the copying app declared. */
    private fun looksLikeImage(clip: ClipData): Boolean {
        val description = clip.description ?: return false
        for (i in 0 until description.mimeTypeCount) {
            if (description.getMimeType(i).startsWith("image/")) {
                return true
            }
        }

        return false
    }

    private fun decode(uri: Uri): Bitmap =
        ImageDecoder.decodeBitmap(ImageDecoder.createSource(resolver, uri)) { decoder, _, _ ->
            // Hardware bitmaps cannot be read back, and compress() needs to read the pixels.
            decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
            decoder.isMutableRequired = false
        }

    /**
     * PNG bytes small enough to carry, halving the picture until they are.
     *
     * The shared layer refuses anything over its limit rather than truncating it, so the shrinking has to
     * happen here — this is the side that has an imaging stack and knows what the picture is.
     */
    private fun encode(source: Bitmap): ByteArray {
        var bitmap = source
        repeat(MaxHalvings + 1) {
            val out = ByteArrayOutputStream()
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
            val png = out.toByteArray()
            if (png.size <= ClipboardSync.MAX_BYTES) {
                if (bitmap !== source) {
                    Log.i(TAG, "Copied picture scaled to ${bitmap.width}x${bitmap.height} to fit the clipboard")
                }

                return png
            }

            val half = Bitmap.createScaledBitmap(bitmap, maxOf(1, bitmap.width / 2), maxOf(1, bitmap.height / 2), true)
            if (bitmap !== source) {
                bitmap.recycle()
            }

            bitmap = half
        }

        throw IllegalStateException(context.getString(R.string.clipboard_too_large))
    }

    override fun onRemoteText(text: String) {
        runCatching {
            manager.setPrimaryClip(ClipData.newPlainText("DeskPair", text))
            Log.d(TAG, "clipboard from host: ${text.length} character(s)")
        }.onFailure {
            Log.w(TAG, "Could not put the host's clipboard on this device", it)
        }
    }
}
