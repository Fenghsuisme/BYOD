#!/usr/bin/env bash
#
# 鎖定 / 還原 GNOME 的「逃脫」快速鍵（考試碟專用）。
# 由 run.sh 在啟動時 lock、結束時 unlock（session 範圍，不永久改）。
#
# 用法： ./Scripts/desktop-lock.sh lock | unlock
#
set -euo pipefail

ACTION="${1:-lock}"

if ! command -v gsettings >/dev/null 2>&1; then
    echo "（非 GNOME 或無 gsettings，略過桌面鎖定）"
    exit 0
fi

WM=org.gnome.desktop.wm.keybindings
SHELL_KB=org.gnome.shell.keybindings

# 會停用的鍵（lock 設空、unlock 還原預設）
WM_KEYS=(close switch-applications switch-applications-backward \
         switch-windows switch-windows-backward switch-group switch-group-backward \
         show-desktop panel-run-dialog)

gset()   { gsettings set   "$1" "$2" "$3" 2>/dev/null || true; }
greset() { gsettings reset "$1" "$2"      2>/dev/null || true; }

if [[ "$ACTION" == "lock" ]]; then
    for k in "${WM_KEYS[@]}"; do gset "$WM" "$k" "[]"; done
    gset org.gnome.mutter overlay-key ""              # 停用 Super 開啟 Activities
    gset "$SHELL_KB" toggle-overview "[]"
    gset "$SHELL_KB" toggle-application-view "[]"
    echo "已鎖定桌面快速鍵（Alt+F4 / Alt+Tab / Super / 顯示桌面 …）"
else
    for k in "${WM_KEYS[@]}"; do greset "$WM" "$k"; done
    greset org.gnome.mutter overlay-key
    greset "$SHELL_KB" toggle-overview
    greset "$SHELL_KB" toggle-application-view
    echo "已還原桌面快速鍵"
fi
