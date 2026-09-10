#!/usr/bin/env bash
#
# 一鍵建置「考試環境碟」（在這隻碟上跑一次，之後整碟複製即可）。
# 會完成：
#   1) 安裝 .NET 8 SDK（若尚未安裝）
#   2) 安裝系統依賴（CEF 函式庫 + g++）
#   3) 佈署 Monaco，並預先建置（還原 CEF 套件，讓複製後的碟開箱即用、免再下載）
#   4) 在桌面建立 EXAM 啟動圖示
#
# 用法： ./Scripts/setup-exam.sh
#
set -euo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
ROOT="$(dirname "$(dirname "$SELF")")"
cd "$ROOT"

echo "==> (1/4) 確認 .NET 8 SDK…"
if ! command -v dotnet >/dev/null 2>&1; then
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then
        export PATH="$HOME/.dotnet:$PATH"
    else
        echo "    安裝 .NET 8 SDK 到 ~/.dotnet …"
        curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        bash /tmp/dotnet-install.sh --channel 8.0
        export PATH="$HOME/.dotnet:$PATH"
        grep -q '.dotnet' "$HOME/.bashrc" 2>/dev/null || \
            echo 'export PATH="$HOME/.dotnet:$PATH"' >> "$HOME/.bashrc"
    fi
fi
echo "    dotnet $(dotnet --version)"

echo ""
echo "==> (2/4) 系統依賴（CEF 函式庫 + g++）…"
if command -v sudo >/dev/null 2>&1; then
    sudo ./Scripts/install-linux-deps.sh
else
    ./Scripts/install-linux-deps.sh
fi

echo ""
echo "==> (3/4) Monaco + 預先建置（還原 CEF 套件）…"
[[ -f App/Assets/webapp/vs/loader.js ]] || ./Scripts/fetch-monaco.sh
dotnet build App/ByodKioskBrowser.App.csproj -c Debug

echo ""
echo "==> (4/4) 建立 EXAM 桌面圖示…"
./Scripts/install-shortcut.sh

echo ""
echo "======================================"
echo " 完成！這隻考試環境碟已就緒："
echo "   - 桌面已有 EXAM 圖示，點兩下開始考試"
echo "   - 環境（SDK / 依賴 / Monaco / 已建置）皆已內含"
echo " 接下來把整碟完整複製即可，所有複本開箱即用。"
echo "======================================"
