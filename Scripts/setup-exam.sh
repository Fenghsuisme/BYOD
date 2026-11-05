#!/usr/bin/env bash
#
# 一鍵建置考試環境：每台考試機 git clone 本專案後各跑一次。
# 會完成：
#   1) 先關閉閒置休眠／關螢幕／鎖定（下載很久，避免中途休眠斷線）
#   2) 安裝基本工具（curl / ca-certificates）
#   3) 安裝系統依賴（g++ / gcc + CEF 函式庫；已裝的略過）
#   4) 下載 GitHub Actions 預先建好的考試程式到 dist/（約 200 MB，不需 .NET SDK、不需建置）
#   5) 安裝 Google Chrome 並鎖定只能開 Moodle（交作業用）
#   6) 在桌面建立 EXAM 與交作業圖示
#   7) GNOME 桌面設定：新酷音、工作區 1 個、Dock 只留垃圾桶並自動隱藏
#
# 用法： ./Scripts/setup-exam.sh   （以考試帳號在桌面登入後執行，不要加 sudo）
#
set -euo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
ROOT="$(dirname "$(dirname "$SELF")")"
cd "$ROOT"

echo "==> (1/7) 關閉閒置休眠／關螢幕／鎖定（避免下載中途休眠）…"
./Scripts/setup-desktop.sh power

echo ""
echo "==> (2/7) 基本工具（curl / ca-certificates）…"
# Ubuntu 26.04 桌面版預設只有 wget 沒有 curl；後續下載（進度條、重試）都用 curl，一律確保有裝
if ! command -v curl >/dev/null 2>&1; then
    if command -v apt-get >/dev/null 2>&1; then
        if command -v sudo >/dev/null 2>&1; then
            sudo apt-get -o DPkg::Lock::Timeout=600 update && sudo apt-get -o DPkg::Lock::Timeout=600 install -y curl ca-certificates
        else
            apt-get -o DPkg::Lock::Timeout=600 update && apt-get -o DPkg::Lock::Timeout=600 install -y curl ca-certificates
        fi
    else
        echo "    找不到 curl，且非 apt 系統；請先手動安裝 curl。" >&2
        exit 1
    fi
fi

echo ""
echo "==> (3/7) 系統依賴（g++ / gcc + CEF 函式庫，已裝的略過）…"
if command -v sudo >/dev/null 2>&1; then
    sudo ./Scripts/install-linux-deps.sh
else
    ./Scripts/install-linux-deps.sh
fi

echo ""
echo "==> (4/7) 下載預先建好的考試程式（不需 .NET SDK、不需建置）…"
./Scripts/fetch-release.sh

echo ""
echo "==> (5/7) Google Chrome（交作業到 Moodle 用，鎖定只能開 Moodle）…"
./Scripts/setup-chrome.sh

echo ""
echo "==> (6/7) 建立桌面圖示…"
./Scripts/install-shortcut.sh

echo ""
echo "==> (7/7) GNOME 桌面設定（新酷音 / 工作區 / Dock）…"
./Scripts/setup-desktop.sh

echo ""
echo "======================================"
echo " 完成！這台考試機已就緒："
echo "   - 桌面已有 EXAM 圖示，點兩下開始考試；考完以「交作業（Moodle）」圖示上傳"
echo "   - 請登出再登入一次，讓新酷音與 Dock 設定生效"
echo "======================================"
