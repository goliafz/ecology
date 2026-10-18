package ru.khodakovsky.wltester.core

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.addJsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import kotlinx.serialization.json.putJsonObject

/**
 * Конфиг для локального xray: на каждый профиль — свой SOCKS5-вход на 127.0.0.1 и свой VLESS-выход.
 * Так все профили проверяются одним процессом и независимо друг от друга.
 */
object XrayConfig {

    /**
     * @param addresses адрес для подключения (обычно заранее разрешённый IP) по индексу профиля;
     *                  если нет — используется домен из ссылки.
     */
    fun build(profiles: List<VlessProfile>, socksPorts: List<Int>, addresses: Map<Int, String> = emptyMap()): JsonObject {
        require(profiles.size == socksPorts.size) { "profiles/ports mismatch" }
        return buildJsonObject {
            putJsonObject("log") { put("loglevel", "warning") }
            putJsonArray("inbounds") {
                profiles.indices.forEach { i ->
                    addJsonObject {
                        put("tag", "in-$i")
                        put("listen", "127.0.0.1")
                        put("port", socksPorts[i])
                        put("protocol", "socks")
                        putJsonObject("settings") {
                            put("auth", "noauth")
                            put("udp", false)
                        }
                    }
                }
            }
            putJsonArray("outbounds") {
                profiles.forEachIndexed { i, p ->
                    addJsonObject {
                        put("tag", "out-$i")
                        put("protocol", "vless")
                        putJsonObject("settings") {
                            putJsonArray("vnext") {
                                addJsonObject {
                                    put("address", addresses[i] ?: p.host)
                                    put("port", p.port)
                                    putJsonArray("users") {
                                        addJsonObject {
                                            put("id", p.uuid)
                                            put("encryption", "none")
                                            if (p.flow.isNotBlank()) put("flow", p.flow)
                                        }
                                    }
                                }
                            }
                        }
                        putJsonObject("streamSettings") {
                            put("network", "tcp")
                            put("security", p.security)
                            when (p.security) {
                                "reality" -> putJsonObject("realitySettings") {
                                    put("serverName", p.effectiveSni)
                                    put("fingerprint", p.fingerprint)
                                    put("publicKey", p.publicKey)
                                    put("shortId", p.shortId)
                                    put("spiderX", p.spiderX)
                                }
                                "tls" -> putJsonObject("tlsSettings") {
                                    put("serverName", p.effectiveSni)
                                    put("fingerprint", p.fingerprint)
                                }
                            }
                        }
                    }
                }
                addJsonObject {
                    put("tag", "block")
                    put("protocol", "blackhole")
                }
            }
            putJsonObject("routing") {
                put("domainStrategy", "AsIs")
                putJsonArray("rules") {
                    profiles.indices.forEach { i ->
                        addJsonObject {
                            put("type", "field")
                            putJsonArray("inboundTag") { add(kotlinx.serialization.json.JsonPrimitive("in-$i")) }
                            put("outboundTag", "out-$i")
                        }
                    }
                }
            }
        }
    }
}
