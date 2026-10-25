#!/usr/bin/env bash
#
# 一鍵建置考試環境：每台考試機 git clone 本專案後各跑一次。
# 會完成：
#   1) 先關閉閒置休眠／關螢幕／鎖定（下載很久，避免中途休眠斷線）
#   2) 安裝基本工具（curl / ca-certificates）
#   3) 安裝 .NET 8 SDK（若尚未安裝）
#   4) 安裝系統依賴（CEF 函式庫 + g++）
#   5) 佈署 Monaco / pdf.js，並預先建置（下載約 200 MB 的 CEF 套件，會顯示進度）
#   6) 在桌面建立 EXAM 啟動圖示
#   7) GNOME 桌面設定：新酷音、工作區 1 個、Dock 只留垃圾桶並自動隱藏
#   8) 安裝 Google Chrome（穩定版，Ubuntu 用的 .deb）
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
        # 連線逾時 + 自動重試：網路不穩時不會無限期卡住
        curl -fsSL --connect-timeout 20 --retry 3 --retry-delay 3 "$1" -o "$2"
    elif command -v wget >/dev/null 2>&1; then
        wget -q -T 20 -t 3 -O "$2" "$1"
    else
        return 1
    fi
}

echo "==> (1/8) 關閉閒置休眠／關螢幕／鎖定（避免下載中途休眠）…"
./Scripts/setup-desktop.sh power

echo ""
echo "==> (2/8) 基本工具（curl / ca-certificates）…"
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
echo "==> (3/8) 確認 .NET 8 SDK…"
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
echo "==> (4/8) 系統依賴（CEF 函式庫 + g++）…"
if command -v sudo >/dev/null 2>&1; then
    sudo ./Scripts/install-linux-deps.sh
else
    ./Scripts/install-linux-deps.sh
fi

echo ""
echo "==> (5/8) Monaco / pdf.js + 預先建置（還原 CEF 套件）…"
[[ -f App/Assets/webapp/vs/loader.js ]] || ./Scripts/fetch-monaco.sh
[[ -f App/Assets/webapp/pdfjs/pdf.min.js ]] || ./Scripts/fetch-pdfjs.sh
# 先單獨 restore 並顯示下載進度（預設輸出在下載期間完全沒字，看起來像當機）；
# 第一次要從 nuget.org 抓約 200 MB（CEF），解壓後約 700 MB，慢的網路或隨身碟要等數分鐘
echo "    下載套件（第一次約 200 MB，請耐心等候）…"
restored=false
for attempt in 1 2 3; do
    if dotnet restore App/ByodKioskBrowser.App.csproj -v n 2>&1 \
        | awk '/GET https|OK https|Installed |error|warning NU/ { sub(/^[ \t]+/, ""); print "    " $0; fflush() }'; then
        restored=true
        break
    fi
    echo "    下載失敗（第 $attempt 次），5 秒後重試…" >&2
    sleep 5
done
if [[ "$restored" != true ]]; then
    echo "    套件下載失敗：請確認網路可連到 https://api.nuget.org 後重跑本腳本。" >&2
    exit 1
fi
echo "    建置中…"
dotnet build App/ByodKioskBrowser.App.csproj -c Debug --no-restore

echo ""
echo "==> (6/8) 建立 EXAM 桌面圖示…"
./Scripts/install-shortcut.sh

echo ""
echo "==> (7/8) GNOME 桌面設定（新酷音 / 工作區 / Dock）…"
./Scripts/setup-desktop.sh

echo ""
echo "==> (8/8) Google Chrome（穩定版）…"
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
echo " 完成！這台考試機已就緒："
echo "   - 桌面已有 EXAM 圖示，點兩下開始考試"
echo "   - 請登出再登入一次，讓新酷音與 Dock 設定生效"
echo "======================================"
