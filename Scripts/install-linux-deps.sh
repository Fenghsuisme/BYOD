#!/usr/bin/env bash
#
# 安裝 CEF / Chromium 在 Linux 上的執行期系統函式庫。
# 支援 Debian/Ubuntu (apt)、Fedora/RHEL (dnf)、Arch (pacman)。
# 用法： sudo ./Scripts/install-linux-deps.sh
#
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "請以 root 執行： sudo $0" >&2
    exit 1
fi

if command -v apt-get >/dev/null 2>&1; then
    echo "偵測到 apt（Debian/Ubuntu）"
    apt-get update

    # 每項可用「新名|舊名」列出（因應 Ubuntu 24.04+ 的 t64 轉換）；
    # 逐一挑選實際可安裝者，避免虛擬套件或單一缺項導致整批中止。
    CANDIDATES=(
        "libnss3" "libnspr4"
        "libatk1.0-0t64|libatk1.0-0" "libatk-bridge2.0-0t64|libatk-bridge2.0-0" "libatspi2.0-0t64|libatspi2.0-0"
        "libcups2t64|libcups2"
        "libdrm2" "libgbm1"
        "libxkbcommon0"
        "libxcomposite1" "libxdamage1" "libxrandr2" "libxfixes3" "libxext6" "libx11-6" "libxcb1" "libxrender1"
        "libpango-1.0-0" "libcairo2"
        "libasound2t64|libasound2"
        "libglib2.0-0t64|libglib2.0-0" "libgtk-3-0t64|libgtk-3-0"
        "fonts-liberation"
    )

    TO_INSTALL=()
    for entry in "${CANDIDATES[@]}"; do
        IFS='|' read -ra alts <<< "$entry"
        picked=""
        for a in "${alts[@]}"; do
            if apt-get install -y --dry-run "$a" >/dev/null 2>&1; then
                picked="$a"
                break
            fi
        done
        if [[ -n "$picked" ]]; then
            TO_INSTALL+=("$picked")
        else
            echo "  略過（找不到可安裝的候選）：$entry" >&2
        fi
    done

    echo "將安裝：${TO_INSTALL[*]}"
    apt-get install -y "${TO_INSTALL[@]}"
elif command -v dnf >/dev/null 2>&1; then
    echo "偵測到 dnf（Fedora/RHEL）"
    dnf install -y \
        nss nspr \
        at-spi2-atk at-spi2-core atk \
        cups-libs \
        libdrm mesa-libgbm \
        libxkbcommon \
        libXcomposite libXdamage libXrandr libXfixes libXext libX11 libxcb libXrender \
        pango cairo \
        alsa-lib \
        glib2 gtk3 \
        liberation-fonts
elif command -v pacman >/dev/null 2>&1; then
    echo "偵測到 pacman（Arch）"
    pacman -Sy --noconfirm \
        nss nspr \
        at-spi2-core atk \
        cups \
        libdrm mesa \
        libxkbcommon \
        libxcomposite libxdamage libxrandr libxfixes libxext libx11 libxcb libxrender \
        pango cairo \
        alsa-lib \
        glib2 gtk3 \
        ttf-liberation
else
    echo "找不到支援的套件管理員（apt/dnf/pacman）。請手動安裝 Chromium 執行期依賴。" >&2
    exit 1
fi

echo "完成。CEF 執行期依賴已安裝。"
