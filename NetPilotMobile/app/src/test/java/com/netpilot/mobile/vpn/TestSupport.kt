package com.netpilot.mobile.vpn

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket

/**
 * Helpers shared by the host tests.
 *
 * Nothing in here may call into `android.*`: the JVM unit test runs against stubbed
 * framework classes, and the point of these tests is to prove the logic that does *not*
 * need a device.
 */

/** `"10.111.222.1"` -> the integer form [Packet] writes with `writeInt`. */
internal fun ip(s: String): Int {
    val parts = s.split(".")
    return ((parts[0].toInt() and 0xFF) shl 24) or
            ((parts[1].toInt() and 0xFF) shl 16) or
            ((parts[2].toInt() and 0xFF) shl 8) or
            (parts[3].toInt() and 0xFF)
}

internal fun u16(p: ByteArray, off: Int): Int =
    ((p[off].toInt() and 0xFF) shl 8) or (p[off + 1].toInt() and 0xFF)

/**
 * Verifies a packet without reusing the code that produced it: naive int accumulation,
 * folded once at the end, plus the RFC 1071 property that a packet carrying a correct
 * checksum folds to zero.
 */
internal fun checksumsValid(p: ByteArray): Boolean {
    if (p.size < 20) return false
    val ihl = (p[0].toInt() and 0x0F) * 4
    if (ihl < 20 || p.size < ihl) return false
    if (u16(p, 0) shr 8 != 0x45) return false              // IPv4, 20-byte header
    val total = u16(p, 2)
    if (total != p.size) return false
    // A correct ones-complement checksum makes the covered bytes fold to 0xFFFF.
    if (fold(sum(p, 0, ihl)) != 0xFFFF) return false

    val proto = p[9].toInt() and 0xFF
    if (proto != Packet.TCP) return true

    val tcpLen = total - ihl
    val pseudo = ByteArray(12)
    System.arraycopy(p, 12, pseudo, 0, 4)
    System.arraycopy(p, 16, pseudo, 4, 4)
    pseudo[9] = proto.toByte()
    pseudo[10] = ((tcpLen ushr 8) and 0xFF).toByte()
    pseudo[11] = (tcpLen and 0xFF).toByte()

    val all = ByteArray(12 + tcpLen)
    System.arraycopy(pseudo, 0, all, 0, 12)
    System.arraycopy(p, ihl, all, 12, tcpLen)
    return fold(sum(all, 0, all.size)) == 0xFFFF
}

private fun sum(data: ByteArray, off: Int, len: Int): Int {
    var s = 0
    var i = off
    val end = off + len
    while (i + 1 < end) {
        s += ((data[i].toInt() and 0xFF) shl 8) or (data[i + 1].toInt() and 0xFF)
        if (s > 0xFFFF) s = (s and 0xFFFF) + (s ushr 16)
        i += 2
    }
    if (i < end) s += (data[i].toInt() and 0xFF) shl 8
    return s
}

private fun fold(v: Int): Int {
    var x = v
    while (x ushr 16 != 0) x = (x and 0xFFFF) + (x ushr 16)
    return x
}

/** A loopback TCP server that writes back whatever it is sent. */
internal class TcpEcho {
    private val server = ServerSocket()

    init {
        server.bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), 0))
        Thread({
            while (!server.isClosed) {
                val sock = try {
                    server.accept()
                } catch (t: Throwable) {
                    break
                }
                Thread({
                    try {
                        val input = sock.getInputStream()
                        val output = sock.getOutputStream()
                        val buf = ByteArray(8192)
                        while (true) {
                            val n = input.read(buf)
                            if (n <= 0) break
                            output.write(buf, 0, n)
                            output.flush()
                        }
                    } catch (t: Throwable) {
                        // peer hung up — normal end of the echo
                    }
                    runCatching { sock.close() }
                }, "np-echo").apply { isDaemon = true; start() }
            }
        }, "np-echo-accept").apply { isDaemon = true; start() }
    }

    val port: Int get() = server.localPort

    fun close() = runCatching { server.close() }
}

/** A loopback UDP server that echoes datagrams back to their sender. */
internal class UdpEcho {
    private val socket = DatagramSocket(
        InetSocketAddress(InetAddress.getByName("127.0.0.1"), 0)
    )

    init {
        Thread({
            val buf = ByteArray(2048)
            while (!socket.isClosed) {
                val packet = DatagramPacket(buf, buf.size)
                try {
                    socket.receive(packet)
                } catch (t: Throwable) {
                    break
                }
                try {
                    socket.send(
                        DatagramPacket(packet.data, packet.length, packet.address, packet.port)
                    )
                } catch (t: Throwable) {
                    // shutting down
                }
            }
        }, "np-udp-echo").apply { isDaemon = true; start() }
    }

    val port: Int get() = socket.localPort

    fun close() = runCatching { socket.close() }
}
