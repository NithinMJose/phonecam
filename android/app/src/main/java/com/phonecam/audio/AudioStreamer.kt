package com.phonecam.audio

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.NoiseSuppressor
import android.os.Process
import android.os.SystemClock
import android.util.Log
import com.phonecam.protocol.Protocol
import java.util.concurrent.atomic.AtomicBoolean

class AudioStreamer(
    private val onAudioPacket: (data: ByteArray, length: Int, timestampUs: Long) -> Unit,
    private val onError: (String) -> Unit
) {
    private val running = AtomicBoolean(false)
    private var thread: Thread? = null
    private var recorder: AudioRecord? = null
    private var aec: AcousticEchoCanceler? = null
    private var ns: NoiseSuppressor? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        thread = Thread(::captureLoop, "PhoneCamAudio").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        try {
            recorder?.stop()
        } catch (_: Throwable) {
        }
        try {
            thread?.join(500)
        } catch (_: InterruptedException) {
        }
        thread = null
        releaseRecorder()
    }

    @SuppressLint("MissingPermission")
    private fun createAudioRecord(chunkBytes: Int): AudioRecord? {
        val minBuf = AudioRecord.getMinBufferSize(
            Protocol.AUDIO_SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT
        )
        if (minBuf <= 0) return null
        val bufferBytes = maxOf(minBuf * 2, chunkBytes * 4)

        val sources = intArrayOf(
            MediaRecorder.AudioSource.VOICE_COMMUNICATION,
            MediaRecorder.AudioSource.MIC
        )
        for (source in sources) {
            try {
                val ar = AudioRecord(
                    source,
                    Protocol.AUDIO_SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    bufferBytes
                )
                if (ar.state == AudioRecord.STATE_INITIALIZED) {
                    return ar
                }
                ar.release()
            } catch (_: Throwable) {
            }
        }
        return null
    }

    private fun captureLoop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO)

        val samplesPerChunk = Protocol.AUDIO_SAMPLE_RATE / 50 // 20 ms = 960 samples
        val pcmBytesPerChunk = samplesPerChunk * 2 // 1920 bytes mono PCM16
        val packetBuf = ByteArray(Protocol.AUDIO_FRAME_HEADER_SIZE + pcmBytesPerChunk)
        Protocol.writeAudioFrameHeader(packetBuf)

        val ar = createAudioRecord(pcmBytesPerChunk)
        if (ar == null) {
            running.set(false)
            onError("Microphone AudioRecord failed to initialize")
            return
        }
        recorder = ar

        try {
            if (AcousticEchoCanceler.isAvailable()) {
                aec = AcousticEchoCanceler.create(ar.audioSessionId)?.apply { enabled = true }
            }
        } catch (_: Throwable) {
        }
        try {
            if (NoiseSuppressor.isAvailable()) {
                ns = NoiseSuppressor.create(ar.audioSessionId)?.apply { enabled = true }
            }
        } catch (_: Throwable) {
        }

        try {
            ar.startRecording()
            Log.i("PhoneCam", "AudioStreamer started (${Protocol.AUDIO_SAMPLE_RATE}Hz mono PCM16)")

            while (running.get()) {
                var offset = Protocol.AUDIO_FRAME_HEADER_SIZE
                var remaining = pcmBytesPerChunk
                while (remaining > 0 && running.get()) {
                    val read = ar.read(packetBuf, offset, remaining)
                    if (read <= 0) {
                        if (!running.get()) break
                        if (read == AudioRecord.ERROR_INVALID_OPERATION || read == AudioRecord.ERROR_DEAD_OBJECT) {
                            onError("Microphone capture stopped unexpectedly ($read)")
                            running.set(false)
                            break
                        }
                        break
                    }
                    offset += read
                    remaining -= read
                }
                if (!running.get() || remaining > 0) continue

                val tsUs = SystemClock.elapsedRealtimeNanos() / 1000L
                onAudioPacket(packetBuf, packetBuf.size, tsUs)
            }
        } catch (t: Throwable) {
            if (running.get()) {
                onError("Microphone error: ${t.message}")
            }
        } finally {
            running.set(false)
            releaseRecorder()
            Log.i("PhoneCam", "AudioStreamer stopped")
        }
    }

    private fun releaseRecorder() {
        try {
            aec?.release()
        } catch (_: Throwable) {
        }
        aec = null
        try {
            ns?.release()
        } catch (_: Throwable) {
        }
        ns = null
        try {
            recorder?.release()
        } catch (_: Throwable) {
        }
        recorder = null
    }
}

