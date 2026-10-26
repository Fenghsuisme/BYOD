#!/usr/bin/env bash
#
# 下載 GitHub Actions 預先建好的 Linux x64 成品到 dist/（由 run.sh 執行）。
# 考試機不需要 .NET SDK、不需要抓 NuGet、不需要建置。
# 已是最新版就不重新下載；要更新考試機時：git pull 後再跑一次本腳本。
#
# 用法： ./Scripts/fetch-release.sh
#
set -euo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
ROOT="$(dirname "$(dirname "$SELF")")"
DIST="$ROOT/dist"
BASE_URL="${BYOD_RELEASE_URL:-https://github.com/Fenghsuisme/BYOD/releases/download/linux-latest}"

if ! command -v curl >/dev/null 2>&1; then
    echo "    找不到 curl，請先執行： sudo apt-get install -y curl" >&2
    exit 1
fi

# 連線逾時 + 自動重試：網路不穩時不會無限期卡住
CURL=(curl -fL --connect-timeout 20 --retry 3 --retry-delay 3)

remote_commit="$("${CURL[@]}" -sS "$BASE_URL/BUILD_COMMIT" | tr -d '[:space:]')" || remote_commit=""
local_commit="$(tr -d '[:space:]' 2>/dev/null < "$DIST/BUILD_COMMIT" || true)"
if [[ -n "$remote_commit" && "$remote_commit" == "$local_commit" && -x "$DIST/ByodKioskBrowser" ]]; then
    echo "    已是最新版（${remote_commit:0:7}），不需下載。"
    exit 0
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "    下載考試程式（約 200 MB）…"
"${CURL[@]}" --progress-bar "$BASE_URL/byod-linux-x64.tar.gz" -o "$TMP/byod.tar.gz"

echo "    解壓中…"
mkdir -p "$TMP/dist"
tar -xzf "$TMP/byod.tar.gz" -C "$TMP/dist"
if [[ ! -x "$TMP/dist/ByodKioskBrowser" ]]; then
    echo "    下載的檔案不完整（找不到 ByodKioskBrowser），請重跑本腳本。" >&2
    exit 1
fi

# 解壓成功才替換，避免下載一半把舊版弄壞；舊版裡的考試紀錄（exams/、logs/）搬到新版保留
for keep in exams logs; do
    if [[ -d "$DIST/$keep" ]]; then
        mv "$DIST/$keep" "$TMP/dist/$keep"
    fi
done
rm -rf "$DIST"
mv "$TMP/dist" "$DIST"
echo "    完成：$(cat "$DIST/BUILD_COMMIT" 2>/dev/null | cut -c1-7)"
