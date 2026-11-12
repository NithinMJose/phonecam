package com.phonecam.net

import com.phonecam.protocol.Protocol
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException

data class DiscoveredPc(
    val ip: String,
    val name: String,
    val port: Int
)

object PcDiscovery {
    suspend fun discover(timeoutMs: Long = 1300L): List<DiscoveredPc> = withContext(Dispatchers.IO) {
        val found = LinkedHashMap<String, DiscoveredPc>()
        val probeBytes = Protocol.DISCOVERY_PROBE.toByteArray(Charsets.UTF_8)

        val socket = DatagramSocket().apply {
            broadcast = true
            soTimeout = 300
        }

        try {
            val targets = LinkedHashSet<InetAddress>()
            targets.add(InetAddress.getByName("255.255.255.255"))

            val ifaces = NetworkInterface.getNetworkInterfaces()
            while (ifaces.hasMoreElements()) {
                val iface = ifaces.nextElement()
                if (!iface.isUp || iface.isLoopback) continue
                for (addr in iface.interfaceAddresses) {
                    addr.broadcast?.let { targets.add(it) }
                }
            }

            // Send two rounds of probes 200ms apart in case the first UDP packet drops.
            repeat(2) { round ->
                for (target in targets) {
                    try {
                        val pkt = DatagramPacket(probeBytes, probeBytes.size, target, Protocol.DISCOVERY_PORT)
                        socket.send(pkt)
                    } catch (_: Throwable) {
                    }
                }

                val listenUntil = System.currentTimeMillis() + (if (round == 0) 350L else timeoutMs - 350L)
                val rxBuf = ByteArray(512)
                while (System.currentTimeMillis() < listenUntil) {
                    try {
                        val rxPkt = DatagramPacket(rxBuf, rxBuf.size)
                        socket.receive(rxPkt)
                        val msg = String(rxPkt.data, rxPkt.offset, rxPkt.length, Charsets.UTF_8)
                        if (msg.startsWith(Protocol.DISCOVERY_REPLY_PREFIX)) {
                            val parts = msg.removePrefix(Protocol.DISCOVERY_REPLY_PREFIX).split('|')
                            val name = parts.getOrNull(0)?.ifBlank { "Windows PC" } ?: "Windows PC"
                            val port = parts.getOrNull(1)?.toIntOrNull() ?: Protocol.DEFAULT_PORT
                            val ip = rxPkt.address.hostAddress ?: continue
                            found[ip] = DiscoveredPc(ip = ip, name = name, port = port)
                        }
                    } catch (_: SocketTimeoutException) {
                    } catch (_: Throwable) {
                        break
                    }
                }
            }
        } finally {
            try {
                socket.close()
            } catch (_: Throwable) {
            }
        }

        found.values.toList()
    }
}

