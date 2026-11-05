#!/usr/bin/env bash
#
# 考試機的 Google Chrome：安裝 + 鎖定只能開 Moodle（交作業用）。由 setup-exam.sh 呼叫，也可單獨執行。
#   1) 安裝 Google Chrome 穩定版（Ubuntu .deb；已裝的略過）
#   2) 寫入 Chrome 企業政策：只允許 Moodle 網域、禁用開發者工具與下載、
#      每次關閉即清除登入狀態（避免下一位學生沿用上一位的帳號）
#
# 政策檔為全機生效：這台的 Chrome 只能用來交作業。要解除時刪除 $POLICY_FILE 即可。
#
# 用法： ./Scripts/setup-chrome.sh   （需 sudo 權限）
#
set -euo pipefail

MOODLE_HOST="${BYOD_MOODLE_HOST:-elearningv4.nuk.edu.tw}"
POLICY_FILE=/etc/opt/chrome/policies/managed/byod-moodle.json

as_root() {
    if command -v sudo >/dev/null 2>&1 && [[ $EUID -ne 0 ]]; then sudo "$@"; else "$@"; fi
}

if command -v google-chrome >/dev/null 2>&1; then
    echo "    已安裝：$(google-chrome --version)"
elif [[ "$(dpkg --print-architecture 2>/dev/null)" != "amd64" ]]; then
    echo "    Google Chrome 只提供 x64（amd64）版本，此機器略過。" >&2
    exit 0
else
    # 官方 .deb 安裝時會一併加入 Google 的 apt 套件庫
    CHROME_DEB="$(mktemp --suffix=.deb)"
    trap 'rm -f "$CHROME_DEB"' EXIT
    curl -fL --connect-timeout 20 --speed-limit 1024 --speed-time 60 --retry 3 --retry-delay 3 \
        --progress-bar https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb -o "$CHROME_DEB"
    chmod 644 "$CHROME_DEB"
    as_root apt-get -o DPkg::Lock::Timeout=600 install -y "$CHROME_DEB"
    echo "    已安裝：$(google-chrome --version)"
fi

echo "    寫入 Chrome 政策（只允許 $MOODLE_HOST）…"
as_root mkdir -p "$(dirname "$POLICY_FILE")"
# 允許清單另含 Moodle 登入頁實際載入的 CDN / 字型網域，少了版面會跑掉
as_root tee "$POLICY_FILE" >/dev/null <<EOF
{
  "URLBlocklist": ["*"],
  "URLAllowlist": [
    "$MOODLE_HOST",
    "cdn.jsdelivr.net",
    "fonts.googleapis.com",
    "fonts.gstatic.com"
  ],
  "HomepageLocation": "https://$MOODLE_HOST/",
  "RestoreOnStartup": 4,
  "RestoreOnStartupURLs": ["https://$MOODLE_HOST/"],
  "ForceEphemeralProfiles": true,
  "IncognitoModeAvailability": 1,
  "BrowserGuestModeEnabled": false,
  "BrowserSignin": 0,
  "SyncDisabled": true,
  "PasswordManagerEnabled": false,
  "DeveloperToolsAvailability": 2,
  "DownloadRestrictions": 3,
  "DefaultBrowserSettingEnabled": false,
  "MetricsReportingEnabled": false
}
EOF
as_root chmod 644 "$POLICY_FILE"
echo "    完成（chrome://policy 可確認生效）"
