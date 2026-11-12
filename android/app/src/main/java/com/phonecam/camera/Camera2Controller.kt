package com.phonecam.camera

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Rect
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.MediaCodec
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.util.Range
import android.util.Size
import android.view.Surface
import java.util.concurrent.Executor
import kotlin.math.abs

data class CameraConfig(
    val cameraId: String,
    val size: Size,
    val fpsRange: Range<Int>,
    val sensorOrientation: Int,
    val zoomRange: Range<Float>,
    val activeArray: Rect?
)

class Camera2Controller(
    context: Context,
    private val onError: (Throwable) -> Unit
) {
    private val cameraManager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
    private val thread = HandlerThread("Camera2Thread").apply { start() }
    private val handler = Handler(thread.looper)
    private val executor = Executor { command ->
        if (thread.isAlive) handler.post(command)
    }

    @Volatile
    private var cameraDevice: CameraDevice? = null

    @Volatile
    private var captureSession: CameraCaptureSession? = null

    @Volatile
    private var activeConfig: CameraConfig? = null

    @Volatile
    private var activeSurface: Surface? = null

    @Volatile
    private var currentZoomRatio: Float = 1.0f

    @Volatile
    private var closed = false

    fun selectCamera(
        useFront: Boolean,
        targetWidth: Int,
        targetHeight: Int,
        targetFps: Int
    ): CameraConfig {
        val desiredFacing = if (useFront) {
            CameraCharacteristics.LENS_FACING_FRONT
        } else {
            CameraCharacteristics.LENS_FACING_BACK
        }
        val cameraId = cameraManager.cameraIdList.firstOrNull { id ->
            val chars = cameraManager.getCameraCharacteristics(id)
            chars.get(CameraCharacteristics.LENS_FACING) == desiredFacing
        } ?: cameraManager.cameraIdList.firstOrNull()
            ?: error("No camera found on device")

        val chars = cameraManager.getCameraCharacteristics(cameraId)
        val map = chars.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            ?: error("Camera stream configuration map unavailable")

        val supportedSizes = map.getOutputSizes(MediaCodec::class.java)
            ?: error("No MediaCodec output sizes supported")

        val chosenSize = supportedSizes.firstOrNull {
            it.width == targetWidth && it.height == targetHeight
        } ?: supportedSizes.minByOrNull {
            val targetAspect = targetWidth.toDouble() / targetHeight
            val aspectDiff = abs((it.width.toDouble() / it.height) - targetAspect)
            val areaDiff = abs((it.width * it.height) - (targetWidth * targetHeight))
            aspectDiff * 1_000_000.0 + areaDiff
        } ?: Size(targetWidth, targetHeight)

        val fpsRanges = chars.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)
            ?: arrayOf(Range(targetFps, targetFps))

        val chosenFps = fpsRanges.firstOrNull {
            it.lower == targetFps && it.upper == targetFps
        } ?: fpsRanges.filter {
            it.upper >= targetFps
        }.minByOrNull {
            (it.upper - it.lower) * 100 + abs(it.upper - targetFps)
        } ?: fpsRanges.maxByOrNull { it.upper } ?: Range(targetFps, targetFps)

        val orientation = chars.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90
        val zoomRange = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            chars.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE) ?: Range(1.0f, 1.0f)
        } else {
            val maxDigital = chars.get(CameraCharacteristics.SCALER_AVAILABLE_MAX_DIGITAL_ZOOM) ?: 1.0f
            Range(1.0f, maxDigital.coerceAtLeast(1.0f))
        }
        val activeArray = chars.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE)
        return CameraConfig(cameraId, chosenSize, chosenFps, orientation, zoomRange, activeArray)
    }

    @SuppressLint("MissingPermission")
    fun start(config: CameraConfig, encoderSurface: Surface, initialZoom: Float = 1.0f) {
        activeConfig = config
        activeSurface = encoderSurface
        currentZoomRatio = initialZoom.coerceIn(config.zoomRange.lower, config.zoomRange.upper)

        cameraManager.openCamera(
            config.cameraId,
            executor,
            object : CameraDevice.StateCallback() {
                override fun onOpened(camera: CameraDevice) {
                    if (closed) {
                        camera.close()
                        return
                    }
                    cameraDevice = camera
                    createSession(camera, config, encoderSurface)
                }

                override fun onDisconnected(camera: CameraDevice) {
                    camera.close()
                    if (!closed) onError(IllegalStateException("Camera disconnected"))
                }

                override fun onError(camera: CameraDevice, error: Int) {
                    camera.close()
                    if (!closed) onError(IllegalStateException("Camera open failed: error $error"))
                }
            }
        )
    }

    fun setZoom(zoomRatio: Float) {
        val cfg = activeConfig ?: return
        val clamped = zoomRatio.coerceIn(cfg.zoomRange.lower, cfg.zoomRange.upper)
        if (abs(clamped - currentZoomRatio) < 0.01f) return
        currentZoomRatio = clamped

        if (closed) return
        handler.post {
            val camera = cameraDevice ?: return@post
            val session = captureSession ?: return@post
            val surface = activeSurface ?: return@post
            if (!closed) {
                applyRepeatingRequest(camera, session, cfg, surface)
            }
        }
    }

    private fun createSession(
        camera: CameraDevice,
        config: CameraConfig,
        encoderSurface: Surface
    ) {
        try {
            val sessionConfig = SessionConfiguration(
                SessionConfiguration.SESSION_REGULAR,
                listOf(OutputConfiguration(encoderSurface)),
                executor,
                object : CameraCaptureSession.StateCallback() {
                    override fun onConfigured(session: CameraCaptureSession) {
                        if (closed) {
                            session.close()
                            return
                        }
                        captureSession = session
                        applyRepeatingRequest(camera, session, config, encoderSurface)
                    }

                    override fun onConfigureFailed(session: CameraCaptureSession) {
                        if (!closed) onError(IllegalStateException("Camera capture session configure failed"))
                    }
                }
            )
            camera.createCaptureSession(sessionConfig)
        } catch (t: Throwable) {
            if (!closed) onError(t)
        }
    }

    private fun applyRepeatingRequest(
        camera: CameraDevice,
        session: CameraCaptureSession,
        config: CameraConfig,
        encoderSurface: Surface
    ) {
        try {
            val builder = camera.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
                addTarget(encoderSurface)
                set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO)
                set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, config.fpsRange)
                set(
                    CaptureRequest.CONTROL_AF_MODE,
                    CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO
                )
            }

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                builder.set(CaptureRequest.CONTROL_ZOOM_RATIO, currentZoomRatio)
            } else if (config.activeArray != null && currentZoomRatio > 1.0f) {
                val r = config.activeArray
                val cropW = (r.width() / currentZoomRatio).toInt()
                val cropH = (r.height() / currentZoomRatio).toInt()
                val dx = (r.width() - cropW) / 2
                val dy = (r.height() - cropH) / 2
                builder.set(
                    CaptureRequest.SCALER_CROP_REGION,
                    Rect(r.left + dx, r.top + dy, r.left + dx + cropW, r.top + dy + cropH)
                )
            }

            session.setRepeatingRequest(builder.build(), null, handler)
        } catch (t: Throwable) {
            if (!closed) onError(t)
        }
    }

    fun stop() {
        closed = true
        try {
            captureSession?.stopRepeating()
        } catch (_: Throwable) {
        }
        try {
            captureSession?.close()
        } catch (_: Throwable) {
        }
        captureSession = null
        try {
            cameraDevice?.close()
        } catch (_: Throwable) {
        }
        cameraDevice = null
        activeConfig = null
        activeSurface = null
        thread.quitSafely()
    }
}
