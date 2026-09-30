#!/usr/bin/env bash
#
# 在本機安裝兩個桌面捷徑（每台機器跑一次即可）：
#   - 程式設計          → judge 網站
#   - OOP與資料結構     → 題目 PDF（BYOD_MODE=pdf）
# 依「這個專案資料夾的實際位置」產生，並設定執行權限與信任，
# 同時放到桌面與應用程式清單（Activities 可搜尋）。
#
# 用法： ./Scripts/install-shortcut.sh
#
set -euo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
PROJECT_ROOT="$(dirname "$(dirname "$SELF")")"

if [[ ! -f "$PROJECT_ROOT/run.sh" ]]; then
    echo "找不到 $PROJECT_ROOT/run.sh，請確認腳本在專案的 Scripts/ 目錄下。" >&2
    exit 1
fi

# write_shortcut <檔案路徑> <顯示名稱> <啟動指令>
write_shortcut() {
    local target="$1" name="$2" exec_cmd="$3"
    mkdir -p "$(dirname "$target")"
    cat > "$target" <<EOF
[Desktop Entry]
Type=Application
Name=$name
Comment=競賽防弊瀏覽器（Kiosk）
Exec=bash -lc "cd '$PROJECT_ROOT' && $exec_cmd"
Path=$PROJECT_ROOT
Terminal=true
Icon=$PROJECT_ROOT/App/Assets/exam-icon.svg
Categories=Education;
EOF
    chmod +x "$target"
    gio set "$target" metadata::trusted true 2>/dev/null || true
}

DESKTOP_DIR="$(xdg-user-dir DESKTOP 2>/dev/null || true)"
[[ -z "${DESKTOP_DIR:-}" ]] && DESKTOP_DIR="$HOME/Desktop"
APPS_DIR="$HOME/.local/share/applications"
mkdir -p "$DESKTOP_DIR" "$APPS_DIR"

# 兩個入口
install_pair() {
    local file="$1" name="$2" cmd="$3"
    write_shortcut "$DESKTOP_DIR/$file" "$name" "$cmd"
    write_shortcut "$APPS_DIR/$file" "$name" "$cmd"
}

install_pair "EXAM-程式設計.desktop"      "程式設計"        "./run.sh"
install_pair "EXAM-OOP資料結構.desktop"   "OOP與資料結構"   "BYOD_MODE=pdf ./run.sh"

update-desktop-database "$APPS_DIR" 2>/dev/null || true

echo "完成。已在桌面建立兩個圖示："
echo "  - 程式設計       （judge 網站）"
echo "  - OOP與資料結構  （題目 PDF）"
echo ""
echo "若圖示顯示未信任，右鍵 → 允許執行；或按 Super 搜尋名稱開啟。"
echo "PDF 模式需在專案根目錄的 pdf-id.txt 填入 Google Drive 檔案 ID。"
