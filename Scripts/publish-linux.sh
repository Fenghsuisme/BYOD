#!/usr/bin/env bash
#
# 發佈自包含綠色資料夾（含 .NET runtime + CEF + Monaco），供免 SDK 部署。
# 請在「有 .NET SDK 的 Linux 主機」上執行（會為 linux-x64 打包）。
#
# 用法： ./Scripts/publish-linux.sh [RID，預設 linux-x64]
#
set -euo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
ROOT="$(dirname "$(dirname "$SELF")")"
cd "$ROOT"

RID="${1:-linux-x64}"
OUT="dist/BYOD-$RID"

# 確保 dotnet 在 PATH
if ! command -v dotnet >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export PATH="$HOME/.dotnet:$PATH"
fi

echo "==> (1/4) 佈署 Monaco 本地資源…"
[[ -f App/Assets/webapp/vs/loader.js ]] || ./Scripts/fetch-monaco.sh

echo "==> (2/4) 發佈自包含（$RID）…"
rm -rf "$OUT"
dotnet publish App/ByodKioskBrowser.App.csproj \
    -c Release -r "$RID" --self-contained true -o "$OUT"

echo "==> (3/4) 複製部署腳本與圖示…"
cp deploy/run.sh deploy/install-shortcut.sh deploy/setup-exam.sh "$OUT/"
cp Scripts/install-linux-deps.sh "$OUT/"
cp App/Assets/exam-icon.svg "$OUT/"

echo "==> (4/4) 設定執行權限…"
chmod +x "$OUT"/*.sh "$OUT/ByodKioskBrowser" 2>/dev/null || true

echo ""
echo "======================================"
echo " 完成！綠色資料夾：$OUT"
echo ""
echo " 部署到考試機（有網路）："
echo "   1) 整包複製過去"
echo "   2) 進資料夾執行：  ./setup-exam.sh"
echo "   3) 桌面出現 EXAM，點兩下開始考試（該機免裝 .NET SDK）"
echo "======================================"
