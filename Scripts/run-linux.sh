#!/usr/bin/env bash
#
# 啟動 BYOD 防弊瀏覽器（Linux）。
# 放在發佈輸出資料夾（含 ByodKioskBrowser 執行檔）內，或從該資料夾呼叫。
#
# 用法：
#   ./run-linux.sh                 # 一般啟動
#   BYOD_DEBUG=1 ./run-linux.sh     # 顯示 CEF/Avalonia 除錯輸出
#
set -euo pipefail

# 切到執行檔所在目錄（支援從任意路徑呼叫）
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

BIN="./ByodKioskBrowser"
if [[ ! -f "$BIN" ]]; then
    echo "找不到 $BIN。請將本腳本放在發佈輸出資料夾內。" >&2
    exit 1
fi

# 確保執行權限（CI 打包/下載後常遺失可執行位元）
chmod +x "$BIN" 2>/dev/null || true
# CEF 的 browser 子行程執行檔（名稱可能因版本而異）
find . -maxdepth 2 -type f -iname '*browserprocess*' -exec chmod +x {} \; 2>/dev/null || true

# Wayland 工作階段：透過 XWayland 走 X11，CEF 相容性較佳
if [[ "${XDG_SESSION_TYPE:-}" == "wayland" ]]; then
    echo "偵測到 Wayland — 將透過 XWayland (X11) 執行以取得較佳相容性。"
    export GDK_BACKEND=x11
fi

if [[ "${BYOD_DEBUG:-}" == "1" ]]; then
    exec "$BIN"
else
    exec "$BIN" 2>/dev/null
fi
