package com.phonecam

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.view.View
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.phonecam.net.PcDiscovery
import com.phonecam.protocol.Protocol
import com.phonecam.stream.CameraStreamService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import java.util.Locale

class MainActivity : AppCompatActivity() {

    private lateinit var tvStatus: TextView
    private lateinit var tvStats: TextView
    private lateinit var tvError: TextView
    private lateinit var tvHostLabel: TextView
    private lateinit var tvDiscoveryHint: TextView
    private lateinit var etHost: EditText
    private lateinit var etPort: EditText
    private lateinit var btnModeUsb: Button
    private lateinit var btnModeWifi: Button
    private lateinit var btnDiscover: Button
    private lateinit var chkAutoStart: CheckBox
    private lateinit var btnSwitchCam: Button
    private lateinit var btnToggle: Button

    private val uiScope = CoroutineScope(Dispatchers.Main + SupervisorJob())
    private var stateJob: Job? = null
    private var scanJob: Job? = null

    private var selectedFrontCamera = false
    private var wifiMode = false

    private val prefs by lazy { getSharedPreferences("phonecam_prefs", Context.MODE_PRIVATE) }

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { results ->
        val cameraGranted = results[Manifest.permission.CAMERA] == true ||
            hasPermission(Manifest.permission.CAMERA)
        if (cameraGranted) {
            startStreamingService()
        } else {
            tvError.visibility = View.VISIBLE
            tvError.text = "Camera permission is required to stream."
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        tvStatus = findViewById(R.id.tvStatus)
        tvStats = findViewById(R.id.tvStats)
        tvError = findViewById(R.id.tvError)
        tvHostLabel = findViewById(R.id.tvHostLabel)
        tvDiscoveryHint = findViewById(R.id.tvDiscoveryHint)
        etHost = findViewById(R.id.etHost)
        etPort = findViewById(R.id.etPort)
        btnModeUsb = findViewById(R.id.btnModeUsb)
        btnModeWifi = findViewById(R.id.btnModeWifi)
        btnDiscover = findViewById(R.id.btnDiscover)
        chkAutoStart = findViewById(R.id.chkAutoStart)
        btnSwitchCam = findViewById(R.id.btnSwitchCam)
        btnToggle = findViewById(R.id.btnToggle)

        wifiMode = prefs.getBoolean("wifi_mode", false)
        applyTransportMode(wifiMode, triggerAutoScan = false)

        chkAutoStart.isChecked = prefs.getBoolean("auto_start", false)
        chkAutoStart.setOnCheckedChangeListener { _, isChecked ->
            prefs.edit().putBoolean("auto_start", isChecked).apply()
        }

        if (chkAutoStart.isChecked &&
            !CameraStreamService.state.value.isRunning &&
            hasPermission(Manifest.permission.CAMERA) &&
            etHost.text.toString().trim().isNotEmpty()
        ) {
            startStreamingService()
        }

        btnModeUsb.setOnClickListener {
            if (!CameraStreamService.state.value.isRunning) {
                applyTransportMode(useWifi = false, triggerAutoScan = false)
            }
        }

        btnModeWifi.setOnClickListener {
            if (!CameraStreamService.state.value.isRunning) {
                applyTransportMode(useWifi = true, triggerAutoScan = true)
            }
        }

        btnDiscover.setOnClickListener {
            runDiscoveryScan()
        }

        btnSwitchCam.setOnClickListener {
            val nextFront = !selectedFrontCamera
            selectedFrontCamera = nextFront
            btnSwitchCam.text = if (nextFront) "Camera: Front" else "Camera: Back"
            if (CameraStreamService.state.value.isRunning) {
                val intent = Intent(this, CameraStreamService::class.java).apply {
                    action = CameraStreamService.ACTION_SWITCH_CAMERA
                    putExtra(CameraStreamService.EXTRA_USE_FRONT, nextFront)
                }
                startService(intent)
            }
        }

        btnToggle.setOnClickListener {
            if (CameraStreamService.state.value.isRunning) {
                stopStreamingService()
            } else {
                ensurePermissionsAndStart()
            }
        }
    }

    private fun applyTransportMode(useWifi: Boolean, triggerAutoScan: Boolean) {
        wifiMode = useWifi
        prefs.edit().putBoolean("wifi_mode", useWifi).apply()

        val activeTint = ColorStateList.valueOf(Color.parseColor("#2563EB"))
        val inactiveTint = ColorStateList.valueOf(Color.parseColor("#374151"))

        if (useWifi) {
            btnModeUsb.backgroundTintList = inactiveTint
            btnModeWifi.backgroundTintList = activeTint
            tvHostLabel.text = "PC WiFi / Hotspot IP"
            btnDiscover.visibility = View.VISIBLE
            tvDiscoveryHint.visibility = View.VISIBLE
            val savedIp = prefs.getString("wifi_host", "") ?: ""
            etHost.setText(savedIp)
            etHost.hint = "e.g. 192.168.1.42"
            if (triggerAutoScan) {
                runDiscoveryScan()
            } else if (savedIp.isBlank()) {
                tvDiscoveryHint.text = "Tap 'Scan PC' or enter the IP shown on the Windows PhoneCam bar."
            } else {
                tvDiscoveryHint.text = "Using saved WiFi IP (tap 'Scan PC' to refresh)."
            }
        } else {
            btnModeUsb.backgroundTintList = activeTint
            btnModeWifi.backgroundTintList = inactiveTint
            tvHostLabel.text = "Target Host (127.0.0.1 for USB adb reverse)"
            btnDiscover.visibility = View.GONE
            tvDiscoveryHint.visibility = View.GONE
            etHost.setText("127.0.0.1")
        }
    }

    private fun runDiscoveryScan() {
        scanJob?.cancel()
        btnDiscover.isEnabled = false
        btnDiscover.text = "Scanning..."
        tvDiscoveryHint.visibility = View.VISIBLE
        tvDiscoveryHint.text = "Broadcasting UDP discovery on port ${Protocol.DISCOVERY_PORT}..."

        scanJob = uiScope.launch {
            try {
                val results = PcDiscovery.discover()
                when {
                    results.isEmpty() -> {
                        tvDiscoveryHint.text =
                            "No PC replied. Ensure Windows PhoneCam is open & allowed in Windows Firewall, or type the IP manually."
                    }
                    results.size == 1 -> {
                        val pc = results[0]
                        etHost.setText(pc.ip)
                        etPort.setText(pc.port.toString())
                        prefs.edit().putString("wifi_host", pc.ip).apply()
                        tvDiscoveryHint.text = "Found ${pc.name} at ${pc.ip}:${pc.port}"
                    }
                    else -> {
                        val labels = results.map { "${it.name} (${it.ip}:${it.port})" }.toTypedArray()
                        AlertDialog.Builder(this@MainActivity)
                            .setTitle("Select PC")
                            .setItems(labels) { _, which ->
                                val pc = results[which]
                                etHost.setText(pc.ip)
                                etPort.setText(pc.port.toString())
                                prefs.edit().putString("wifi_host", pc.ip).apply()
                                tvDiscoveryHint.text = "Selected ${pc.name} at ${pc.ip}:${pc.port}"
                            }
                            .show()
                    }
                }
            } finally {
                btnDiscover.isEnabled = true
                btnDiscover.text = "Scan PC"
            }
        }
    }

    override fun onStart() {
        super.onStart()
        stateJob = uiScope.launch {
            CameraStreamService.state.collectLatest { st ->
                tvStatus.text = st.statusText
                tvStats.text = String.format(
                    Locale.US,
                    "%dx%d @ %d fps | %.2f Mbps\nSent: %d | Dropped: %d",
                    st.width, st.height, st.fps, st.mbps, st.packetsSent, st.droppedFrames
                )
                if (st.errorText != null) {
                    tvError.visibility = View.VISIBLE
                    tvError.text = st.errorText
                } else {
                    tvError.visibility = View.GONE
                }

                if (st.isRunning) {
                    selectedFrontCamera = st.useFrontCamera
                    btnSwitchCam.text = if (st.useFrontCamera) "Camera: Front" else "Camera: Back"
                }
                btnToggle.text = if (st.isRunning) "Stop Streaming" else "Start Streaming"
                btnModeUsb.isEnabled = !st.isRunning
                btnModeWifi.isEnabled = !st.isRunning
                btnDiscover.isEnabled = !st.isRunning
                etHost.isEnabled = !st.isRunning
                etPort.isEnabled = !st.isRunning
            }
        }
    }

    override fun onStop() {
        stateJob?.cancel()
        stateJob = null
        super.onStop()
    }

    private fun ensurePermissionsAndStart() {
        val needed = mutableListOf<String>()
        if (!hasPermission(Manifest.permission.CAMERA)) {
            needed.add(Manifest.permission.CAMERA)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            !hasPermission(Manifest.permission.POST_NOTIFICATIONS)
        ) {
            needed.add(Manifest.permission.POST_NOTIFICATIONS)
        }

        if (needed.isEmpty()) {
            startStreamingService()
        } else {
            permissionLauncher.launch(needed.toTypedArray())
        }
    }

    private fun hasPermission(perm: String): Boolean =
        ContextCompat.checkSelfPermission(this, perm) == PackageManager.PERMISSION_GRANTED

    private fun startStreamingService() {
        val host = etHost.text.toString().trim().ifEmpty {
            if (wifiMode) "" else "127.0.0.1"
        }
        if (host.isEmpty()) {
            tvError.visibility = View.VISIBLE
            tvError.text = "Enter your PC's WiFi/Hotspot IP or tap 'Scan PC'."
            return
        }
        if (wifiMode) {
            prefs.edit().putString("wifi_host", host).apply()
        }
        val port = etPort.text.toString().trim().toIntOrNull() ?: Protocol.DEFAULT_PORT

        val intent = Intent(this, CameraStreamService::class.java).apply {
            action = CameraStreamService.ACTION_START
            putExtra(CameraStreamService.EXTRA_HOST, host)
            putExtra(CameraStreamService.EXTRA_PORT, port)
            putExtra(CameraStreamService.EXTRA_USE_FRONT, selectedFrontCamera)
        }
        ContextCompat.startForegroundService(this, intent)
    }

    private fun stopStreamingService() {
        val intent = Intent(this, CameraStreamService::class.java).apply {
            action = CameraStreamService.ACTION_STOP
        }
        startService(intent)
    }
}
