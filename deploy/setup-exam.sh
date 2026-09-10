#!/usr/bin/env bash
#
# 發佈包版：考試機一鍵設定（免 .NET SDK）。
#   1) 安裝系統依賴（CEF 函式庫 + g++）
#   2) 建立 EXAM 桌面圖示
# Monaco 與 .NET runtime 已隨發佈包內含，無需佈署或安裝 SDK。
#
# 用法： ./setup-exam.sh
#
set -euo pipefail

ROOT="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
cd "$ROOT"

echo "==> (1/2) 安裝系統依賴（CEF 函式庫 + g++）…"
if command -v sudo >/dev/null 2>&1; then
    sudo ./install-linux-deps.sh
else
    ./install-linux-deps.sh
fi

echo ""
echo "==> (2/2) 建立 EXAM 桌面圖示…"
./install-shortcut.sh

echo ""
echo "======================================"
echo " 完成！桌面應出現「EXAM」圖示，點兩下即可開始考試。"
echo "======================================"
