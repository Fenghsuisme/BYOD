#!/usr/bin/env bash
#
# 開發啟動器：一鍵跑起 BYOD 防弊瀏覽器（含編輯器）。
# 會自動確認 dotnet 在 PATH、必要時佈署 Monaco，再執行 dotnet run。
#
# 用法：
#   ./run.sh                      一般啟動（全螢幕 kiosk）
#   BYOD_WINDOWED=1 ./run.sh       視窗模式（有邊框，方便觀察）
#   ./run.sh -c Release            傳遞額外參數給 dotnet run
#
# 離開：Ctrl+Alt+Shift+Q，或右上「結束考試」按鈕。
#
set -euo pipefail

# 切到專案根目錄（腳本所在位置）
cd "$(dirname "${BASH_SOURCE[0]}")"

# 確保 dotnet 在 PATH（dotnet-install.sh 預設裝於 ~/.dotnet）
if ! command -v dotnet >/dev/null 2>&1; then
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then
        export PATH="$HOME/.dotnet:$PATH"
        export DOTNET_ROOT="$HOME/.dotnet"
    else
        echo "找不到 dotnet。請先安裝 .NET 8 SDK（見 README）。" >&2
        exit 1
    fi
fi

# Monaco 本地資源若尚未佈署，先抓一次
if [[ ! -f "App/Assets/webapp/vs/loader.js" ]]; then
    echo "Monaco 尚未佈署，執行 Scripts/fetch-monaco.sh …"
    ./Scripts/fetch-monaco.sh
fi

# 確保 C/C++ 編譯器存在，否則編輯器的「▶ 執行」無法編譯。
# 注意：Linux 用的是 g++ / gcc（build-essential）；MinGW 是「Windows 版 gcc」，
# 僅在 Windows 上部署時才需要，Linux 用不到。
if ! command -v g++ >/dev/null 2>&1; then
    echo "找不到 g++（C/C++ 編譯器）。將執行 Scripts/install-linux-deps.sh 安裝（需 sudo）…"
    if command -v sudo >/dev/null 2>&1; then
        sudo ./Scripts/install-linux-deps.sh
    else
        echo "請以 root 執行： ./Scripts/install-linux-deps.sh" >&2
    fi
fi

echo "啟動中…（離開：Ctrl+Alt+Shift+Q 或右上「結束考試」）"
exec dotnet run --project App/ByodKioskBrowser.App.csproj "$@"
