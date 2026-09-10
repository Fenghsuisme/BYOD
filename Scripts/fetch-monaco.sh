#!/usr/bin/env bash
#
# 下載 Monaco Editor 並佈署到 App/Assets/webapp/vs（本地資源，不外連 CDN）。
# 純 bash（curl + tar），Linux/macOS 皆可用，不需 Node.js / pwsh。
#
# 用法： ./Scripts/fetch-monaco.sh [版本，預設 0.52.2]
#
set -euo pipefail

VERSION="${1:-0.52.2}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
TARGET_VS="$PROJECT_ROOT/App/Assets/webapp/vs"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

URL="https://registry.npmjs.org/monaco-editor/-/monaco-editor-$VERSION.tgz"
echo "下載 Monaco Editor $VERSION ..."
echo "  $URL"

# curl 或 wget 皆可
if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$URL" -o "$TMP/monaco.tgz"
elif command -v wget >/dev/null 2>&1; then
    wget -qO "$TMP/monaco.tgz" "$URL"
else
    echo "找不到 curl 或 wget，無法下載。" >&2
    exit 1
fi

echo "解壓中 ..."
tar -xzf "$TMP/monaco.tgz" -C "$TMP"

SRC="$TMP/package/min/vs"
if [[ ! -d "$SRC" ]]; then
    echo "找不到 $SRC，套件結構可能已變更。" >&2
    exit 1
fi

mkdir -p "$TARGET_VS"
# 清除舊內容（保留說明檔）
find "$TARGET_VS" -mindepth 1 ! -name 'PLACE_MONACO_HERE.md' -exec rm -rf {} + 2>/dev/null || true

echo "複製 min/vs → App/Assets/webapp/vs ..."
cp -R "$SRC/." "$TARGET_VS/"

echo "完成。Monaco 本地資源已就位：$TARGET_VS"
