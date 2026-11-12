package com.phonecam.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

object Protocol {
    const val MAGIC: Short = 0x5043 // "PC"
    const val VERSION: Byte = 1
    const val HEADER_SIZE = 24
    const val MAX_PAYLOAD_BYTES = 16 * 1024 * 1024
    const val DEFAULT_PORT = 18420
    const val DISCOVERY_PORT = 18421
    const val DISCOVERY_PROBE = "PHONECAM_DISCOVER_V1"
    const val DISCOVERY_REPLY_PREFIX = "PHONECAM_HERE_V1|"

    // Phone -> PC
    const val TYPE_VIDEO_FRAME: Byte = 0x01
    const val TYPE_CODEC_CONFIG: Byte = 0x02
    const val TYPE_STREAM_INFO: Byte = 0x03

    // PC -> Phone
    const val TYPE_REQUEST_KEYFRAME: Byte = 0x10
    const val TYPE_PING: Byte = 0x11
    const val TYPE_PONG: Byte = 0x12
    const val TYPE_SET_CONFIG: Byte = 0x13

    const val FLAG_KEYFRAME: Short = 0x0001
    const val FLAG_EOS: Short = 0x0002

    const val CODEC_H264: Byte = 1

    const val CAMERA_FACING_BACK = 0
    const val CAMERA_FACING_FRONT = 1
    const val CAMERA_FACING_UNCHANGED = 0xFF

    data class ConfigUpdate(
        val width: Int,
        val height: Int,
        val fps: Int,
        val bitrate: Long,
        val cameraFacing: Int,
        val flags: Int,
        val zoomX100: Int
    )

    fun readSetConfigPayload(src: ByteArray, length: Int): ConfigUpdate? {
        if (length < 16) return null
        val buf = ByteBuffer.wrap(src, 0, length).order(ByteOrder.BIG_ENDIAN)
        val width = buf.short.toInt() and 0xFFFF
        val height = buf.short.toInt() and 0xFFFF
        val fps = buf.short.toInt() and 0xFFFF
        val bitrate = buf.int.toLong() and 0xFFFFFFFFL
        val facing = buf.get().toInt() and 0xFF
        val flags = buf.get().toInt() and 0xFF
        val zoomX100 = buf.short.toInt() and 0xFFFF
        return ConfigUpdate(width, height, fps, bitrate, facing, flags, zoomX100)
    }

    fun writeHeader(
        dst: ByteBuffer,
        type: Byte,
        flags: Short,
        sequence: Int,
        payloadLength: Int,
        timestampUs: Long
    ) {
        dst.clear()
        dst.order(ByteOrder.BIG_ENDIAN)
        dst.putShort(MAGIC)
        dst.put(VERSION)
        dst.put(type)
        dst.putShort(flags)
        dst.putShort(0) // reserved
        dst.putInt(sequence)
        dst.putInt(payloadLength)
        dst.putLong(timestampUs)
        dst.flip()
    }

    fun writeStreamInfoPayload(
        dst: ByteBuffer,
        width: Int,
        height: Int,
        fps: Int,
        codec: Byte,
        rotationQuarters: Int,
        bitrate: Int
    ) {
        dst.clear()
        dst.order(ByteOrder.BIG_ENDIAN)
        dst.putInt(width)
        dst.putInt(height)
        dst.putShort(fps.toShort())
        dst.put(codec)
        dst.put(rotationQuarters.toByte())
        dst.putInt(bitrate)
        dst.flip()
    }
}

