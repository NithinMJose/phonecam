package com.phonecam.stream

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.util.Log
import android.view.OrientationEventListener
import androidx.core.app.NotificationCompat
import com.phonecam.MainActivity
import com.phonecam.camera.Camera2Controller
import com.phonecam.camera.VideoEncoder
import com.phonecam.net.TcpStreamClient
import com.phonecam.protocol.Protocol
import kotlinx.coroutines.flow.MutableStateFlow

data class StreamStatus(
    val isRunning: Boolean = false,
    val isConnected: Boolean = false,
    val useFrontCamera: Boolean = false,
    val statusText: String = "Idle",
    val width: Int = 1280,
    val height: Int = 720,
    val fps: Int = 30,
    val bitrate: Int = 6_000_000,
    val zoom: Float = 1.0f,
    val mbps: Double = 0.0,
    val packetsSent: Long = 0,
    val droppedFrames: Long = 0,
    val errorText: String? = null
)

class CameraStreamService : Service() {

    companion object {
        const val ACTION_START = "com.phonecam.action.START"
        const val ACTION_STOP = "com.phonecam.action.STOP"
        const val ACTION_SWITCH_CAMERA = "com.phonecam.action.SWITCH_CAMERA"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_USE_FRONT = "use_front"

        private const val CHANNEL_ID = "phonecam_stream"
        private const val NOTIFICATION_ID = 1001

        val state = MutableStateFlow(StreamStatus())
    }

    private val mainHandler = Handler(Looper.getMainLooper())
    private var wakeLock: PowerManager.WakeLock? = null
    private var encoder: VideoEncoder? = null
    private var cameraController: Camera2Controller? = null
    private var tcpClient: TcpStreamClient? = null

    private var currentHost = "127.0.0.1"
    private var currentPort = Protocol.DEFAULT_PORT
    private var useFrontCamera = false
    private var targetWidth = 1280
    private var targetHeight = 720
    private var targetFps = 30
    private var targetBitrate = 6_000_000
    private var currentZoom = 1.0f

    private var currentWidth = targetWidth
    private var currentHeight = targetHeight
    private var currentSensorOrientation = 90
    private var lastDeviceAngle = 0
    private var currentRotationQuarters = 1
    private var orientationListener: OrientationEventListener? = null

    private var lastBytes = 0L
    private var lastRateCheckMs = 0L

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopPipeline(error = null)
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_START -> {
                currentHost = intent.getStringExtra(EXTRA_HOST)?.ifBlank { "127.0.0.1" } ?: "127.0.0.1"
                currentPort = intent.getIntExtra(EXTRA_PORT, Protocol.DEFAULT_PORT)
                useFrontCamera = intent.getBooleanExtra(EXTRA_USE_FRONT, state.value.useFrontCamera)
                startForegroundNotification("Starting stream to $currentHost:$currentPort...")
                startPipeline(currentHost, currentPort)
            }
            ACTION_SWITCH_CAMERA -> {
                val wantFront = intent.getBooleanExtra(EXTRA_USE_FRONT, !useFrontCamera)
                if (wantFront != useFrontCamera) {
                    applyRemoteOrLocalConfig(
                        newWidth = targetWidth,
                        newHeight = targetHeight,
                        newFps = targetFps,
                        newBitrate = targetBitrate,
                        newFront = wantFront,
                        newZoom = currentZoom
                    )
                }
            }
        }
        return START_NOT_STICKY
    }

    private fun startForegroundNotification(contentText: String) {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val channel = NotificationChannel(
            CHANNEL_ID,
            "PhoneCam Streaming",
            NotificationManager.IMPORTANCE_LOW
        )
        nm.createNotificationChannel(channel)

        startForeground(
            NOTIFICATION_ID,
            buildNotification(contentText),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA
        )
    }

    private fun updateNotification(contentText: String) {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.notify(NOTIFICATION_ID, buildNotification(contentText))
    }

    private fun buildNotification(contentText: String): Notification {
        val openIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val stopIntent = PendingIntent.getService(
            this,
            1,
            Intent(this, CameraStreamService::class.java).apply { action = ACTION_STOP },
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("PhoneCam Active")
            .setContentText(contentText)
            .setSmallIcon(android.R.drawable.presence_video_online)
            .setOngoing(true)
            .setContentIntent(openIntent)
            .addAction(android.R.drawable.ic_media_pause, "Stop", stopIntent)
            .build()
    }

    private fun startPipeline(host: String, port: Int) {
        stopPipeline(error = null)
        state.value = state.value.copy(
            isRunning = true,
            isConnected = false,
            useFrontCamera = useFrontCamera,
            statusText = "Connecting to $host:$port...",
            width = targetWidth,
            height = targetHeight,
            fps = targetFps,
            bitrate = targetBitrate,
            zoom = currentZoom,
            errorText = null
        )

        try {
            Log.i("PhoneCam", "Starting pipeline -> $host:$port (front=$useFrontCamera)")
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PhoneCam::StreamWakeLock")
                .apply { acquire() }

            val cam = Camera2Controller(this) { err ->
                Log.e("PhoneCam", "Camera error", err)
                stopPipeline("Camera error: ${err.message}")
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
            val camConfig = cam.selectCamera(useFrontCamera, targetWidth, targetHeight, targetFps)
            val width = camConfig.size.width
            val height = camConfig.size.height
            currentWidth = width
            currentHeight = height
            currentSensorOrientation = camConfig.sensorOrientation
            val rotQuarters = computeRotationQuarters(currentSensorOrientation, useFrontCamera, lastDeviceAngle)
            currentRotationQuarters = rotQuarters
            Log.i(
                "PhoneCam",
                "Selected camera ${camConfig.cameraId} (front=$useFrontCamera): ${width}x${height} fps=${camConfig.fpsRange} sensorRot=${camConfig.sensorOrientation} uprightRot=${rotQuarters * 90}"
            )

            val client = TcpStreamClient(
                host = host,
                port = port,
                initialWidth = width,
                initialHeight = height,
                initialFps = targetFps,
                initialRotationQuarters = rotQuarters,
                initialBitrate = targetBitrate,
                autoReconnect = true,
                onConnecting = { attempt ->
                    val label = if (attempt <= 1) {
                        "Connecting to $host:$port..."
                    } else {
                        "Reconnecting (#$attempt) to $host:$port..."
                    }
                    updateNotification(label)
                    state.value = state.value.copy(
                        isConnected = false,
                        statusText = label,
                        mbps = 0.0
                    )
                },
                onConnected = {
                    Log.i("PhoneCam", "TCP connected to $host:$port")
                    lastRateCheckMs = SystemClock.elapsedRealtime()
                    lastBytes = 0L
                    val camLabel = if (useFrontCamera) "Front" else "Back"
                    updateNotification("Streaming $camLabel ${currentWidth}x${currentHeight} @ ${targetFps}fps")
                    state.value = state.value.copy(
                        isRunning = true,
                        isConnected = true,
                        useFrontCamera = useFrontCamera,
                        statusText = "Streaming $camLabel ($host:$port)",
                        width = currentWidth,
                        height = currentHeight,
                        fps = targetFps,
                        bitrate = targetBitrate,
                        zoom = currentZoom,
                        errorText = null
                    )
                },
                onRequestKeyframe = {
                    encoder?.requestSyncFrame()
                },
                onSetConfig = { cfg ->
                    mainHandler.post { handleRemoteConfig(cfg) }
                },
                onStatsUpdate = { pkts, bytes, dropped ->
                    val now = SystemClock.elapsedRealtime()
                    val dtMs = (now - lastRateCheckMs).coerceAtLeast(1L)
                    val deltaBytes = (bytes - lastBytes).coerceAtLeast(0L)
                    lastBytes = bytes
                    lastRateCheckMs = now
                    val mbps = (deltaBytes * 8.0) / (dtMs * 1000.0)
                    state.value = state.value.copy(
                        mbps = mbps,
                        packetsSent = pkts,
                        droppedFrames = dropped
                    )
                },
                onReconnecting = { reason, delayMs ->
                    Log.w("PhoneCam", "Link lost ($reason); retrying in ${delayMs}ms")
                    val msg = "Reconnecting in ${delayMs / 1000}s ($reason)"
                    updateNotification(msg)
                    state.value = state.value.copy(
                        isConnected = false,
                        statusText = msg,
                        mbps = 0.0
                    )
                },
                onStopped = { reason ->
                    if (reason != null) {
                        mainHandler.post {
                            stopPipeline("Disconnected: $reason")
                            stopForeground(STOP_FOREGROUND_REMOVE)
                            stopSelf()
                        }
                    }
                }
            )

            val enc = VideoEncoder(
                width = width,
                height = height,
                fps = targetFps,
                bitrate = targetBitrate,
                onEncodedPacket = { data, size, tsUs, isConfig, isKeyframe ->
                    client.enqueueEncodedFrame(data, size, tsUs, isConfig, isKeyframe)
                },
                onError = { err ->
                    Log.e("PhoneCam", "Encoder error", err)
                    stopPipeline("Encoder error: ${err.message}")
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    stopSelf()
                }
            )

            encoder = enc
            cameraController = cam
            tcpClient = client

            enc.start()
            cam.start(camConfig, enc.inputSurface, currentZoom)
            client.start()
            startOrientationListener()
        } catch (t: Throwable) {
            Log.e("PhoneCam", "Start failed", t)
            stopPipeline("Start failed: ${t.message}")
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    private fun handleRemoteConfig(cfg: Protocol.ConfigUpdate) {
        val newWidth = if (cfg.width > 0) cfg.width else targetWidth
        val newHeight = if (cfg.height > 0) cfg.height else targetHeight
        val newFps = if (cfg.fps > 0) cfg.fps.coerceIn(10, 60) else targetFps
        val newBitrate = if (cfg.bitrate > 0) cfg.bitrate.toInt().coerceIn(500_000, 25_000_000) else targetBitrate
        val newFront = when (cfg.cameraFacing) {
            Protocol.CAMERA_FACING_FRONT -> true
            Protocol.CAMERA_FACING_BACK -> false
            else -> useFrontCamera
        }
        val newZoom = if (cfg.zoomX100 > 0) (cfg.zoomX100 / 100.0f).coerceIn(0.5f, 10.0f) else currentZoom

        applyRemoteOrLocalConfig(newWidth, newHeight, newFps, newBitrate, newFront, newZoom)
    }

    private fun applyRemoteOrLocalConfig(
        newWidth: Int,
        newHeight: Int,
        newFps: Int,
        newBitrate: Int,
        newFront: Boolean,
        newZoom: Float
    ) {
        val client = tcpClient ?: return
        if (!state.value.isRunning) return

        val needReconfigure =
            newWidth != targetWidth ||
            newHeight != targetHeight ||
            newFps != targetFps ||
            newFront != useFrontCamera

        if (!needReconfigure) {
            if (newBitrate != targetBitrate) {
                targetBitrate = newBitrate
                encoder?.setBitrate(newBitrate)
                state.value = state.value.copy(bitrate = newBitrate)
                Log.i("PhoneCam", "Updated bitrate dynamically -> $newBitrate bps")
            }
            if (newZoom != currentZoom) {
                currentZoom = newZoom
                cameraController?.setZoom(newZoom)
                state.value = state.value.copy(zoom = newZoom)
            }
            return
        }

        targetWidth = newWidth
        targetHeight = newHeight
        targetFps = newFps
        targetBitrate = newBitrate
        useFrontCamera = newFront
        currentZoom = if (newFront != state.value.useFrontCamera && newZoom == currentZoom) 1.0f else newZoom

        try {
            Log.i("PhoneCam", "Reconfiguring pipeline -> ${targetWidth}x${targetHeight}@${targetFps}fps bitrate=$targetBitrate front=$useFrontCamera zoom=$currentZoom")
            cameraController?.stop()
            cameraController = null
            encoder?.stop()
            encoder = null

            val cam = Camera2Controller(this) { err ->
                Log.e("PhoneCam", "Camera error after reconfigure", err)
                stopPipeline("Camera error: ${err.message}")
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
            val camConfig = cam.selectCamera(useFrontCamera, targetWidth, targetHeight, targetFps)
            val width = camConfig.size.width
            val height = camConfig.size.height
            currentWidth = width
            currentHeight = height
            currentSensorOrientation = camConfig.sensorOrientation
            val rotQuarters = computeRotationQuarters(currentSensorOrientation, useFrontCamera, lastDeviceAngle)
            currentRotationQuarters = rotQuarters

            val enc = VideoEncoder(
                width = width,
                height = height,
                fps = targetFps,
                bitrate = targetBitrate,
                onEncodedPacket = { data, size, tsUs, isConfig, isKeyframe ->
                    client.enqueueEncodedFrame(data, size, tsUs, isConfig, isKeyframe)
                },
                onError = { err ->
                    Log.e("PhoneCam", "Encoder error after reconfigure", err)
                    stopPipeline("Encoder error: ${err.message}")
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    stopSelf()
                }
            )

            encoder = enc
            cameraController = cam

            enc.start()
            cam.start(camConfig, enc.inputSurface, currentZoom)
            client.sendStreamInfo(width, height, targetFps, rotQuarters, targetBitrate)

            val camLabel = if (useFrontCamera) "Front" else "Back"
            if (state.value.isConnected) {
                updateNotification("Streaming $camLabel ${width}x${height} @ ${targetFps}fps")
            }
            state.value = state.value.copy(
                useFrontCamera = useFrontCamera,
                statusText = if (state.value.isConnected) {
                    "Streaming $camLabel ($currentHost:$currentPort)"
                } else {
                    state.value.statusText
                },
                width = width,
                height = height,
                fps = targetFps,
                bitrate = targetBitrate,
                zoom = currentZoom
            )
        } catch (t: Throwable) {
            Log.e("PhoneCam", "Reconfigure failed", t)
            stopPipeline("Reconfigure failed: ${t.message}")
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    private fun startOrientationListener() {
        orientationListener?.disable()
        orientationListener = object : OrientationEventListener(this) {
            override fun onOrientationChanged(orientation: Int) {
                if (orientation == ORIENTATION_UNKNOWN) return
                val snapped = when {
                    orientation >= 330 || orientation <= 30 -> 0
                    orientation in 60..120 -> 90
                    orientation in 150..210 -> 180
                    orientation in 240..300 -> 270
                    else -> return
                }
                if (snapped == lastDeviceAngle) return
                lastDeviceAngle = snapped
                val newQuarters = computeRotationQuarters(currentSensorOrientation, useFrontCamera, snapped)
                if (newQuarters != currentRotationQuarters) {
                    currentRotationQuarters = newQuarters
                    Log.i("PhoneCam", "Orientation changed: device=$snapped uprightRot=${newQuarters * 90}")
                    tcpClient?.sendStreamInfo(currentWidth, currentHeight, targetFps, newQuarters, targetBitrate)
                }
            }
        }.apply {
            if (canDetectOrientation()) enable()
        }
    }

    private fun computeRotationQuarters(sensorOrientation: Int, isFront: Boolean, deviceAngle: Int): Int {
        val cwDegrees = if (isFront) {
            (sensorOrientation - deviceAngle + 360) % 360
        } else {
            (sensorOrientation + deviceAngle) % 360
        }
        return (cwDegrees / 90) % 4
    }

    private fun stopPipeline(error: String?) {
        Log.i("PhoneCam", "stopPipeline error=$error")
        orientationListener?.disable()
        orientationListener = null
        cameraController?.stop()
        cameraController = null
        encoder?.stop()
        encoder = null
        tcpClient?.stop()
        tcpClient = null
        try {
            if (wakeLock?.isHeld == true) wakeLock?.release()
        } catch (_: Throwable) {
        }
        wakeLock = null

        val preservedError = error ?: state.value.errorText
        state.value = state.value.copy(
            isRunning = false,
            isConnected = false,
            statusText = if (preservedError == null) "Idle" else "Stopped",
            mbps = 0.0,
            errorText = preservedError
        )
    }

    override fun onDestroy() {
        stopPipeline(error = null)
        super.onDestroy()
    }
}
