#!/usr/bin/env bash
#
# 一鍵建置「考試環境碟」（在這隻碟上跑一次，之後整碟複製即可）。
# 會完成：
#   1) 安裝 .NET 8 SDK（若尚未安裝）
#   2) 安裝系統依賴（CEF 函式庫 + g++）
#   3) 佈署 Monaco，並預先建置（還原 CEF 套件，讓複製後的碟開箱即用、免再下載）
#   4) 在桌面建立 EXAM 啟動圖示
#   5) GNOME 桌面設定：新酷音、工作區 1 個、Dock 只留垃圾桶並自動隱藏、閒置不休眠不鎖定
#   6) 安裝 Google Chrome（穩定版，Ubuntu 用的 .deb）
#
# 用法： ./Scripts/setup-exam.sh   （以考試帳號在桌面登入後執行，不要加 sudo）
#
set -euo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
ROOT="$(dirname "$(dirname "$SELF")")"
cd "$ROOT"

# 下載工具（curl 或 wget）；乾淨系統可能兩者皆無
download() {
    # download <URL> <輸出檔>
    if command -v curl >/dev/null 2>&1; then
        curl -sSL "$1" -o "$2"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO "$2" "$1"
    else
        return 1
    fi
}

echo "==> (0/6) 基本工具（curl / ca-certificates）…"
if ! command -v curl >/dev/null 2>&1 && ! command -v wget >/dev/null 2>&1; then
    if command -v apt-get >/dev/null 2>&1; then
        if command -v sudo >/dev/null 2>&1; then
            sudo apt-get update && sudo apt-get install -y curl ca-certificates
        else
            apt-get update && apt-get install -y curl ca-certificates
        fi
    else
        echo "    找不到 curl/wget，且非 apt 系統；請先手動安裝 curl。" >&2
        exit 1
    fi
fi

echo ""
echo "==> (1/6) 確認 .NET 8 SDK…"
if ! command -v dotnet >/dev/null 2>&1; then
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then
        export PATH="$HOME/.dotnet:$PATH"
    else
        echo "    安裝 .NET 8 SDK 到 ~/.dotnet …"
        download https://dot.net/v1/dotnet-install.sh /tmp/dotnet-install.sh
        bash /tmp/dotnet-install.sh --channel 8.0
        export PATH="$HOME/.dotnet:$PATH"
        grep -q '.dotnet' "$HOME/.bashrc" 2>/dev/null || \
            echo 'export PATH="$HOME/.dotnet:$PATH"' >> "$HOME/.bashrc"
    fi
fi
echo "    dotnet $(dotnet --version)"

echo ""
echo "==> (2/6) 系統依賴（CEF 函式庫 + g++）…"
if command -v sudo >/dev/null 2>&1; then
    sudo ./Scripts/install-linux-deps.sh
else
    ./Scripts/install-linux-deps.sh
fi

echo ""
echo "==> (3/6) Monaco / pdf.js + 預先建置（還原 CEF 套件）…"
[[ -f App/Assets/webapp/vs/loader.js ]] || ./Scripts/fetch-monaco.sh
[[ -f App/Assets/webapp/pdfjs/pdf.min.js ]] || ./Scripts/fetch-pdfjs.sh
dotnet build App/ByodKioskBrowser.App.csproj -c Debug

echo ""
echo "==> (4/6) 建立 EXAM 桌面圖示…"
./Scripts/install-shortcut.sh

echo ""
echo "==> (5/6) GNOME 桌面設定（新酷音 / 工作區 / Dock / 休眠）…"
./Scripts/setup-desktop.sh

echo ""
echo "==> (6/6) Google Chrome（穩定版）…"
if command -v google-chrome >/dev/null 2>&1; then
    echo "    已安裝：$(google-chrome --version)"
elif [[ "$(dpkg --print-architecture 2>/dev/null)" != "amd64" ]]; then
    echo "    Google Chrome 只提供 x64（amd64）版本，此機器略過。" >&2
else
    # 官方 .deb 安裝時會一併加入 Google 的 apt 套件庫，之後隨系統更新
    CHROME_DEB="$(mktemp --suffix=.deb)"
    download https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb "$CHROME_DEB"
    chmod 644 "$CHROME_DEB"
    if command -v sudo >/dev/null 2>&1; then
        sudo apt-get install -y "$CHROME_DEB"
    else
        apt-get install -y "$CHROME_DEB"
    fi
    rm -f "$CHROME_DEB"
    echo "    已安裝：$(google-chrome --version)"
fi

echo ""
echo "======================================"
echo " 完成！這隻考試環境碟已就緒："
echo "   - 桌面已有 EXAM 圖示，點兩下開始考試"
echo "   - 環境（SDK / 依賴 / Monaco / 已建置）皆已內含"
echo " 接下來把整碟完整複製即可，所有複本開箱即用。"
echo "======================================"
