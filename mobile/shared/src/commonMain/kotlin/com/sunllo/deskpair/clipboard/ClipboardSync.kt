package com.sunllo.deskpair.clipboard

import com.sunllo.deskpair.crypto.Crypto
import okio.ByteString.Companion.toByteString
import sunllo.messages.Clipboard
import sunllo.messages.ClipboardFormat
import sunllo.messages.MultiClipboards

/**
 * Keeps two clipboards in step without letting them shout at each other.
 *
 * Both sides watch their own clipboard and send what changes, so without this the first copy bounces
 * forever: we send, the host applies and its own watcher fires, it sends back, we apply, ours fires. The
 * cure is to remember the last thing sent and the last thing applied, and to refuse anything that matches
 * either. Comparing content rather than timing is what makes it reliable — a delay long enough to outlast
 * an echo is also long enough to drop a real copy someone made straight after.
 *
 * The hash has to be byte-identical to `ClipboardSync.Hash` on the C# side, because the host puts its own
 * hash on the wire and compares ours against it.
 */
public class ClipboardSync {

    // Written by whichever thread sends and read by the one that receives, so the values have to be
    // visible across both. The check and the mark are not one atomic step, and deliberately so: the worst
    // an unlucky interleaving can do is send one message the other side then recognises as its own, because
    // both ends filter. Locking a pair of hashes to avoid that would cost more than it saves.
    @kotlin.concurrent.Volatile
    private var lastSent: ByteArray? = null

    @kotlin.concurrent.Volatile
    private var lastApplied: ByteArray? = null

    /** False when this is what the peer just gave us, or what we already sent. */
    public fun shouldSend(hash: ByteArray): Boolean =
        !hash.contentEquals(lastApplied) && !hash.contentEquals(lastSent)

    /** False when this is our own send bouncing back. */
    public fun shouldApply(hash: ByteArray): Boolean =
        !hash.contentEquals(lastSent) && !hash.contentEquals(lastApplied)

    public fun markSent(hash: ByteArray) {
        lastSent = hash
    }

    public fun markApplied(hash: ByteArray) {
        lastApplied = hash
    }

    public companion object {

        /**
         * SHA-256 over every item in format order: the format and the length as little-endian int32s, then
         * the bytes. Sorting by format is what lets two sides that collected the same content in a different
         * order still agree they are holding the same thing.
         */
        public fun hash(clipboards: MultiClipboards): ByteArray {
            val buffer = okio.Buffer()
            for (item in clipboards.items.sortedBy { it.format.value }) {
                buffer.writeIntLe(item.format.value)
                buffer.writeIntLe(item.content.size)
                buffer.write(item.content)
            }
            return Crypto.sha256(buffer.readByteArray())
        }

        /**
         * The most a single clipboard message may carry, mirroring `ProtocolConstants.MaxClipboardBytes`.
         *
         * Bounding it here rather than finding out at the host means a picture too large to send is
         * refused with a reason, instead of vanishing somewhere between the two.
         */
        public const val MAX_BYTES: Int = 16 * 1024 * 1024

        /** Wraps plain text the way the desktop does: UTF-8 bytes, one CF_TEXT item, hash attached. */
        public fun textToWire(text: String): MultiClipboards =
            requireNotNull(toWire(text = text, png = null)) { "Text must not be empty." }

        /** Wraps a PNG as a single CF_IMAGE_PNG item. The bytes travel as they are; PNG says its own size. */
        public fun imageToWire(png: ByteArray): MultiClipboards =
            requireNotNull(toWire(text = null, png = png)) { "The image must not be empty." }

        /**
         * Packs whatever there is to send, or null when there is nothing that fits.
         *
         * `width`, `height` and `compressed` (proto fields 3-5) are left unset on purpose: nothing on
         * either side has ever read them, a PNG carries its own dimensions, and `compressed` names no
         * algorithm. Writing them would invent a contract rather than honour one.
         */
        public fun toWire(text: String?, png: ByteArray?): MultiClipboards? {
            val items = mutableListOf<Clipboard>()
            var total = 0

            fun add(format: ClipboardFormat, bytes: ByteArray) {
                // Skip what will not fit rather than abandoning the whole clipboard — the same rule the
                // desktop's ToWire follows, so a caption survives an oversized picture beside it.
                if (bytes.isEmpty() || total + bytes.size > MAX_BYTES) {
                    return
                }

                total += bytes.size
                items += Clipboard(format = format, content = bytes.toByteString())
            }

            text?.let { add(ClipboardFormat.CF_TEXT, it.encodeToByteArray()) }
            png?.let { add(ClipboardFormat.CF_IMAGE_PNG, it) }

            if (items.isEmpty()) {
                return null
            }

            val partial = MultiClipboards(items = items)
            return partial.copy(content_hash = hash(partial).toByteString())
        }

        /**
         * The text in an incoming clipboard, or null when it carries none.
         *
         * Only text comes back to the phone. That is a scope decision rather than a limitation: the
         * product needs a picture to travel phone-to-desk, and pushing the desk's screenshots onto a
         * phone's clipboard is a separate feature with its own bandwidth question. HTML and RTF are
         * dropped rather than mangled, and a file list is a promise this client has no transfer channel
         * to redeem — offering it would put a paste on the host's screen that could never complete.
         */
        public fun textFromWire(clipboards: MultiClipboards): String? =
            clipboards.items.firstOrNull { it.format == ClipboardFormat.CF_TEXT }
                ?.content?.utf8()
                ?.takeIf { it.isNotEmpty() }
    }
}

/**
 * The phone's own clipboard, supplied by the app because reaching it needs a Context on Android and a
 * UIPasteboard on iOS — neither of which belongs in shared code.
 */
public interface ClipboardBridge {

    /** Called when the host's clipboard changes, with the text to put on this device's clipboard. */
    public fun onRemoteText(text: String)
}
