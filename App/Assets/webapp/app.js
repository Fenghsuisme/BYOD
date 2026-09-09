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
            model: null,
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

        // ================= 分頁管理（多檔案） =================
        var languageSelect = document.getElementById("language-select");
        var tabsEl = document.getElementById("tabs");
        var newTabBtn = document.getElementById("new-tab");

        var tabs = [];
        var activeId = null;
        var tabSeq = 0;

        function extForLang(lang) { return lang === "c" ? ".c" : ".cpp"; }
        function langForName(name) { return /\.c$/i.test(name) ? "c" : "cpp"; }
        function activeTab() { return tabs.find(function (t) { return t.id === activeId; }); }

        function renderTabs() {
            tabsEl.innerHTML = "";
            tabs.forEach(function (tab) {
                var el = document.createElement("div");
                el.className = "tab" + (tab.id === activeId ? " active" : "");

                var name = document.createElement("span");
                name.className = "tab-name";
                name.textContent = tab.name;
                name.title = "點擊切換、雙擊改名";
                name.addEventListener("click", function () { activateTab(tab.id); });
                name.addEventListener("dblclick", function () { renameTab(tab); });

                var close = document.createElement("span");
                close.className = "tab-close";
                close.textContent = "×";
                close.title = "關閉";
                close.addEventListener("click", function (e) { e.stopPropagation(); closeTab(tab.id); });

                el.appendChild(name);
                el.appendChild(close);
                tabsEl.appendChild(el);
            });
        }

        function activateTab(id) {
            var tab = tabs.find(function (t) { return t.id === id; });
            if (!tab) { return; }
            activeId = id;
            editor.setModel(tab.model);
            if (languageSelect.value !== tab.lang) { languageSelect.value = tab.lang; }
            renderTabs();
            editor.focus();
        }

        function createTab(name, content, lang) {
            tabSeq++;
            lang = lang || "cpp";
            var id = "t" + tabSeq;
            var tab = {
                id: id,
                name: name || ("file" + tabSeq + extForLang(lang)),
                lang: lang,
                model: monaco.editor.createModel(content || "", lang)
            };
            tabs.push(tab);
            activateTab(id);
            return tab;
        }

        function closeTab(id) {
            var idx = tabs.findIndex(function (t) { return t.id === id; });
            if (idx < 0) { return; }
            tabs[idx].model.dispose();
            tabs.splice(idx, 1);
            if (tabs.length === 0) { createTab(null, "", languageSelect.value); return; }
            if (activeId === id) { activateTab(tabs[Math.max(0, idx - 1)].id); }
            else { renderTabs(); }
        }

        function renameTab(tab) {
            var name = window.prompt ? window.prompt("檔名：", tab.name) : null;
            if (!name) { return; }
            tab.name = name;
            tab.lang = langForName(name);
            monaco.editor.setModelLanguage(tab.model, tab.lang);
            if (tab.id === activeId && languageSelect.value !== tab.lang) { languageSelect.value = tab.lang; }
            renderTabs();
        }

        newTabBtn.addEventListener("click", function () { createTab(null, "", languageSelect.value); });

        languageSelect.addEventListener("change", function () {
            var tab = activeTab();
            if (!tab) { return; }
            tab.lang = languageSelect.value;
            tab.name = tab.name.replace(/\.(c|cpp)$/i, extForLang(tab.lang));
            monaco.editor.setModelLanguage(tab.model, tab.lang);
            renderTabs();
        });

        // ================= 內容同步 =================
        var syncTimer = null;
        editor.onDidChangeModelContent(function () {
            if (syncTimer) { clearTimeout(syncTimer); }
            syncTimer = setTimeout(function () {
                notifyCodeChanged(editor.getValue());
            }, 250);
        });

        // 供 C# 端呼叫（ExecuteJavaScript）以推送程式碼 / 語言（作用於目前分頁）
        window.__setCode = function (code) {
            if (activeTab() && editor.getValue() !== code) {
                editor.setValue(code || "");
            }
        };

        window.__setLanguage = function (lang) {
            var tab = activeTab();
            if (!lang || !tab) { return; }
            tab.lang = lang;
            tab.name = tab.name.replace(/\.(c|cpp)$/i, extForLang(lang));
            monaco.editor.setModelLanguage(tab.model, lang);
            if (languageSelect.value !== lang) { languageSelect.value = lang; }
            renderTabs();
        };

        // 建立第一個分頁
        createTab(null, "", "cpp");

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
