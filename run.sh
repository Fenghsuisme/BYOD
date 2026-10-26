#!/usr/bin/env bash
#
# 開發啟動器：一鍵跑起 BYOD 防弊瀏覽器（含編輯器）。
# 有 dist/（setup-exam.sh 下載的預建成品）就直接執行它；
# 否則為開發模式：確認 dotnet 在 PATH、必要時佈署 Monaco，再執行 dotnet run。
#
# 用法：
#   ./run.sh                      一般啟動（全螢幕 kiosk）
#   BYOD_WINDOWED=1 ./run.sh       視窗模式（有邊框，方便觀察）
#   ./run.sh -c Release            傳遞額外參數給 dotnet run（開發模式）
#   BYOD_DEV=1 ./run.sh            有 dist/ 也強制用原始碼 dotnet run
#
# 離開：Ctrl+Alt+Shift+Q，或右上「結束考試」按鈕。
#
set -euo pipefail

# 切到專案根目錄（解析符號連結，允許從桌面捷徑呼叫）
SELF="$(readlink -f "${BASH_SOURCE[0]}" 2>/dev/null || echo "${BASH_SOURCE[0]}")"
cd "$(dirname "$SELF")"

PREBUILT=false
if [[ -x dist/ByodKioskBrowser && "${BYOD_DEV:-}" != "1" ]]; then
    PREBUILT=true
else
    # 確保 dotnet 在 PATH（dotnet-install.sh 預設裝於 ~/.dotnet）
    if ! command -v dotnet >/dev/null 2>&1; then
        if [[ -x "$HOME/.dotnet/dotnet" ]]; then
            export PATH="$HOME/.dotnet:$PATH"
            export DOTNET_ROOT="$HOME/.dotnet"
        else
            echo "找不到考試程式（dist/）也找不到 dotnet。請先執行 ./Scripts/setup-exam.sh。" >&2
            exit 1
        fi
    fi

    # Monaco 本地資源若尚未佈署，先抓一次
    if [[ ! -f "App/Assets/webapp/vs/loader.js" ]]; then
        echo "Monaco 尚未佈署，執行 Scripts/fetch-monaco.sh …"
        ./Scripts/fetch-monaco.sh
    fi
fi

# 題目 PDF 的 Google Drive 檔案 ID（PDF 模式用）；pdf-id.txt 內第一個非註解行
if [[ -f "pdf-id.txt" ]]; then
    PDF_ID="$(grep -vE '^\s*#' pdf-id.txt | head -n1 | tr -d '[:space:]')"
    [[ -n "$PDF_ID" ]] && export BYOD_PDF_ID="$PDF_ID"
fi

# 確保 C/C++ 編譯器存在，否則編輯器的「▶ 執行」無法編譯。
# 注意：Linux 用的是 g++ / gcc；MinGW 是「Windows 版 gcc」，
# 僅在 Windows 上部署時才需要，Linux 用不到。
if ! command -v g++ >/dev/null 2>&1; then
    echo "找不到 g++（C/C++ 編譯器）。將執行 Scripts/install-linux-deps.sh 安裝（需 sudo）…"
    if command -v sudo >/dev/null 2>&1; then
        sudo ./Scripts/install-linux-deps.sh
    else
        echo "請以 root 執行： ./Scripts/install-linux-deps.sh" >&2
    fi
fi

# 中文輸入法：CEF 在 Linux 沒接輸入法，App 內直接以 DBus 連 ibus（見 App/Browser/IBusInputMethod.cs），
# 不需設定 GTK_IM_MODULE。BYOD_IME_DEBUG=1 可在終端機印出每個按鍵交給輸入法的結果。

# 鎖定 GNOME 逃脫快速鍵（Alt+F4 / Alt+Tab / Super）；結束（含當機/Ctrl+C）時還原
./Scripts/desktop-lock.sh lock || true
# 同時清掉 App 被保底 Kill 時來不及刪除的暫存（CEF 快取 / 題目 PDF / 編譯暫存）
cleanup() {
    ./Scripts/desktop-lock.sh unlock || true
    local tmp="${TMPDIR:-/tmp}"
    rm -rf "$tmp"/ByodKiosk_* "$tmp"/byod_exam_*.pdf "$tmp"/byod_run_* 2>/dev/null || true
}
trap cleanup EXIT

# 終端輸出（含 CEF 原生訊息）同時記錄到 logs/terminal_<時間>.log
mkdir -p logs
# 「結束考試」時學生程式碼備份至 logs/<類別>_<時間>/
export BYOD_LOG_DIR="$PWD/logs"
LOG="logs/terminal_$(date +%Y-%m-%d_%H-%M-%S).log"
echo "啟動中…"
echo "終端輸出將同時記錄到：$LOG"
{
    echo "[BYOD] 輸入法行程：$(pgrep -a 'ibus|fcitx' 2>/dev/null | tr '\n' ';' || true)"
} | tee "$LOG"

# 用 tee 同時輸出到畫面與檔案；保留程式的結束碼
set -o pipefail
if [[ "$PREBUILT" == true ]]; then
    ./dist/ByodKioskBrowser "$@" 2>&1 | tee -a "$LOG"
else
    dotnet run --project App/ByodKioskBrowser.App.csproj "$@" 2>&1 | tee -a "$LOG"
fi
