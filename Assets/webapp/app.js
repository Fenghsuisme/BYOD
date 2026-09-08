(function () {
    "use strict";

    // ----- 與 C# 溝通的橋接 -----
    var host = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;

    function postToHost(obj) {
        if (host) {
            host.postMessage(JSON.stringify(obj));
        }
    }

    // ----- Monaco 載入器保護：若本地資源缺失則顯示提示 -----
    if (window.__monacoLoadFailed || typeof require === "undefined") {
        document.getElementById("editor").classList.add("hidden");
        document.getElementById("fallback").classList.remove("hidden");
        return;
    }

    require.config({ paths: { vs: "vs" } });

    require(["vs/editor/editor.main"], function () {
        var editor = monaco.editor.create(document.getElementById("editor"), {
            value: "",
            language: "cpp",
            theme: "vs-dark",
            fontSize: 14,
            automaticLayout: true,
            minimap: { enabled: false },
            contextmenu: false,        // 停用編輯器右鍵選單
            scrollBeyondLastLine: false,
            renderWhitespace: "selection"
        });

        // ================= 剪貼簿隔離 =================
        // 僅允許「編輯器內部暫存區」的複製 / 剪下 / 貼上，
        // 封鎖來自系統剪貼簿的外部資料（防止本機 IDE / Copilot 生成後貼入）。
        var internalClipboard = "";

        function getSelectedText() {
            var sel = editor.getSelection();
            if (!sel || sel.isEmpty()) {
                return "";
            }
            return editor.getModel().getValueInRange(sel);
        }

        function internalCopy() {
            var text = getSelectedText();
            if (text) {
                internalClipboard = text;
            }
        }

        function internalCut() {
            var sel = editor.getSelection();
            var text = getSelectedText();
            if (text) {
                internalClipboard = text;
                editor.executeEdits("internal-clipboard", [
                    { range: sel, text: "", forceMoveMarkers: true }
                ]);
            }
        }

        function internalPaste() {
            var sel = editor.getSelection();
            editor.executeEdits("internal-clipboard", [
                { range: sel, text: internalClipboard, forceMoveMarkers: true }
            ]);
        }

        // 以編輯器命令綁定攔截 Ctrl/Cmd + C / X / V（優先於 Monaco 內建行為）。
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyC, internalCopy);
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyX, internalCut);
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyV, internalPaste);

        // DOM 層級後備防線：攔截任何原生剪貼簿事件，杜絕系統剪貼簿交換。
        function blockNativeClipboard(e, kind) {
            e.preventDefault();
            e.stopPropagation();
            if (kind === "paste") {
                // 忽略系統剪貼簿內容，改插入內部暫存區，並回報一次外部貼入嘗試。
                internalPaste();
                postToHost({ type: "cheat", reason: "external-paste-blocked" });
            } else if (kind === "copy") {
                internalCopy();
            } else if (kind === "cut") {
                internalCut();
            }
        }

        document.addEventListener("paste", function (e) { blockNativeClipboard(e, "paste"); }, true);
        document.addEventListener("copy", function (e) { blockNativeClipboard(e, "copy"); }, true);
        document.addEventListener("cut", function (e) { blockNativeClipboard(e, "cut"); }, true);

        // 封鎖拖放（避免以拖曳方式帶入外部文字 / 檔案）。
        function blockDrag(e) { e.preventDefault(); e.stopPropagation(); }
        document.addEventListener("dragover", blockDrag, true);
        document.addEventListener("drop", function (e) {
            blockDrag(e);
            postToHost({ type: "cheat", reason: "external-drop-blocked" });
        }, true);

        // ================= 內容雙向同步 =================
        var syncTimer = null;
        editor.onDidChangeModelContent(function () {
            if (syncTimer) {
                clearTimeout(syncTimer);
            }
            syncTimer = setTimeout(function () {
                postToHost({ type: "codeChanged", code: editor.getValue() });
            }, 250);
        });

        // 接收來自 C# 的訊息（PostWebMessageAsString → 字串）。
        if (host) {
            host.addEventListener("message", function (ev) {
                var msg;
                try {
                    msg = JSON.parse(ev.data);
                } catch (err) {
                    return;
                }
                if (!msg || !msg.type) {
                    return;
                }
                if (msg.type === "setCode") {
                    if (editor.getValue() !== msg.code) {
                        editor.setValue(msg.code || "");
                    }
                } else if (msg.type === "setLanguage") {
                    setLanguage(msg.language);
                }
            });
        }

        // ================= 語言切換 =================
        var languageSelect = document.getElementById("language-select");

        function setLanguage(lang) {
            if (!lang) {
                return;
            }
            monaco.editor.setModelLanguage(editor.getModel(), lang);
            if (languageSelect.value !== lang) {
                languageSelect.value = lang;
            }
        }

        languageSelect.addEventListener("change", function () {
            setLanguage(languageSelect.value);
        });

        // 通知 C# 編輯器已就緒，索取初始程式碼 / 語言。
        postToHost({ type: "ready" });
    });
})();
