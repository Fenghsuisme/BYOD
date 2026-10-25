#!/usr/bin/env bash
#
# 考試碟的 GNOME 桌面設定（由 setup-exam.sh 呼叫，也可單獨執行）。
# 會完成：
#   1) 安裝 ibus 新酷音，並加入輸入來源
#   2) 工作區固定為 1 個
#   3) 左側 Dock 只留垃圾桶，並自動隱藏
#   4) 閒置時不自動休眠
#
# 這些是考試碟的永久設定（寫入 dconf），不會在結束時還原；
# 考試中的暫時鎖定由 desktop-lock.sh 負責。
#
# 用法： ./Scripts/setup-desktop.sh   （以考試帳號在桌面登入後執行，不要加 sudo）
#
set -euo pipefail

if [[ $EUID -eq 0 ]]; then
    echo "請以考試帳號執行（不要加 sudo）：桌面設定是寫入目前使用者的 dconf。" >&2
    exit 1
fi

as_root() {
    if command -v sudo >/dev/null 2>&1; then sudo "$@"; else "$@"; fi
}

echo "  - 安裝 ibus 新酷音…"
if command -v apt-get >/dev/null 2>&1; then
    as_root apt-get install -y ibus ibus-chewing
else
    echo "    非 apt 系統，請手動安裝 ibus 與 ibus-chewing。" >&2
fi

if ! command -v gsettings >/dev/null 2>&1 || [[ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ]]; then
    echo "    找不到 gsettings 或桌面工作階段（請在 GNOME 桌面登入後執行），略過桌面設定。" >&2
    exit 0
fi

# key 存在才設定（不同 GNOME / Ubuntu 版本的 key 不盡相同）
gset() {
    if gsettings get "$1" "$2" >/dev/null 2>&1; then
        gsettings set "$1" "$2" "$3"
    else
        echo "    略過（此版本沒有 $1 $2）"
    fi
}

echo "  - 加入新酷音輸入來源…"
SOURCES_SCHEMA=org.gnome.desktop.input-sources
current="$(gsettings get "$SOURCES_SCHEMA" sources)"
if [[ "$current" == *"'chewing'"* ]]; then
    echo "    已有新酷音"
elif [[ "$current" == *"[]"* ]]; then
    gsettings set "$SOURCES_SCHEMA" sources "[('xkb', 'us'), ('ibus', 'chewing')]"
else
    gsettings set "$SOURCES_SCHEMA" sources "${current%]}, ('ibus', 'chewing')]"
fi
# 讓執行中的 ibus 認得剛裝好的新酷音
if command -v ibus >/dev/null 2>&1 && pgrep -x ibus-daemon >/dev/null 2>&1; then
    ibus write-cache >/dev/null 2>&1 || true
    ibus restart >/dev/null 2>&1 || true
fi

echo "  - 工作區固定為 1 個…"
gset org.gnome.mutter dynamic-workspaces false
gset org.gnome.desktop.wm.preferences num-workspaces 1

echo "  - Dock 只留垃圾桶、自動隱藏…"
DOCK=org.gnome.shell.extensions.dash-to-dock
gset "$DOCK" show-favorites false
gset "$DOCK" show-running false
gset "$DOCK" show-mounts false
gset "$DOCK" show-show-apps-button false
gset "$DOCK" show-trash true
# dock-fixed=false 才會自動隱藏；intellihide=false 讓它一律縮進去（滑鼠碰左緣才出現）
gset "$DOCK" dock-fixed false
gset "$DOCK" autohide true
gset "$DOCK" intellihide false

echo "  - 閒置不自動休眠…"
POWER=org.gnome.settings-daemon.plugins.power
gset "$POWER" sleep-inactive-ac-type 'nothing'
gset "$POWER" sleep-inactive-battery-type 'nothing'

echo "  桌面設定完成。若選字窗沒出現，登出再登入一次即可。"
