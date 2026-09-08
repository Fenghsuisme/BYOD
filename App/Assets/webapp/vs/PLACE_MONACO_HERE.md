# 此處需放置 Monaco Editor 本地資源

執行期會以 `vs/loader.js` 載入 Monaco，且**嚴禁外連 CDN**。
請將 Monaco Editor 套件中的 `min/vs` 目錄內容，複製到本資料夾（`Assets/webapp/vs`），
使結構如下：

```
Assets/webapp/vs/
├── loader.js
├── editor/
│   └── editor.main.js  等
├── base/
├── basic-languages/
└── language/
```

最簡單的方式：在 Windows 端執行專案根目錄的 `Scripts/fetch-monaco.ps1`，會自動下載並就位。

> 注意：這個 `.md` 檔僅為佔位說明，放置 Monaco 後可保留或刪除，不影響執行。
