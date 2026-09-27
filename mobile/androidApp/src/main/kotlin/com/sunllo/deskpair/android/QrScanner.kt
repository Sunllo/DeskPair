package com.sunllo.deskpair.android

import android.content.Context
import android.util.Log
import com.google.android.gms.common.moduleinstall.ModuleInstall
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import com.sunllo.deskpair.store.ConnectLink

/**
 * Reads a pairing code off a desktop's screen.
 *
 * Play Services' own scanner rather than a camera preview of our own: it brings its own UI, it is
 * downloaded on demand instead of bundled, and — the part that shows in the manifest — the scan happens in
 * its activity, so this app never declares the CAMERA permission at all. For something most people use once
 * when they set the app up, that is the right trade.
 */
object QrScanner {

    /** What came of asking for a scan. Separate from what a code says, because they fail differently. */
    sealed interface Event {
        /** The user backed out. Not a failure, and not worth a message. */
        data object Cancelled : Event

        /**
         * The scanner itself could not start — no Play Services, no camera, or the module would not
         * download. Distinct from a code that could not be read, because telling someone their code is
         * wrong when the camera never opened sends them off to fix the wrong thing.
         */
        data object Unavailable : Event

        data class Read(val scanned: ConnectLink.Scanned) : Event
    }

    /** Scans, then reports what came of it. */
    fun scan(context: Context, onResult: (Event) -> Unit) {
        val options = GmsBarcodeScannerOptions.Builder()
            .setBarcodeFormats(Barcode.FORMAT_QR_CODE)
            .enableAutoZoom()
            .build()

        GmsBarcodeScanning.getClient(context, options).startScan()
            .addOnSuccessListener { barcode -> onResult(Event.Read(ConnectLink.parse(barcode.rawValue))) }
            .addOnCanceledListener { onResult(Event.Cancelled) }
            .addOnFailureListener { error ->
                // A device with no Play Services, one with no camera, or one that could not fetch the
                // module all land here. Worth saying out loud, and worth saying accurately: the first
                // version of this reported "that is not a DeskPair code", which sent people off to
                // re-generate a code that was never the problem.
                Log.w(TAG, "Code scanner unavailable", error)
                onResult(Event.Unavailable)
            }
    }

    /**
     * Asks Play Services to fetch the scanner module ahead of time.
     *
     * Without this the first scan stalls on a download behind an opaque spinner. Failure is ignored on
     * purpose — this is an optimisation, and [scan] reports the real problem if there is one.
     */
    fun warmUp(context: Context) {
        runCatching {
            ModuleInstall.getClient(context)
                .installModules(
                    com.google.android.gms.common.moduleinstall.ModuleInstallRequest.newBuilder()
                        .addApi(GmsBarcodeScanning.getClient(context))
                        .build(),
                )
        }.onFailure { Log.d(TAG, "Could not pre-install the scanner module", it) }
    }

    private const val TAG = "DeskPairScanner"
}
