#!/usr/bin/env bash
#
# 在本機安裝「BYOD 防弊瀏覽器」桌面捷徑（每台機器跑一次即可）。
# 會依「這個專案資料夾的實際位置」產生捷徑，並設定執行權限與信任，
# 同時放到桌面與應用程式清單（Activities 可搜尋）。
#
# 用法： ./Scripts/install-shortcut.sh
#
set -euo pipefail

# 專案根目錄 = 此腳本上一層（解析符號連結）
SELF="$(readlink -f "${BASH_SOURCE[0]}")"
PROJECT_ROOT="$(dirname "$(dirname "$SELF")")"

if [[ ! -f "$PROJECT_ROOT/run.sh" ]]; then
    echo "找不到 $PROJECT_ROOT/run.sh，請確認腳本在專案的 Scripts/ 目錄下。" >&2
    exit 1
fi

write_shortcut() {
    local target="$1"
    mkdir -p "$(dirname "$target")"
    cat > "$target" <<EOF
[Desktop Entry]
Type=Application
Name=EXAM
Comment=競賽防弊瀏覽器（Kiosk）
Exec=bash -lc "cd '$PROJECT_ROOT' && ./run.sh"
Path=$PROJECT_ROOT
Terminal=true
Icon=$PROJECT_ROOT/App/Assets/exam-icon.svg
Categories=Education;
EOF
    chmod +x "$target"
    # 標記為信任，避免 GNOME 顯示「未信任 / 沒權限」
    gio set "$target" metadata::trusted true 2>/dev/null || true
}

# 桌面（取系統設定的桌面路徑，找不到就用 ~/Desktop）
DESKTOP_DIR="$(xdg-user-dir DESKTOP 2>/dev/null || true)"
[[ -z "${DESKTOP_DIR:-}" ]] && DESKTOP_DIR="$HOME/Desktop"
mkdir -p "$DESKTOP_DIR"
write_shortcut "$DESKTOP_DIR/EXAM.desktop"

# 應用程式清單（Activities 搜尋 EXAM 可開）
write_shortcut "$HOME/.local/share/applications/EXAM.desktop"
update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true

echo "完成。"
echo "  桌面捷徑：$DESKTOP_DIR/EXAM.desktop"
echo "  指向專案：$PROJECT_ROOT"
echo ""
echo "若桌面圖示仍顯示未信任，於圖示上按右鍵 → 允許執行（Allow Launching）。"
echo "或按 Super 鍵，搜尋「BYOD」直接開啟。"
