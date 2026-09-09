(function () {
    "use strict";

    // ----- 與 C# 溝通的橋接（CefGlue RegisterJavascriptObject → window.editorBridge） -----
    // editorBridge 的方法為非同步（回傳 Promise），此處僅做單向通知，忽略回傳值。
    function bridge() {
        return window.editorBridge || null;
    }

    function notifyReady() {
        var b = bridge();
        if (b && b.ready) { b.ready(); }
    }

    function notifyCodeChanged(code) {
        var b = bridge();
        if (b && b.codeChanged) { b.codeChanged(code); }
    }

    function notifyCheat(reason) {
        var b = bridge();
        if (b && b.notifyCheat) { b.notifyCheat(reason); }
    }

    // ----- Monaco 載入器保護：本地資源缺失則顯示提示 -----
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
            contextmenu: false,
            scrollBeyondLastLine: false,
            renderWhitespace: "selection"
        });

        // ================= 剪貼簿隔離 =================
        // 僅允許編輯器內部暫存區的複製/剪下/貼上，封鎖系統剪貼簿的外部資料。
        var internalClipboard = "";

        function getSelectedText() {
            var sel = editor.getSelection();
            if (!sel || sel.isEmpty()) { return ""; }
            return editor.getModel().getValueInRange(sel);
        }

        function internalCopy() {
            var text = getSelectedText();
            if (text) { internalClipboard = text; }
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

        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyC, internalCopy);
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyX, internalCut);
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyV, internalPaste);

        function blockNativeClipboard(e, kind) {
            e.preventDefault();
            e.stopPropagation();
            if (kind === "paste") {
                internalPaste();
                notifyCheat("external-paste-blocked");
            } else if (kind === "copy") {
                internalCopy();
            } else if (kind === "cut") {
                internalCut();
            }
        }

        document.addEventListener("paste", function (e) { blockNativeClipboard(e, "paste"); }, true);
        document.addEventListener("copy", function (e) { blockNativeClipboard(e, "copy"); }, true);
        document.addEventListener("cut", function (e) { blockNativeClipboard(e, "cut"); }, true);

        function blockDrag(e) { e.preventDefault(); e.stopPropagation(); }
        document.addEventListener("dragover", blockDrag, true);
        document.addEventListener("drop", function (e) {
            blockDrag(e);
            notifyCheat("external-drop-blocked");
        }, true);

        // ================= 語言切換 =================
        var languageSelect = document.getElementById("language-select");
        languageSelect.addEventListener("change", function () {
            monaco.editor.setModelLanguage(editor.getModel(), languageSelect.value);
        });

        // ================= 內容同步 =================
        var syncTimer = null;
        editor.onDidChangeModelContent(function () {
            if (syncTimer) { clearTimeout(syncTimer); }
            syncTimer = setTimeout(function () {
                notifyCodeChanged(editor.getValue());
            }, 250);
        });

        // 供 C# 端呼叫（ExecuteJavaScript）以推送程式碼 / 語言
        window.__setCode = function (code) {
            if (editor.getValue() !== code) {
                editor.setValue(code || "");
            }
        };

        window.__setLanguage = function (lang) {
            if (!lang) { return; }
            monaco.editor.setModelLanguage(editor.getModel(), lang);
            if (languageSelect.value !== lang) {
                languageSelect.value = lang;
            }
        };

        // ================= 編譯 / 執行 =================
        var runBtn = document.getElementById("run-btn");
        var stdinEl = document.getElementById("stdin");
        var outputEl = document.getElementById("output");
        var runStatus = document.getElementById("run-status");

        function setOutput(text, cls) {
            outputEl.textContent = text || "";
            outputEl.className = cls || "";
        }

        function renderResult(r) {
            if (!r) { setOutput("（無回應）", "error"); return; }

            if (r.phase === "error") {
                setOutput(r.message || "執行器錯誤", "error");
                return;
            }
            if (r.phase === "compile") {
                setOutput("編譯錯誤：\n" + (r.stderr || r.message || ""), "error");
                return;
            }
            // phase === run
            var parts = [];
            if (r.stdout) { parts.push(r.stdout.replace(/\n$/, "")); }
            if (r.stderr) { parts.push("[stderr]\n" + r.stderr.replace(/\n$/, "")); }
            var footer = "\n──────────\n";
            if (r.timedOut) {
                footer += "⏱ " + (r.message || "執行逾時");
            } else {
                footer += "結束代碼 " + r.exitCode + "，耗時 " + r.timeMs + " ms";
            }
            setOutput((parts.join("\n") || "(無輸出)") + footer, r.ok ? "ok" : "error");
        }

        var running = false;
        function runCode() {
            if (running) { return; }
            var b = bridge();
            if (!b || !b.runCode) { setOutput("橋接未就緒，無法執行。", "error"); return; }

            running = true;
            runBtn.disabled = true;
            runStatus.textContent = "編譯執行中…";
            setOutput("");

            var lang = languageSelect.value;
            var code = editor.getValue();
            var stdin = stdinEl.value || "";

            Promise.resolve(b.runCode(lang, code, stdin))
                .then(function (json) {
                    var r;
                    try { r = JSON.parse(json); } catch (e) { r = null; }
                    renderResult(r);
                })
                .catch(function (e) {
                    setOutput("執行失敗：" + e, "error");
                })
                .then(function () {
                    running = false;
                    runBtn.disabled = false;
                    runStatus.textContent = "";
                });
        }

        runBtn.addEventListener("click", runCode);
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, runCode);

        // 通知 C# 編輯器已就緒
        notifyReady();
    });
})();
