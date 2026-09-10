#!/usr/bin/env bash
#
# 發佈包啟動器：直接執行自包含執行檔（免 .NET SDK）。
# 此檔位於發佈輸出資料夾根目錄，與 ByodKioskBrowser 執行檔同層。
#
# 離開：Ctrl+Alt+Shift+Q，或右上「結束考試」。
#
set -euo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}" 2>/dev/null || echo "${BASH_SOURCE[0]}")"
cd "$(dirname "$SELF")"

BIN="./ByodKioskBrowser"
if [[ ! -f "$BIN" ]]; then
    echo "找不到 $BIN，請確認在發佈輸出資料夾內執行。" >&2
    exit 1
fi

# 確保 C/C++ 編譯器存在（編輯器「執行」需要）
if ! command -v g++ >/dev/null 2>&1; then
    echo "缺少 g++，執行 install-linux-deps.sh（需 sudo）…"
    if command -v sudo >/dev/null 2>&1; then sudo ./install-linux-deps.sh; else ./install-linux-deps.sh; fi
fi

# 執行權限
chmod +x "$BIN" 2>/dev/null || true
find . -maxdepth 2 -type f -iname '*browserprocess*' -exec chmod +x {} \; 2>/dev/null || true

# 終端輸出寫入 logs
mkdir -p logs
LOG="logs/terminal_$(date +%Y-%m-%d_%H-%M-%S).log"
echo "啟動中…（離開：Ctrl+Alt+Shift+Q 或右上「結束考試」）"
echo "終端輸出記錄：$LOG"

set -o pipefail
"$BIN" "$@" 2>&1 | tee "$LOG"
