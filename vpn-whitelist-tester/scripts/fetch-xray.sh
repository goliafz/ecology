#!/usr/bin/env bash
# Скачивает ядро Xray-core для Android и кладёт его как libxray.so в jniLibs.
# Используется в GitHub Actions; локально — для сборки APK с работающим тестом VPN.
set -euo pipefail
cd "$(dirname "$0")/.."

VER="${XRAY_VERSION:-v25.8.3}"
OUT=app/src/main/jniLibs
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

fetch() {
  local asset="$1" abi="$2" required="$3"
  local url="https://github.com/XTLS/Xray-core/releases/download/$VER/$asset"
  if ! curl -fsSL --retry 3 -o "$TMP/$asset" "$url"; then
    echo "Xray $VER: нет $asset, беру последний релиз"
    if ! curl -fsSL --retry 3 -o "$TMP/$asset" "https://github.com/XTLS/Xray-core/releases/latest/download/$asset"; then
      if [ "$required" = 1 ]; then echo "Не удалось скачать $asset" >&2; exit 1; fi
      echo "Пропускаю $abi"; return 0
    fi
  fi
  mkdir -p "$OUT/$abi" "$TMP/$abi"
  unzip -o -q "$TMP/$asset" xray -d "$TMP/$abi"
  mv "$TMP/$abi/xray" "$OUT/$abi/libxray.so"
  chmod 755 "$OUT/$abi/libxray.so"
  echo "$abi: $(du -h "$OUT/$abi/libxray.so" | cut -f1)"
}

fetch Xray-android-arm64-v8a.zip arm64-v8a 1
fetch Xray-linux-arm32-v7a.zip armeabi-v7a 0
fetch Xray-android-amd64.zip x86_64 0
