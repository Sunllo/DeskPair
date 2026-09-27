package com.sunllo.deskpair.crypto

import com.sunllo.deskpair.framing.writeUInt32Le

/**
 * The controller half of the peer handshake, mirroring `DeskPair.Protocol.Crypto.Handshake`.
 *
 * ControllerHello goes out in the clear, HostHello comes back signed, and from that point every frame in
 * both directions is encrypted. The signature binds the host's long-term identity to this session's
 * ephemeral keys and nonces, so neither a replay nor an impersonation survives.
 */
internal object Handshake {

    const val NONCE_BYTES = 32

    private val TRANSCRIPT_LABEL = "SunlloHS1".encodeToByteArray()
    private val INFO_C2H = "sunllo/v1/c2h".encodeToByteArray()
    private val INFO_H2C = "sunllo/v1/h2c".encodeToByteArray()
    private val INFO_IV_C2H = "sunllo/v1/iv/c2h".encodeToByteArray()
    private val INFO_IV_H2C = "sunllo/v1/iv/h2c".encodeToByteArray()
    private val INFO_MEDIA_C2H = "sunllo/v1/media/c2h".encodeToByteArray()
    private val INFO_MEDIA_H2C = "sunllo/v1/media/h2c".encodeToByteArray()
    private val INFO_MEDIA_IV_C2H = "sunllo/v1/media/iv/c2h".encodeToByteArray()
    private val INFO_MEDIA_IV_H2C = "sunllo/v1/media/iv/h2c".encodeToByteArray()

    const val KEY_BYTES = 32
    const val IV_PREFIX_BYTES = 4

    /**
     * The bytes the host signs: the label with no length prefix, then six fields each prefixed by its length
     * as a uint32 little-endian. The prefixes are what stop a value being shifted from one field into the
     * next — without them a host id and an identity key could be re-cut to say something different.
     */
    fun transcript(
        hostId: String,
        identityPk: ByteArray,
        hostEphemeralPk: ByteArray,
        hostNonce: ByteArray,
        controllerEphemeralPk: ByteArray,
        controllerNonce: ByteArray,
    ): ByteArray {
        val buffer = ArrayList<Byte>(256)
        fun append(bytes: ByteArray) {
            bytes.forEach { buffer.add(it) }
        }

        fun appendField(field: ByteArray) {
            val length = ByteArray(4)
            length.writeUInt32Le(0, field.size.toUInt())
            append(length)
            append(field)
        }

        append(TRANSCRIPT_LABEL)
        appendField(hostId.encodeToByteArray())
        appendField(identityPk)
        appendField(hostEphemeralPk)
        appendField(hostNonce)
        appendField(controllerEphemeralPk)
        appendField(controllerNonce)

        return Crypto.sha256(buffer.toByteArray())
    }

    /**
     * Derives this side's keys. The salt is the controller nonce followed by the host nonce, in that order,
     * whichever side is deriving.
     */
    fun derive(
        ownPrivateKey: EcdhPrivateKey,
        peerEphemeralSpki: ByteArray,
        controllerNonce: ByteArray,
        hostNonce: ByteArray,
    ): SessionKeys {
        val secret = Crypto.ecdhRawSecret(ownPrivateKey, peerEphemeralSpki)
        val prk = Hkdf.extract(salt = controllerNonce + hostNonce, inputKeyMaterial = secret)
        secret.fill(0)

        return SessionKeys(
            txKey = Hkdf.expand(prk, INFO_C2H, KEY_BYTES),
            txIvPrefix = Hkdf.expand(prk, INFO_IV_C2H, IV_PREFIX_BYTES),
            rxKey = Hkdf.expand(prk, INFO_H2C, KEY_BYTES),
            rxIvPrefix = Hkdf.expand(prk, INFO_IV_H2C, IV_PREFIX_BYTES),
            mediaTxKey = Hkdf.expand(prk, INFO_MEDIA_C2H, KEY_BYTES),
            mediaTxIvPrefix = Hkdf.expand(prk, INFO_MEDIA_IV_C2H, IV_PREFIX_BYTES),
            mediaRxKey = Hkdf.expand(prk, INFO_MEDIA_H2C, KEY_BYTES),
            mediaRxIvPrefix = Hkdf.expand(prk, INFO_MEDIA_IV_H2C, IV_PREFIX_BYTES),
        ).also { prk.fill(0) }
    }
}

/**
 * Directional keys, named from the controller's point of view: [txKey] encrypts what it sends, [rxKey]
 * decrypts what it receives. The media pair is derived even when UDP is declined, because they are
 * independent HKDF outputs and deriving them later would mean re-running the handshake.
 */
internal class SessionKeys(
    val txKey: ByteArray,
    val txIvPrefix: ByteArray,
    val rxKey: ByteArray,
    val rxIvPrefix: ByteArray,
    val mediaTxKey: ByteArray,
    val mediaTxIvPrefix: ByteArray,
    val mediaRxKey: ByteArray,
    val mediaRxIvPrefix: ByteArray,
)
