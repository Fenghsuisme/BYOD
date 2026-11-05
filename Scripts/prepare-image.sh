#!/usr/bin/env bash
#
# 母碟「去個人化」：setup-exam.sh 跑完、實測沒問題後，於 Clonezilla 擷取映像前最後執行一次。
# 讓複製出來的每台機器（可為不同型號）各自取得獨立身分、不互相衝突：
#   1) 檢查考試程式與依賴都已就緒（不完整就中止，避免做出壞映像）
#   2) 還原桌面快速鍵鎖定、清除母碟上的考試紀錄、暫存與 Chrome 登入狀態
#   3) 關閉自動更新（避免考試中 apt 佔鎖、跳更新視窗、偷偷換版本）
#   4) 網路設定解除綁定母碟網卡（MAC / 介面名稱），清除 DHCP 租約
#   5) 清空 machine-id（每台第一次開機各自重新產生 → DHCP 不再拿到同一個 IP）
#   6) 確認 UEFI 備援開機檔存在（不同主機板沒有母碟的 NVRAM 開機項目也能開機）
#
# 用法： ./Scripts/prepare-image.sh [--shutdown]   （以考試帳號在桌面執行，不要加 sudo；過程中會要求 sudo 密碼）
#   --shutdown：完成後直接關機
#
# 執行後「不要再開機進入母碟」：一開機就會重新產生 machine-id，須重跑本腳本。
#
set -euo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
ROOT="$(dirname "$(dirname "$SELF")")"
cd "$ROOT"

if [[ $EUID -eq 0 ]]; then
    echo "請以考試帳號執行（不要加 sudo）：要清的是考試帳號的桌面設定與紀錄。" >&2
    exit 1
fi

as_root() {
    if command -v sudo >/dev/null 2>&1; then sudo "$@"; else "$@"; fi
}

echo "==> (1/6) 檢查考試程式與依賴…"
problems=()
[[ -x dist/ByodKioskBrowser ]] || problems+=("找不到 dist/ByodKioskBrowser（請先跑 ./Scripts/setup-exam.sh）")
command -v g++ >/dev/null 2>&1 || problems+=("找不到 g++")
dpkg-query -W -f='${Status}' ibus-chewing 2>/dev/null | grep -q "ok installed" || problems+=("未安裝 ibus-chewing（新酷音）")
[[ -n "$(ls "$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"/EXAM-*.desktop 2>/dev/null)" ]] \
    || problems+=("桌面沒有 EXAM 圖示（請跑 ./Scripts/install-shortcut.sh）")
if [[ ${#problems[@]} -gt 0 ]]; then
    printf '    ✗ %s\n' "${problems[@]}" >&2
    echo "    母碟尚未就緒，未做任何變更。" >&2
    exit 1
fi
echo "    考試程式版本：$(cut -c1-7 dist/BUILD_COMMIT 2>/dev/null || echo 未知)"

echo ""
echo "==> (2/6) 還原快速鍵鎖定、清除考試紀錄、暫存與 Chrome 登入狀態…"
# 母碟測試時若異常結束，快速鍵會停在鎖定狀態；不還原的話每台複製機都會帶著
./Scripts/desktop-lock.sh unlock || true
rm -rf dist/logs dist/exams logs exams
rm -rf "${TMPDIR:-/tmp}"/ByodKiosk_* "${TMPDIR:-/tmp}"/byod_exam_*.pdf "${TMPDIR:-/tmp}"/byod_run_* 2>/dev/null || true
DESKTOP_DIR="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
rm -rf "$DESKTOP_DIR"/OOP\&DS_* 2>/dev/null || true
rm -f "$HOME/.bash_history"
# 母碟測試時若登入過 Moodle，不清掉的話每台複製機開 Chrome 都是那個帳號
rm -rf "$HOME/.config/google-chrome" "$HOME/.cache/google-chrome"
if [[ -f /etc/opt/chrome/policies/managed/byod-moodle.json ]]; then
    echo "    Chrome 已鎖定只能開 Moodle"
elif command -v google-chrome >/dev/null 2>&1; then
    echo "    ⚠ Chrome 尚未鎖定（請跑 ./Scripts/setup-chrome.sh），學生考完可用它上任何網站" >&2
fi

echo ""
echo "==> (3/6) 關閉自動更新…"
if command -v systemctl >/dev/null 2>&1; then
    for unit in apt-daily.timer apt-daily-upgrade.timer unattended-upgrades.service; do
        as_root systemctl disable --now "$unit" 2>/dev/null || true
    done
fi
if [[ -d /etc/apt/apt.conf.d ]]; then
    printf 'APT::Periodic::Update-Package-Lists "0";\nAPT::Periodic::Unattended-Upgrade "0";\n' \
        | as_root tee /etc/apt/apt.conf.d/20auto-upgrades >/dev/null
fi
# 不跳「有可用更新」視窗（蓋在考試畫面上）
gsettings set com.ubuntu.update-notifier no-show-notifications true 2>/dev/null || true

echo ""
echo "==> (4/6) 網路設定解除綁定母碟網卡…"
if command -v nmcli >/dev/null 2>&1; then
    while IFS=: read -r uuid type; do
        case "$type" in
            802-3-ethernet)
                # 有線網路刪掉即可：NetworkManager 會在每台機器的網卡上自動建立預設 DHCP 連線
                as_root nmcli connection delete "$uuid" >/dev/null || true ;;
            802-11-wireless)
                # Wi-Fi 保留（含密碼），只拿掉綁定母碟網卡的欄位
                as_root nmcli connection modify "$uuid" connection.interface-name "" \
                    802-11-wireless.mac-address "" >/dev/null || true ;;
        esac
    done < <(nmcli -t -f UUID,TYPE connection show)
fi
as_root rm -f /var/lib/NetworkManager/*.lease /var/lib/dhcp/*.leases 2>/dev/null || true

echo ""
echo "==> (5/6) 清空 machine-id（每台第一次開機各自重新產生）…"
# 清成空檔（不是刪除）：systemd 開機時看到空檔會自動產生新的 ID
as_root truncate -s 0 /etc/machine-id
as_root rm -f /var/lib/dbus/machine-id
as_root ln -s /etc/machine-id /var/lib/dbus/machine-id
if ls /etc/ssh/ssh_host_*_key >/dev/null 2>&1; then
    echo "    注意：此機裝有 SSH 伺服器，所有複製機會共用同一組 host key（不影響考試）。"
fi

echo ""
echo "==> (6/6) 檢查 UEFI 備援開機檔…"
EFI=/boot/efi/EFI
if [[ ! -d /sys/firmware/efi ]]; then
    echo "    母碟以傳統 BIOS 開機，略過（複製機的 BIOS 也須設為 Legacy / CSM 開機）。"
elif as_root test -f "$EFI/BOOT/BOOTX64.EFI"; then
    echo "    已存在：$EFI/BOOT/BOOTX64.EFI"
elif as_root test -f "$EFI/ubuntu/shimx64.efi"; then
    # 與 Ubuntu 安裝程式的備援配置相同：shim 當 BOOTX64.EFI，fbx64 會在新機器上自動補建開機項目
    as_root mkdir -p "$EFI/BOOT"
    as_root cp "$EFI/ubuntu/shimx64.efi" "$EFI/BOOT/BOOTX64.EFI"
    for f in grubx64.efi mmx64.efi fbx64.efi; do
        if as_root test -f "$EFI/ubuntu/$f"; then
            as_root cp "$EFI/ubuntu/$f" "$EFI/BOOT/$f"
        fi
    done
    echo "    已建立 $EFI/BOOT/BOOTX64.EFI"
else
    echo "    ⚠ 找不到 $EFI/ubuntu/shimx64.efi，無法建立備援開機檔；複製機若開不了機，" >&2
    echo "      請在 Clonezilla 還原時保留「更新 EFI NVRAM 開機項目」選項。" >&2
fi

sync
echo ""
echo "======================================"
echo " 母碟已可擷取。接下來："
echo "   1) 關機，用 Clonezilla 擷取整顆硬碟（device-image / savedisk）"
echo "   2) 不要再開機進入母碟：一開機就會產生新的 machine-id，須重跑本腳本"
echo "   3) 還原到比母碟小的硬碟時，Clonezilla 進階選項選 -icds 與 -k1（依比例調整分割區）"
echo "   4) 複製機開機後跑 ./Scripts/check-exam.sh 確認狀態"
echo "======================================"

if [[ "${1:-}" == "--shutdown" ]]; then
    as_root systemctl poweroff
fi
