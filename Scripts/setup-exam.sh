#!/usr/bin/env bash
#
# 一鍵設定考試機（複製專案到新機器後，跑這一支就好）：
#   1) 安裝系統依賴（CEF 函式庫 + g++）
#   2) 佈署 Monaco 編輯器本地資源
#   3) 在桌面建立 EXAM 啟動圖示
#
# 用法： ./Scripts/setup-exam.sh
#
set -euo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
ROOT="$(dirname "$(dirname "$SELF")")"
cd "$ROOT"

echo "==> (1/3) 安裝系統依賴（CEF 函式庫 + g++）…"
if command -v sudo >/dev/null 2>&1; then
    sudo ./Scripts/install-linux-deps.sh
else
    ./Scripts/install-linux-deps.sh
fi

echo ""
echo "==> (2/3) 佈署 Monaco 編輯器…"
if [[ -f App/Assets/webapp/vs/loader.js ]]; then
    echo "（已存在，略過）"
else
    ./Scripts/fetch-monaco.sh
fi

echo ""
echo "==> (3/3) 建立 EXAM 桌面圖示…"
./Scripts/install-shortcut.sh

echo ""
echo "======================================"
echo " 完成！桌面應出現「EXAM」圖示，點兩下即可開始考試。"
echo "======================================"
