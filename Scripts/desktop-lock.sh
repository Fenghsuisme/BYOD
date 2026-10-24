#!/usr/bin/env bash
#
# 鎖定 / 還原 GNOME 的「逃脫」快速鍵與滑鼠操作（考試碟專用）。
# 由 run.sh 在啟動時 lock、結束時 unlock。
#
# gsettings 的修改會寫入 dconf（重開機後仍在），因此 lock 時先把原值存到狀態檔，
# unlock 時逐一寫回原值。若上次異常結束（當機 / 斷電）留下狀態檔，
# 下次 lock 會沿用舊狀態檔（保留真正的原值），unlock 一樣能完整還原。
#
# 用法： ./Scripts/desktop-lock.sh lock | unlock
#
set -euo pipefail

ACTION="${1:-lock}"

if ! command -v gsettings >/dev/null 2>&1; then
    echo "（非 GNOME 或無 gsettings，略過桌面鎖定）"
    exit 0
fi

STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/byod-kiosk"
STATE_FILE="$STATE_DIR/desktop-lock.state"

WM=org.gnome.desktop.wm.keybindings
MUTTER_KB=org.gnome.mutter.keybindings
SHELL_KB=org.gnome.shell.keybindings
MEDIA_KB=org.gnome.settings-daemon.plugins.media-keys

# 鎖定項目：「schema key 鎖定值」，每行一項。不存在的 key（版本差異）會自動略過。
LOCK_ITEMS=$(cat <<EOF
org.gnome.desktop.wm.preferences mouse-button-modifier ''
org.gnome.mutter overlay-key ''
org.gnome.mutter edge-tiling false
org.gnome.desktop.interface enable-hot-corners false
org.gnome.shell.extensions.dash-to-dock hot-keys false
$WM close []
$WM minimize []
$WM maximize []
$WM unmaximize []
$WM toggle-maximized []
$WM toggle-fullscreen []
$WM begin-move []
$WM begin-resize []
$WM activate-window-menu []
$WM show-desktop []
$WM panel-run-dialog []
$WM panel-main-menu []
$WM switch-applications []
$WM switch-applications-backward []
$WM switch-windows []
$WM switch-windows-backward []
$WM switch-group []
$WM switch-group-backward []
$WM switch-panels []
$WM switch-panels-backward []
$WM cycle-windows []
$WM cycle-windows-backward []
$WM cycle-group []
$WM cycle-group-backward []
$WM cycle-panels []
$WM cycle-panels-backward []
$WM switch-to-workspace-left []
$WM switch-to-workspace-right []
$WM switch-to-workspace-up []
$WM switch-to-workspace-down []
$WM switch-to-workspace-last []
$WM switch-to-workspace-1 []
$WM switch-to-workspace-2 []
$WM switch-to-workspace-3 []
$WM switch-to-workspace-4 []
$WM move-to-workspace-left []
$WM move-to-workspace-right []
$WM move-to-workspace-up []
$WM move-to-workspace-down []
$WM move-to-workspace-last []
$WM move-to-workspace-1 []
$WM move-to-workspace-2 []
$WM move-to-workspace-3 []
$WM move-to-workspace-4 []
$WM move-to-monitor-left []
$WM move-to-monitor-right []
$WM move-to-monitor-up []
$WM move-to-monitor-down []
$MUTTER_KB toggle-tiled-left []
$MUTTER_KB toggle-tiled-right []
$MUTTER_KB switch-monitor []
$SHELL_KB toggle-overview []
$SHELL_KB toggle-application-view []
$SHELL_KB toggle-message-tray []
$SHELL_KB toggle-quick-settings []
$SHELL_KB focus-active-notification []
$SHELL_KB open-application-menu []
$SHELL_KB shift-overview-up []
$SHELL_KB shift-overview-down []
$SHELL_KB switch-to-application-1 []
$SHELL_KB switch-to-application-2 []
$SHELL_KB switch-to-application-3 []
$SHELL_KB switch-to-application-4 []
$SHELL_KB switch-to-application-5 []
$SHELL_KB switch-to-application-6 []
$SHELL_KB switch-to-application-7 []
$SHELL_KB switch-to-application-8 []
$SHELL_KB switch-to-application-9 []
$SHELL_KB screenshot []
$SHELL_KB screenshot-window []
$SHELL_KB show-screenshot-ui []
$SHELL_KB show-screen-recording-ui []
$MEDIA_KB terminal []
$MEDIA_KB home []
$MEDIA_KB search []
$MEDIA_KB www []
$MEDIA_KB email []
$MEDIA_KB calculator []
$MEDIA_KB control-center []
$MEDIA_KB help []
$MEDIA_KB logout []
$MEDIA_KB screensaver []
EOF
)

# key 存在才回傳 0（不同 GNOME / Ubuntu 版本的 key 不盡相同）
has_key() { gsettings get "$1" "$2" >/dev/null 2>&1; }

restore() {
    [[ -f "$STATE_FILE" ]] || return 0
    while IFS=$'\t' read -r schema key value; do
        [[ -z "$schema" ]] && continue
        gsettings set "$schema" "$key" "$value" 2>/dev/null || true
    done < "$STATE_FILE"
    rm -f "$STATE_FILE"
}

if [[ "$ACTION" == "lock" ]]; then
    mkdir -p "$STATE_DIR"
    # 上次異常結束留下的狀態檔才是真正的原值，不可覆蓋
    if [[ ! -f "$STATE_FILE" ]]; then
        tmp="$STATE_FILE.tmp"
        : > "$tmp"
        while read -r schema key _; do
            has_key "$schema" "$key" || continue
            printf '%s\t%s\t%s\n' "$schema" "$key" "$(gsettings get "$schema" "$key")" >> "$tmp"
        done <<< "$LOCK_ITEMS"
        mv "$tmp" "$STATE_FILE"
    fi
    while read -r schema key value; do
        has_key "$schema" "$key" || continue
        gsettings set "$schema" "$key" "$value" 2>/dev/null || true
    done <<< "$LOCK_ITEMS"
    echo "已鎖定桌面快速鍵（Super / Super+拖曳 / Alt+Tab / Alt+F4 / 工作區 / Ctrl+Alt+T …）"
else
    restore
    echo "已還原桌面快速鍵"
fi
