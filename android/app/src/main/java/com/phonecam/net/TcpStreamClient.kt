package com.phonecam.net

import android.os.SystemClock
import com.phonecam.protocol.Protocol
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.EOFException
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.ArrayDeque
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

class TcpStreamClient(
    private val host: String,
    private val port: Int,
    initialWidth: Int,
    initialHeight: Int,
    initialFps: Int,
    initialRotationQuarters: Int,
    initialBitrate: Int,
    private val autoReconnect: Boolean = true,
    private val onConnecting: (attempt: Int) -> Unit,
    private val onConnected: () -> Unit,
    private val onRequestKeyframe: () -> Unit,
    private val onSetConfig: (Protocol.ConfigUpdate) -> Unit,
    private val onStatsUpdate: (packetsSent: Long, bytesSent: Long, dropped: Long) -> Unit,
    private val onReconnecting: (reason: String, delayMs: Long) -> Unit,
    private val onStopped: (String?) -> Unit
) {
    private class FrameSlot(capacity: Int = 256 * 1024) {
        var data = ByteArray(capacity)
        var length = 0
        var timestampUs = 0L
        var isConfig = false
        var isKeyframe = false

        fun copyFrom(src: ByteBuffer, size: Int) {
            if (size > data.size) {
                data = ByteArray(size)
            }
            src.get(data, 0, size)
            length = size
        }
    }

    private data class PongReply(val pcSendTsUs: Long, val phoneRxTsUs: Long)
    private data class StreamInfoData(
        val width: Int,
        val height: Int,
        val fps: Int,
        val rotationQuarters: Int,
        val bitrate: Int
    )

    private val running = AtomicBoolean(false)
    private val connected = AtomicBoolean(false)
    private val waitingForKeyframe = AtomicBoolean(false)

    private val lock = ReentrantLock()
    private val hasWork = lock.newCondition()
    private val freePool = ArrayDeque<FrameSlot>(4).apply {
        repeat(4) { addLast(FrameSlot()) }
    }
    private val sendQueue = ArrayDeque<FrameSlot>(4)
    private val pongQueue = ArrayDeque<PongReply>(4)
    private val pendingStreamInfo = ArrayDeque<StreamInfoData>(2)

    @Volatile
    private var latestStreamInfo = StreamInfoData(
        initialWidth, initialHeight, initialFps, initialRotationQuarters, initialBitrate
    )

    @Volatile
    private var latestCodecConfig: ByteArray? = null

    @Volatile
    private var socket: Socket? = null

    private val sequence = AtomicInteger(0)
    private val packetsSent = AtomicLong(0)
    private val bytesSent = AtomicLong(0)
    private val droppedFrames = AtomicLong(0)

    private var writerThread: Thread? = null
    private var readerThread: Thread? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        writerThread = Thread(::runWriterLoop, "TcpStreamWriter").apply { start() }
    }

    fun sendStreamInfo(
        width: Int,
        height: Int,
        fps: Int,
        rotationQuarters: Int,
        bitrate: Int
    ) {
        val info = StreamInfoData(width, height, fps, rotationQuarters, bitrate)
        latestStreamInfo = info
        if (!connected.get()) return
        lock.withLock {
            pendingStreamInfo.addLast(info)
            hasWork.signal()
        }
    }

    fun enqueueEncodedFrame(
        buffer: ByteBuffer,
        size: Int,
        timestampUs: Long,
        isConfig: Boolean,
        isKeyframe: Boolean
    ) {
        if (!running.get()) return

        if (isConfig) {
            val copy = ByteArray(size)
            buffer.get(copy, 0, size)
            latestCodecConfig = copy
            if (!connected.get()) return
            lock.withLock {
                val slot = freePool.pollFirst() ?: FrameSlot(size)
                if (size > slot.data.size) slot.data = ByteArray(size)
                System.arraycopy(copy, 0, slot.data, 0, size)
                slot.length = size
                slot.timestampUs = timestampUs
                slot.isConfig = true
                slot.isKeyframe = false
                sendQueue.addLast(slot)
                hasWork.signal()
            }
            return
        }

        if (!connected.get()) return

        if (waitingForKeyframe.get()) {
            if (!isKeyframe) {
                droppedFrames.incrementAndGet()
                return
            }
            waitingForKeyframe.set(false)
        }

        lock.withLock {
            if (sendQueue.size >= 3) {
                val kept = ArrayDeque<FrameSlot>(3)
                while (sendQueue.isNotEmpty()) {
                    val queued = sendQueue.removeFirst()
                    if (queued.isConfig) {
                        kept.addLast(queued)
                    } else {
                        freePool.addLast(queued)
                        droppedFrames.incrementAndGet()
                    }
                }
                sendQueue.addAll(kept)

                if (!isKeyframe) {
                    waitingForKeyframe.set(true)
                    droppedFrames.incrementAndGet()
                    onRequestKeyframe()
                    return
                }
            }

            val slot = freePool.pollFirst() ?: FrameSlot(size)
            slot.copyFrom(buffer, size)
            slot.timestampUs = timestampUs
            slot.isConfig = false
            slot.isKeyframe = isKeyframe
            sendQueue.addLast(slot)
            hasWork.signal()
        }
    }

    private fun runWriterLoop() {
        val headerBuf = ByteBuffer.allocate(Protocol.HEADER_SIZE)
        val infoBuf = ByteBuffer.allocate(16)
        val pongPayload = ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN)

        var attempt = 0
        var backoffMs = 1000L
        var fatalReason: String? = null

        while (running.get()) {
            attempt++
            onConnecting(attempt)

            var sock: Socket? = null
            var reader: Thread? = null
            var disconnectReason: String? = null

            try {
                val targetHost = this.host
                val targetPort = this.port
                sock = Socket().apply {
                    tcpNoDelay = true
                    sendBufferSize = 256 * 1024
                    soTimeout = 4500
                    connect(InetSocketAddress(targetHost, targetPort), 3000)
                }
                socket = sock
                backoffMs = 1000L
                attempt = 0

                lock.withLock {
                    while (sendQueue.isNotEmpty()) freePool.addLast(sendQueue.removeFirst())
                    pongQueue.clear()
                    pendingStreamInfo.clear()
                    waitingForKeyframe.set(true)
                }

                connected.set(true)
                onConnected()

                val out = BufferedOutputStream(sock.getOutputStream(), 64 * 1024)
                reader = Thread({
                    disconnectReason = runReaderLoop(sock)
                    closeSocketQuietly(sock)
                    lock.withLock { hasWork.signalAll() }
                }, "TcpStreamReader").apply {
                    readerThread = this
                    start()
                }

                val initialInfo = latestStreamInfo
                Protocol.writeStreamInfoPayload(
                    infoBuf,
                    initialInfo.width,
                    initialInfo.height,
                    initialInfo.fps,
                    Protocol.CODEC_H264,
                    initialInfo.rotationQuarters,
                    initialInfo.bitrate
                )
                writePacket(
                    out, headerBuf, Protocol.TYPE_STREAM_INFO, 0,
                    SystemClock.elapsedRealtimeNanos() / 1000L, infoBuf.array(), 16
                )

                latestCodecConfig?.let { spsPps ->
                    writePacket(
                        out, headerBuf, Protocol.TYPE_CODEC_CONFIG, 0,
                        SystemClock.elapsedRealtimeNanos() / 1000L, spsPps, spsPps.size
                    )
                }
                out.flush()
                onRequestKeyframe()

                var lastStatsTimeMs = SystemClock.elapsedRealtime()

                while (running.get() && disconnectReason == null) {
                    var pong: PongReply? = null
                    var streamInfo: StreamInfoData? = null
                    var slot: FrameSlot? = null

                    lock.withLock {
                        while (running.get() && disconnectReason == null &&
                            pongQueue.isEmpty() && pendingStreamInfo.isEmpty() && sendQueue.isEmpty()
                        ) {
                            hasWork.await()
                        }
                        if (!running.get() || disconnectReason != null) break
                        pong = pongQueue.pollFirst()
                        if (pong == null) {
                            streamInfo = pendingStreamInfo.pollFirst()
                        }
                        if (pong == null && streamInfo == null) {
                            slot = sendQueue.pollFirst()
                        }
                    }

                    if (pong != null) {
                        pongPayload.clear()
                        pongPayload.putLong(pong!!.phoneRxTsUs)
                        writePacket(
                            out, headerBuf, Protocol.TYPE_PONG, 0,
                            pong!!.pcSendTsUs, pongPayload.array(), 8
                        )
                        out.flush()
                        continue
                    }

                    if (streamInfo != null) {
                        val si = streamInfo!!
                        Protocol.writeStreamInfoPayload(
                            infoBuf, si.width, si.height, si.fps, Protocol.CODEC_H264, si.rotationQuarters, si.bitrate
                        )
                        writePacket(
                            out, headerBuf, Protocol.TYPE_STREAM_INFO, 0,
                            SystemClock.elapsedRealtimeNanos() / 1000L, infoBuf.array(), 16
                        )
                        out.flush()
                        continue
                    }

                    val currentSlot = slot ?: continue
                    try {
                        val type = if (currentSlot.isConfig) Protocol.TYPE_CODEC_CONFIG else Protocol.TYPE_VIDEO_FRAME
                        val flags = if (currentSlot.isKeyframe) Protocol.FLAG_KEYFRAME else 0.toShort()
                        writePacket(
                            out, headerBuf, type, flags,
                            currentSlot.timestampUs, currentSlot.data, currentSlot.length
                        )
                        out.flush()

                        val pkts = packetsSent.incrementAndGet()
                        val bytes = bytesSent.addAndGet((Protocol.HEADER_SIZE + currentSlot.length).toLong())
                        val nowMs = SystemClock.elapsedRealtime()
                        if (nowMs - lastStatsTimeMs >= 500) {
                            lastStatsTimeMs = nowMs
                            onStatsUpdate(pkts, bytes, droppedFrames.get())
                        }
                    } finally {
                        lock.withLock {
                            if (freePool.size < 6) {
                                freePool.addLast(currentSlot)
                            }
                        }
                    }
                }
            } catch (t: Throwable) {
                disconnectReason = formatDisconnectReason(t)
            } finally {
                connected.set(false)
                closeSocketQuietly(sock)
                socket = null
                try {
                    reader?.join(500)
                } catch (_: InterruptedException) {
                }
                readerThread = null
                lock.withLock {
                    while (sendQueue.isNotEmpty()) freePool.addLast(sendQueue.removeFirst())
                    pongQueue.clear()
                    pendingStreamInfo.clear()
                }
            }

            if (!running.get()) break

            val reason = disconnectReason ?: "Connection lost"
            if (!autoReconnect) {
                fatalReason = reason
                break
            }

            onReconnecting(reason, backoffMs)
            try {
                Thread.sleep(backoffMs)
            } catch (_: InterruptedException) {
                if (!running.get()) break
            }
            backoffMs = (backoffMs * 2).coerceAtMost(5000L)
        }

        onStopped(fatalReason)
    }

    private fun runReaderLoop(sock: Socket): String {
        val headerBytes = ByteArray(Protocol.HEADER_SIZE)
        val headerBuf = ByteBuffer.wrap(headerBytes).order(ByteOrder.BIG_ENDIAN)
        var rxPayload = ByteArray(256)
        return try {
            val input = DataInputStream(sock.getInputStream())
            while (running.get()) {
                input.readFully(headerBytes)
                headerBuf.clear()
                val magic = headerBuf.short
                val version = headerBuf.get()
                val type = headerBuf.get()
                headerBuf.short
                headerBuf.short
                headerBuf.int
                val payloadLen = headerBuf.int
                val timestampUs = headerBuf.long

                if (magic != Protocol.MAGIC || version != Protocol.VERSION) {
                    return "Protocol header mismatch"
                }
                if (payloadLen < 0 || payloadLen > Protocol.MAX_PAYLOAD_BYTES) {
                    return "Invalid payload length: $payloadLen"
                }
                if (payloadLen > 0) {
                    if (payloadLen > rxPayload.size) {
                        rxPayload = ByteArray(payloadLen)
                    }
                    input.readFully(rxPayload, 0, payloadLen)
                }

                when (type) {
                    Protocol.TYPE_REQUEST_KEYFRAME -> {
                        latestCodecConfig?.let { spsPps ->
                            lock.withLock {
                                val slot = freePool.pollFirst() ?: FrameSlot(spsPps.size)
                                if (spsPps.size > slot.data.size) slot.data = ByteArray(spsPps.size)
                                System.arraycopy(spsPps, 0, slot.data, 0, spsPps.size)
                                slot.length = spsPps.size
                                slot.timestampUs = SystemClock.elapsedRealtimeNanos() / 1000L
                                slot.isConfig = true
                                slot.isKeyframe = false
                                sendQueue.addFirst(slot)
                                hasWork.signal()
                            }
                        }
                        onRequestKeyframe()
                    }
                    Protocol.TYPE_PING -> {
                        val rxTs = SystemClock.elapsedRealtimeNanos() / 1000L
                        lock.withLock {
                            pongQueue.addLast(PongReply(timestampUs, rxTs))
                            hasWork.signal()
                        }
                    }
                    Protocol.TYPE_SET_CONFIG -> {
                        Protocol.readSetConfigPayload(rxPayload, payloadLen)?.let { cfg ->
                            onSetConfig(cfg)
                        }
                    }
                }
            }
            "Stopped"
        } catch (_: EOFException) {
            "PC closed connection"
        } catch (t: Throwable) {
            t.message ?: "Socket read error"
        }
    }

    private fun writePacket(
        out: BufferedOutputStream,
        headerBuf: ByteBuffer,
        type: Byte,
        flags: Short,
        timestampUs: Long,
        payload: ByteArray,
        payloadLength: Int
    ) {
        Protocol.writeHeader(
            headerBuf,
            type = type,
            flags = flags,
            sequence = sequence.getAndIncrement(),
            payloadLength = payloadLength,
            timestampUs = timestampUs
        )
        out.write(headerBuf.array(), 0, Protocol.HEADER_SIZE)
        if (payloadLength > 0) {
            out.write(payload, 0, payloadLength)
        }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        connected.set(false)
        closeSocketQuietly(socket)
        socket = null
        writerThread?.interrupt()
        lock.withLock {
            hasWork.signalAll()
            while (sendQueue.isNotEmpty()) {
                freePool.addLast(sendQueue.removeFirst())
            }
            pongQueue.clear()
            pendingStreamInfo.clear()
        }
    }

    private fun formatDisconnectReason(t: Throwable): String {
        val raw = t.message ?: t.javaClass.simpleName
        return when {
            raw.contains("ECONNREFUSED", ignoreCase = true) ->
                if (host == "127.0.0.1") {
                    "PC not listening (start PhoneCam or tap USB Re-bind)"
                } else {
                    "Connection refused by $host:$port (check Windows PhoneCam & Firewall)"
                }
            raw.contains("timed out", ignoreCase = true) || t is java.net.SocketTimeoutException ->
                "Timed out reaching $host:$port (check WiFi / Firewall)"
            else -> raw
        }
    }

    private fun closeSocketQuietly(s: Socket?) {
        try {
            s?.close()
        } catch (_: Throwable) {
        }
    }
}
