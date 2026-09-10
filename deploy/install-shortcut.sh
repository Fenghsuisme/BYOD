#!/usr/bin/env bash
#
# 發佈包版：在本機建立 EXAM 桌面圖示（指向本資料夾的 run.sh）。
#
set -euo pipefail

ROOT="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"

write_shortcut() {
    local target="$1"
    mkdir -p "$(dirname "$target")"
    cat > "$target" <<EOF
[Desktop Entry]
Type=Application
Name=EXAM
Comment=競賽防弊瀏覽器（Kiosk）
Exec=bash -lc "cd '$ROOT' && ./run.sh"
Path=$ROOT
Terminal=true
Icon=$ROOT/exam-icon.svg
Categories=Education;
EOF
    chmod +x "$target"
    gio set "$target" metadata::trusted true 2>/dev/null || true
}

DESKTOP_DIR="$(xdg-user-dir DESKTOP 2>/dev/null || true)"
[[ -z "${DESKTOP_DIR:-}" ]] && DESKTOP_DIR="$HOME/Desktop"
mkdir -p "$DESKTOP_DIR"
write_shortcut "$DESKTOP_DIR/EXAM.desktop"
write_shortcut "$HOME/.local/share/applications/EXAM.desktop"
update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true

echo "已建立 EXAM 桌面圖示：$DESKTOP_DIR/EXAM.desktop"
echo "若顯示未信任，右鍵圖示 → 允許執行；或按 Super 搜尋「EXAM」。"
