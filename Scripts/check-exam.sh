#!/usr/bin/env bash
#
# 考試機健康檢查：Clonezilla 複製後（或考試前）在每台機器上跑一次，約 10 秒。
# 只讀取、不修改任何設定。最後印出的「機器 ID / IP」請與其他台比對：不可重複。
#
# 用法： ./Scripts/check-exam.sh
#
set -uo pipefail

SELF="$(readlink -f "${BASH_SOURCE[0]}")"
ROOT="$(dirname "$(dirname "$SELF")")"
cd "$ROOT"

fails=0
ok()   { echo "  ✓ $1"; }
fail() { echo "  ✗ $1"; fails=$((fails + 1)); }
info() { echo "  · $1"; }

echo "== 考試程式 =="
if [[ -x dist/ByodKioskBrowser ]]; then
    ok "dist/ByodKioskBrowser（版本 $(cut -c1-7 dist/BUILD_COMMIT 2>/dev/null || echo 未知)）"
else
    fail "找不到 dist/ByodKioskBrowser"
fi
if [[ -f "${XDG_STATE_HOME:-$HOME/.local/state}/byod-kiosk/desktop-lock.state" ]]; then
    fail "快速鍵停在鎖定狀態（上次異常結束）；執行 ./Scripts/desktop-lock.sh unlock 還原"
else
    ok "快速鍵未殘留鎖定"
fi

echo "== 編譯器 =="
tmp="$(mktemp -d)"
printf '#include <iostream>\nint main(){std::cout<<"ok";}\n' > "$tmp/t.cpp"
if command -v g++ >/dev/null 2>&1 && g++ "$tmp/t.cpp" -o "$tmp/t" 2>/dev/null && [[ "$("$tmp/t")" == ok ]]; then
    ok "g++ 可編譯執行"
else
    fail "g++ 無法編譯（sudo ./Scripts/install-linux-deps.sh）"
fi
rm -rf "$tmp"

echo "== 中文輸入 =="
if dpkg-query -W -f='${Status}' ibus-chewing 2>/dev/null | grep -q "ok installed"; then
    ok "已安裝新酷音"
else
    fail "未安裝 ibus-chewing"
fi
if pgrep -x ibus-daemon >/dev/null 2>&1; then
    ok "ibus-daemon 執行中"
else
    fail "ibus-daemon 未執行（登出再登入一次）"
fi

echo "== 網路 =="
ip4="$(ip -4 -o addr show scope global 2>/dev/null | awk '{print $4}' | paste -sd' ' -)"
if [[ -n "$ip4" ]]; then ok "IP：$ip4"; else fail "沒有取得 IP"; fi
if command -v curl >/dev/null 2>&1; then
    code="$(curl -s -o /dev/null -w '%{http_code}' --connect-timeout 10 --max-time 20 https://judge.gai.tw/ || true)"
    if [[ "$code" =~ ^[23] ]]; then
        ok "連得到 judge.gai.tw（HTTP $code）"
    else
        fail "連不到 judge.gai.tw（HTTP ${code:-無回應}）"
    fi
else
    fail "找不到 curl，無法測試連線"
fi

echo "== 硬體 / 桌面（僅供參考）=="
info "顯示卡：$(lspci 2>/dev/null | grep -iE 'vga|3d|display' | sed 's/^[^ ]* //' | paste -sd';' - || echo 未知)"
info "工作階段：${XDG_SESSION_TYPE:-未知}"
info "若程式開啟後立刻閃退，先試： BYOD_DISABLE_GPU=1 ./run.sh"

mid="$(cat /etc/machine-id 2>/dev/null)"
echo ""
echo "======================================"
echo " 機器 ID：${mid:0:8}    IP：${ip4:-無}"
echo " ↑ 與其他台比對，兩者都不可重複（重複代表母碟沒先跑 prepare-image.sh）"
if [[ -z "$mid" ]]; then
    echo " ✗ machine-id 是空的：重開機一次即會自動產生"
    fails=$((fails + 1))
fi
if [[ $fails -eq 0 ]]; then
    echo " 結果：全部通過"
else
    echo " 結果：$fails 項未通過"
fi
echo "======================================"
[[ $fails -eq 0 ]]
