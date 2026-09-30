#!/usr/bin/env bash
#
# 下載 pdf.js（UMD 版）並佈署到 App/Assets/webapp/pdfjs（本地顯示題目 PDF，不外連）。
# 用法： ./Scripts/fetch-pdfjs.sh [版本，預設 2.16.105]
#
set -euo pipefail

VERSION="${1:-2.16.105}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
TARGET="$PROJECT_ROOT/App/Assets/webapp/pdfjs"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

URL="https://registry.npmjs.org/pdfjs-dist/-/pdfjs-dist-$VERSION.tgz"
echo "下載 pdf.js $VERSION ..."
echo "  $URL"
if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$URL" -o "$TMP/pdfjs.tgz"
elif command -v wget >/dev/null 2>&1; then
    wget -qO "$TMP/pdfjs.tgz" "$URL"
else
    echo "找不到 curl 或 wget，無法下載。" >&2
    exit 1
fi

echo "解壓中 ..."
tar -xzf "$TMP/pdfjs.tgz" -C "$TMP"

SRC="$TMP/package/build"
if [[ ! -f "$SRC/pdf.min.js" || ! -f "$SRC/pdf.worker.min.js" ]]; then
    echo "找不到 pdf.min.js / pdf.worker.min.js，套件結構可能已變更。" >&2
    exit 1
fi

mkdir -p "$TARGET"
cp "$SRC/pdf.min.js" "$SRC/pdf.worker.min.js" "$TARGET/"

echo "完成。pdf.js 已就位：$TARGET"
