package com.phonecam.camera

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.view.Surface
import java.nio.ByteBuffer

class VideoEncoder(
    private val width: Int,
    private val height: Int,
    private val fps: Int,
    private val bitrate: Int,
    private val onEncodedPacket: (
        data: ByteBuffer,
        size: Int,
        timestampUs: Long,
        isConfig: Boolean,
        isKeyframe: Boolean
    ) -> Unit,
    private val onError: (Throwable) -> Unit
) {
    private val thread = HandlerThread("VideoEncoderThread").apply { start() }
    private val handler = Handler(thread.looper)

    @Volatile
    private var codec: MediaCodec? = null

    @Volatile
    private var stopped = false

    lateinit var inputSurface: Surface
        private set

    fun start() {
        val encoder = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        encoder.setCallback(object : MediaCodec.Callback() {
            override fun onInputBufferAvailable(codec: MediaCodec, index: Int) = Unit

            override fun onOutputBufferAvailable(
                codec: MediaCodec,
                index: Int,
                info: MediaCodec.BufferInfo
            ) {
                if (stopped) return
                try {
                    if (info.size > 0 && (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM) == 0) {
                        val buffer = codec.getOutputBuffer(index)
                        if (buffer != null) {
                            buffer.position(info.offset)
                            buffer.limit(info.offset + info.size)
                            val tsUs = SystemClock.elapsedRealtimeNanos() / 1000L
                            val isConfig = (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0
                            val isKeyframe = (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0
                            onEncodedPacket(buffer, info.size, tsUs, isConfig, isKeyframe)
                        }
                    }
                    codec.releaseOutputBuffer(index, false)
                } catch (t: Throwable) {
                    if (!stopped) onError(t)
                }
            }

            override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
                if (!stopped) onError(e)
            }

            override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) = Unit
        }, handler)

        configureWithLowLatencyFallback(encoder)
        inputSurface = encoder.createInputSurface()
        encoder.start()
        codec = encoder
    }

    private fun configureWithLowLatencyFallback(encoder: MediaCodec) {
        val format = createBaseFormat()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            format.setInteger(MediaFormat.KEY_LATENCY, 0)
        }
        try {
            encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        } catch (_: MediaCodec.CodecException) {
            // Some vendor encoders reject KEY_LATENCY=0; retry without it.
            val fallback = createBaseFormat()
            encoder.configure(fallback, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        }
    }

    private fun createBaseFormat(): MediaFormat =
        MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(
                MediaFormat.KEY_COLOR_FORMAT,
                MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface
            )
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
            setInteger(
                MediaFormat.KEY_BITRATE_MODE,
                MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR
            )
            setInteger(MediaFormat.KEY_PRIORITY, 0)
        }

    fun requestSyncFrame() {
        val c = codec ?: return
        if (stopped) return
        handler.post {
            try {
                if (!stopped) {
                    val params = Bundle().apply {
                        putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0)
                    }
                    c.setParameters(params)
                }
            } catch (_: Throwable) {
            }
        }
    }

    fun setBitrate(newBitrate: Int) {
        val c = codec ?: return
        if (stopped) return
        handler.post {
            try {
                if (!stopped) {
                    val params = Bundle().apply {
                        putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, newBitrate)
                    }
                    c.setParameters(params)
                }
            } catch (_: Throwable) {
            }
        }
    }

    fun stop() {
        stopped = true
        val c = codec
        codec = null
        try {
            c?.stop()
        } catch (_: Throwable) {
        }
        try {
            c?.release()
        } catch (_: Throwable) {
        }
        try {
            if (::inputSurface.isInitialized) inputSurface.release()
        } catch (_: Throwable) {
        }
        thread.quitSafely()
    }
}

