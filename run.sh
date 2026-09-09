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

echo "啟動中…（離開：Ctrl+Alt+Shift+Q 或右上「結束考試」）"
exec dotnet run --project App/ByodKioskBrowser.App.csproj "$@"
