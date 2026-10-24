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
        registerCppCompletions();

        var editor = monaco.editor.create(document.getElementById("editor"), {
            model: null,
            theme: "vs-dark",
            fontSize: 14,
            automaticLayout: true,
            minimap: { enabled: false },
            contextmenu: false,
            scrollBeyondLastLine: false,
            renderWhitespace: "selection",
            // 補全：Tab 接受、輸入即建議、把文件內出現過的字（含自訂命名）納入建議
            tabCompletion: "on",
            suggestOnTriggerCharacters: true,
            quickSuggestions: { other: true, comments: false, strings: false },
            wordBasedSuggestions: "currentDocument",
            suggestSelection: "first"
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

        // 輸入(stdin)/輸出面板允許正常複製貼上（要貼測資）；只有程式碼編輯器做剪貼簿隔離
        function inIoPanel(e) {
            var t = e.target;
            if (!t) { return false; }
            if (t.id === "stdin" || t.id === "output") { return true; }
            return !!(t.closest && t.closest("#runner"));
        }

        function blockNativeClipboard(e, kind) {
            if (inIoPanel(e)) { return; } // 面板放行
            e.preventDefault();
            e.stopPropagation();
            // 改名輸入框：一律擋系統剪貼簿，但不可把內部剪貼簿貼進編輯器
            if (e.target && e.target.classList && e.target.classList.contains("rename-input")) {
                if (kind === "paste") { notifyCheat("external-paste-blocked"); }
                return;
            }
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

        function blockDrag(e) {
            if (inIoPanel(e)) { return; }
            e.preventDefault();
            e.stopPropagation();
        }
        document.addEventListener("dragover", blockDrag, true);
        document.addEventListener("drop", function (e) {
            if (inIoPanel(e)) { return; }
            blockDrag(e);
            notifyCheat("external-drop-blocked");
        }, true);

        // ================= 分頁管理（多檔案） =================

        // 自製語言下拉（取代原生 <select>）
        // 原生 select 在 macOS 上會開啟 NSMenu（原生 OS 視窗），
        // 導致 Avalonia 觸發 Deactivated → WarningOverlay 蓋住畫面。
        // 純 HTML div 下拉不產生任何原生視窗，徹底避免此問題。
        var langBtn  = document.getElementById('lang-btn');
        var langMenu = document.getElementById('lang-menu');
        var _curLang  = 'cpp';
        var _langLabels = { cpp: 'C++', c: 'C', h: 'Header (.h)' };

        function getLang() { return _curLang; }

        // 就地改名（取代 window.prompt：原生對話框會讓主視窗失焦而誤觸失焦警示）
        // 名稱中的 / 與 \ 會被移除，因為繳交時以「大題/檔名」組成路徑
        function inlineRename(host, current, onCommit) {
            var input = document.createElement("input");
            input.className = "rename-input";
            input.value = current;
            input.spellcheck = false;
            host.innerHTML = "";
            host.appendChild(input);
            input.focus();
            input.select();
            var done = false;
            function finish(save) {
                if (done) { return; }
                done = true;
                var v = input.value.replace(/[\/\\]/g, "").trim();
                if (save && v && v !== current) { onCommit(v); }
                renderBlocks();
                renderTabs();
                editor.focus();
            }
            input.addEventListener("keydown", function (e) {
                e.stopPropagation();
                if (e.key === "Enter") { finish(true); }
                else if (e.key === "Escape") { finish(false); }
            });
            input.addEventListener("blur", function () { finish(true); });
            input.addEventListener("click", function (e) { e.stopPropagation(); });
            input.addEventListener("dblclick", function (e) { e.stopPropagation(); });
        }

        function setLangSilent(val) {
            _curLang = val;
            langBtn.querySelector('.lang-txt').textContent = _langLabels[val] || val;
            langMenu.querySelectorAll('.lang-opt').forEach(function(el) {
                el.classList.toggle('active', el.dataset.val === val);
            });
        }

        langBtn.addEventListener('click', function(e) {
            e.stopPropagation();
            langMenu.classList.toggle('hidden');
        });
        document.addEventListener('click', function() { langMenu.classList.add('hidden'); });

        // ================= 大題（Block）管理 =================
        var blockListEl = document.getElementById('block-list');
        var newBlockBtn  = document.getElementById('new-block');
        var blocks       = [];
        var activeBlockId = null;
        var blockSeq     = 0;

        function getActiveBlock() {
            return blocks.find(function(b) { return b.id === activeBlockId; }) || null;
        }

        function renderBlocks() {
            blockListEl.innerHTML = '';
            blocks.forEach(function(blk) {
                var el = document.createElement('div');
                el.className = 'block' + (blk.id === activeBlockId ? ' active' : '');
                // click/dblclick 掛在 el 上，因為 .block-name 有 pointer-events:none
                el.addEventListener('click', function() { activateBlock(blk.id); });
                el.dataset.blockId = blk.id;
                el.addEventListener('dblclick', function(e) { e.stopPropagation(); renameBlock(blk); });
                var nameSpan = document.createElement('span');
                nameSpan.className = 'block-name';
                nameSpan.textContent = blk.name;
                el.appendChild(nameSpan);
                if (blocks.length > 1) {
                    var x = document.createElement('span');
                    x.className = 'block-close';
                    x.textContent = '×';
                    x.title = '刪除此大題（含所有分頁）';
                    x.addEventListener('click', function(e) { e.stopPropagation(); deleteBlock(blk.id); });
                    el.appendChild(x);
                }
                blockListEl.appendChild(el);
            });
        }

        function activateBlock(id) {
            if (activeBlockId === id) return;
            activeBlockId = id;
            renderBlocks();
            var blkTabs = tabs.filter(function(t) { return t.blockId === id; });
            var blk = getActiveBlock();
            if (blkTabs.length === 0) {
                createTab(null, '', 'cpp');
            } else {
                var prev = blk && blk.activeTabId && blkTabs.find(function(t) { return t.id === blk.activeTabId; });
                activateTab(prev ? prev.id : blkTabs[0].id);
            }
        }

        function deleteBlock(id) {
            if (blocks.length <= 1) return;
            tabs.filter(function(t) { return t.blockId === id; }).forEach(function(t) { t.model.dispose(); });
            tabs = tabs.filter(function(t) { return t.blockId !== id; });
            var idx = blocks.findIndex(function(b) { return b.id === id; });
            blocks.splice(idx, 1);
            if (activeBlockId === id) {
                activeBlockId = blocks[Math.max(0, idx - 1)].id;
                var blkTabs = tabs.filter(function(t) { return t.blockId === activeBlockId; });
                if (blkTabs.length === 0) { createTab(null, '', 'cpp'); }
                else { activateTab(blkTabs[0].id); }
            }
            renderBlocks();
            renderTabs();
        }

        // 雙擊前的兩次 click 會重繪列表，所以改名時才去找「目前畫面上」的元素
        function renameBlock(blk) {
            var el = blockListEl.querySelector('.block[data-block-id="' + blk.id + '"]');
            if (!el) { return; }
            inlineRename(el, blk.name, function (name) { blk.name = name; });
        }

        newBlockBtn.addEventListener('click', function() {
            blockSeq++;
            var blk = { id: 'blk' + blockSeq, name: 'Q' + blockSeq, activeTabId: null };
            blocks.push(blk);
            activeBlockId = blk.id;
            renderBlocks();
            createTab(null, '', 'cpp');
        });

        // ================= 分頁管理（多檔案） =================
        var tabsEl    = document.getElementById("tabs");
        var newTabBtn = document.getElementById("new-tab");

        var tabs     = [];
        var activeId = null;
        var tabSeq   = 0;

        function extForLang(lang) { return lang === "c" ? ".c" : lang === "h" ? ".h" : ".cpp"; }
        function langForName(name) { return /\.c$/i.test(name) ? "c" : /\.h$/i.test(name) ? "h" : "cpp"; }
        // Monaco 沒有 "h" 語言 ID，統一用 "c" 做語法標色
        function monacoLang(lang) { return lang === "h" ? "c" : lang; }
        function activeTab() { return tabs.find(function (t) { return t.id === activeId; }); }

        function renderTabs() {
            tabsEl.innerHTML = "";
            tabs.filter(function(tab) { return tab.blockId === activeBlockId; }).forEach(function (tab) {
                var el = document.createElement("div");
                el.className = "tab" + (tab.id === activeId ? " active" : "");

                var name = document.createElement("span");
                name.className = "tab-name";
                name.textContent = tab.name;
                name.title = "點擊切換、雙擊改名";
                name.addEventListener("click", function () { activateTab(tab.id); });
                name.dataset.tabId = tab.id;
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
            var blk = getActiveBlock();
            if (blk) { blk.activeTabId = id; }
            editor.setModel(tab.model);
            if (getLang() !== tab.lang) { setLangSilent(tab.lang); }
            renderTabs();
            editor.focus();
        }

        function createTab(name, content, lang) {
            tabSeq++;
            lang = lang || "cpp";
            var id = "t" + tabSeq;
            var tab = {
                id: id,
                blockId: activeBlockId,
                name: name || ("file" + tabSeq + extForLang(lang)),
                lang: lang,
                model: monaco.editor.createModel(content || "", monacoLang(lang))
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
            var blkTabs = tabs.filter(function(t) { return t.blockId === activeBlockId; });
            if (blkTabs.length === 0) { createTab(null, "", getLang()); return; }
            if (activeId === id) { activateTab(blkTabs[Math.max(0, blkTabs.length - 1)].id); }
            else { renderTabs(); }
        }

        function renameTab(tab) {
            var nameEl = tabsEl.querySelector('.tab-name[data-tab-id="' + tab.id + '"]');
            if (!nameEl) { return; }
            inlineRename(nameEl, tab.name, function (name) {
                tab.name = name;
                tab.lang = langForName(name);
                monaco.editor.setModelLanguage(tab.model, monacoLang(tab.lang));
                if (tab.id === activeId && getLang() !== tab.lang) { setLangSilent(tab.lang); }
            });
        }

        newTabBtn.addEventListener("click", function () { createTab(null, "", getLang()); });

        // 語言選項點擊（相當於原 languageSelect change 事件）
        langMenu.querySelectorAll('.lang-opt').forEach(function(el) {
            el.addEventListener('click', function(e) {
                e.stopPropagation();
                langMenu.classList.add('hidden');
                setLangSilent(el.dataset.val);
                var tab = activeTab();
                if (!tab) { return; }
                tab.lang = getLang();
                tab.name = tab.name.replace(/\.(cpp|c|h)$/i, extForLang(tab.lang));
                monaco.editor.setModelLanguage(tab.model, monacoLang(tab.lang));
                renderTabs();
            });
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
            tab.name = tab.name.replace(/\.(cpp|c|h)$/i, extForLang(lang));
            monaco.editor.setModelLanguage(tab.model, monacoLang(lang));
            if (getLang() !== lang) { setLangSilent(lang); }
            renderTabs();
        };

        // 供 C# 端呼叫：把判題頁複製的文字寫入內部剪貼簿（允許貼上，仍擋外部系統剪貼簿）
        window.__setClipboard = function (text) {
            internalClipboard = text || "";
        };

        // 供 C# 端呼叫：收集所有大題的分頁（結束考試時繳交 / 下載）
        // 格式：[{name: "Q1/main.cpp", content, lang}, ...]，後端依路徑建子資料夾
        window.__collectFiles = function () {
            var result = [];
            blocks.forEach(function(blk) {
                tabs.filter(function(t) { return t.blockId === blk.id; }).forEach(function(t) {
                    result.push({ name: blk.name + '/' + t.name, content: t.model.getValue(), lang: t.lang });
                });
            });
            return JSON.stringify(result);
        };

        // 建立第一個大題與分頁
        blockSeq++;
        var firstBlk = { id: 'blk' + blockSeq, name: 'Q' + blockSeq, activeTabId: null };
        blocks.push(firstBlk);
        activeBlockId = firstBlk.id;
        renderBlocks();
        createTab(null, "", "cpp");

        // ================= 編譯 / 執行 =================
        var runBtn = document.getElementById("run-btn");
        var stdinEl = document.getElementById("stdin");
        var outputEl = document.getElementById("output");
        var runStatus = document.getElementById("run-status");

        var stdinActions = document.getElementById("stdin-actions");
        var sendBtn = document.getElementById("send-btn");
        var eofBtn = document.getElementById("eof-btn");

        function setOutput(text, cls) {
            outputEl.textContent = text || "";
            outputEl.className = cls || "";
        }

        // 輸出上限（字元）；超過時從頭丟棄舊內容，避免 DOM 過大拖慢頁面
        var OUTPUT_DOM_LIMIT = 2000000;
        var outputLen = 0;

        function appendOutput(text, cls) {
            if (!text) { return; }
            // 若使用者正在看底部，新輸出進來就跟著捲動
            var atBottom = outputEl.scrollTop + outputEl.clientHeight >= outputEl.scrollHeight - 4;
            var span = document.createElement("span");
            if (cls) { span.className = cls; }
            span.textContent = text;
            outputEl.appendChild(span);
            outputLen += text.length;
            while (outputLen > OUTPUT_DOM_LIMIT && outputEl.firstChild && outputEl.firstChild !== span) {
                outputLen -= (outputEl.firstChild.textContent || "").length;
                outputEl.removeChild(outputEl.firstChild);
            }
            if (atBottom) { outputEl.scrollTop = outputEl.scrollHeight; }
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
            // phase === run：stdout / stderr 已由 __runOutput 即時顯示，這裡只補結尾資訊
            if (outputLen === 0) { appendOutput("(無輸出)", "out-info"); }
            var text = outputEl.textContent;
            var footer = (text && text.slice(-1) !== "\n" ? "\n" : "") + "──────────\n";
            if (r.stopped || r.timedOut) {
                footer += "⏱ " + (r.message || "執行逾時");
            } else {
                footer += "結束代碼 " + r.exitCode + "，耗時 " + r.timeMs + " ms";
            }
            appendOutput(footer, r.ok ? "out-info" : "out-err");
        }

        var running = false;
        var interactive = false;
        var runTimeout = null;

        function setRunning(on, isInteractive) {
            running = on;
            interactive = on && isInteractive;
            runBtn.textContent = on ? "■ 停止" : "▶ 執行";
            runBtn.title = on ? "停止執行" : "Ctrl+Enter";
            runBtn.classList.toggle("stop", on);
            stdinActions.classList.toggle("hidden", !interactive);
            stdinEl.classList.toggle("waiting", interactive);
        }

        function finishRun() {
            setRunning(false, false);
            runStatus.textContent = "";
            if (runTimeout) { clearTimeout(runTimeout); runTimeout = null; }
        }

        function runCode() {
            if (running) { return; }
            var b = bridge();
            if (!b || !b.runCode) { setOutput("橋接未就緒，無法執行。", "error"); return; }

            // 執行前輸入欄有內容 → 批次（一次送入並 EOF）；留空 → 互動（執行中再輸入）
            var stdinText = stdinEl.value || "";
            var isInteractive = stdinText.trim() === "";
            if (isInteractive) { stdinText = ""; stdinEl.value = ""; }

            setRunning(true, isInteractive);
            runStatus.textContent = isInteractive
                ? "編譯執行中…（可在輸入欄輸入，Enter 送出）"
                : "編譯執行中…（已送出輸入欄的測資）";
            setOutput("");
            outputLen = 0;

            // fire-and-forget：輸出由 __runOutput 即時推回，結束時呼叫 __runResult
            // 同時傳送所有分頁，讓 .h 等輔助檔案能被 #include
            try {
                // 只編譯當前大題的分頁，避免其他大題的 main() 造成 multiple definition
                var blkFiles = tabs.filter(function(t) { return t.blockId === activeBlockId; });
                var filesJson = JSON.stringify(blkFiles.map(function(t) {
                    return { name: t.name, content: t.model.getValue(), lang: t.lang };
                }));
                // 目前分頁是 .h 時無法單獨編譯，改以同大題第一個 .c/.cpp 分頁為主檔
                var mainTab = activeTab();
                if (mainTab && mainTab.lang === "h") {
                    mainTab = blkFiles.find(function(t) { return t.lang !== "h"; }) || mainTab;
                }
                b.runCode(mainTab.lang, mainTab.model.getValue(), stdinText, filesJson, mainTab.name, isInteractive);
            } catch (e) {
                finishRun();
                setOutput("執行失敗：" + e, "error");
                return;
            }

            // 安全逾時：萬一未收到回傳也解鎖（互動模式 C# 端上限 10 分鐘）
            runTimeout = setTimeout(function () {
                if (running) { finishRun(); appendOutput("\n執行逾時，未收到結果。", "out-err"); }
            }, isInteractive ? 660000 : 60000);
        }

        function stopRun() {
            var b = bridge();
            if (running && b && b.stopRun) { b.stopRun(); }
        }

        // 互動模式：送出輸入欄內容（補上換行），並回顯在輸出區
        function sendInput() {
            var b = bridge();
            if (!interactive || !b || !b.sendInput) { return; }
            var text = stdinEl.value;
            if (text.slice(-1) !== "\n") { text += "\n"; }
            b.sendInput(text);
            appendOutput(text, "out-echo");
            stdinEl.value = "";
            stdinEl.focus();
        }

        function sendEof() {
            var b = bridge();
            if (!interactive || !b || !b.closeInput) { return; }
            if (stdinEl.value) { sendInput(); }
            b.closeInput();
            appendOutput("[EOF]\n", "out-info");
            interactive = false;
            stdinActions.classList.add("hidden");
            stdinEl.classList.remove("waiting");
        }

        // 供 C# 端呼叫：執行中的即時輸出
        window.__runOutput = function (stream, text) {
            if (!running) { return; }
            appendOutput(text, stream === "stderr" ? "out-err" : stream === "info" ? "out-info" : null);
        };

        // 供 C# 端呼叫：接收編譯 / 執行結果
        window.__runResult = function (json) {
            var r;
            try { r = JSON.parse(json); } catch (e) { r = null; }
            finishRun();
            renderResult(r);
        };

        runBtn.addEventListener("click", function () { if (running) { stopRun(); } else { runCode(); } });
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, runCode);
        sendBtn.addEventListener("click", sendInput);
        eofBtn.addEventListener("click", sendEof);
        // 互動模式：Enter 送出、Shift+Enter 換行；非執行中 Enter 照常換行（貼多行測資）
        stdinEl.addEventListener("keydown", function (e) {
            if (interactive && e.key === "Enter" && !e.shiftKey && !e.isComposing) {
                e.preventDefault();
                sendInput();
            }
        });

        // ================= 下載（存至桌面） =================
        var dlBtn = document.getElementById("dl-btn");
        dlBtn.addEventListener("click", function () {
            var b = bridge();
            if (!b || dlBtn.disabled) { return; }
            dlBtn.disabled = true;
            dlBtn.textContent = "儲存中…";
            b.saveFiles(window.__collectFiles());   // 所有大題一起下載
        });

        window.__saveResult = function (msg) {
            dlBtn.disabled = false;
            dlBtn.textContent = "⬇ 下載";
            runStatus.textContent = msg;
            setTimeout(function () {
                if (runStatus.textContent === msg) { runStatus.textContent = ""; }
            }, 4000);
        };

        // ================= 面板可拖曳調整大小 =================
        setupResizers();

        function setupResizers() {
            var runner = document.getElementById("runner");
            var stdinCol = document.getElementById("stdin-col");

            // 垂直：編輯器 ↔ 輸出區（調整 runner 高度）
            makeDrag(document.getElementById("v-splitter"), function (e, start) {
                var dy = start.y - e.clientY; // 往上拖 → 輸出區變高
                var h = clamp(start.h + dy, 80, window.innerHeight - 160);
                runner.style.flex = "0 0 " + h + "px";
            }, function () {
                return { y: 0, h: runner.getBoundingClientRect().height };
            });

            // 水平：輸入 ↔ 輸出（調整 stdin 欄寬度）
            makeDrag(document.getElementById("h-splitter"), function (e, start) {
                var dx = e.clientX - start.x;
                var w = clamp(start.w + dx, 80, runner.getBoundingClientRect().width - 120);
                stdinCol.style.flex = "0 0 " + w + "px";
            }, function () {
                return { x: 0, w: stdinCol.getBoundingClientRect().width };
            });
        }

        function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

        function makeDrag(handle, onMove, getStart) {
            if (!handle) { return; }
            handle.addEventListener("pointerdown", function (e) {
                e.preventDefault();
                var start = getStart();
                start.x = e.clientX;
                start.y = e.clientY;
                handle.setPointerCapture(e.pointerId);
                function move(ev) { onMove(ev, start); }
                function up() {
                    try { handle.releasePointerCapture(e.pointerId); } catch (err) {}
                    document.removeEventListener("pointermove", move);
                    document.removeEventListener("pointerup", up);
                }
                document.addEventListener("pointermove", move);
                document.addEventListener("pointerup", up);
            });
        }

        // 通知 C# 編輯器已就緒
        notifyReady();
    });

    // ================= C / C++ 輕量語法補全 =================
    function registerCppCompletions() {
        if (typeof monaco === "undefined" || registerCppCompletions._done) { return; }
        registerCppCompletions._done = true;

        var K = monaco.languages.CompletionItemKind;
        var SNIPPET = monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet;

        var cKeywords = ["int", "char", "short", "long", "float", "double", "void", "unsigned",
            "signed", "const", "static", "extern", "struct", "union", "enum", "typedef", "sizeof",
            "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue",
            "return", "goto", "NULL"];
        var cppKeywords = ["bool", "true", "false", "class", "struct", "public", "private", "protected",
            "namespace", "using", "template", "typename", "this", "new", "delete", "virtual", "override",
            "nullptr", "auto", "try", "catch", "throw", "operator", "friend", "inline", "explicit",
            "constexpr", "static_cast", "dynamic_cast", "const_cast", "reinterpret_cast"];
        var cLib = ["printf", "scanf", "puts", "gets", "getchar", "putchar", "malloc", "calloc",
            "realloc", "free", "memset", "memcpy", "memmove", "strlen", "strcmp", "strncmp", "strcpy",
            "strncpy", "strcat", "strstr", "atoi", "atof", "abs", "labs", "pow", "sqrt", "ceil",
            "floor", "fabs", "rand", "srand", "qsort", "fopen", "fclose", "fscanf", "fprintf", "EOF"];
        var cppLib = ["std", "cout", "cin", "cerr", "endl", "string", "vector", "map", "unordered_map",
            "set", "unordered_set", "pair", "queue", "stack", "priority_queue", "sort", "reverse",
            "max", "min", "swap", "make_pair", "to_string", "stoi", "stod", "push_back", "pop_back",
            "size", "empty", "begin", "end", "front", "back", "find", "insert", "erase", "count",
            "substr", "length", "first", "second"];
        var headers = ["#include <iostream>", "#include <vector>", "#include <string>",
            "#include <algorithm>", "#include <map>", "#include <set>", "#include <queue>",
            "#include <cmath>", "#include <cstdio>", "#include <cstring>", "#include <cstdlib>",
            "#include <stdio.h>", "#include <stdlib.h>", "#include <string.h>", "#include <math.h>"];

        function items(list, kind, range) {
            return list.map(function (w) {
                return { label: w, kind: kind, insertText: w, range: range };
            });
        }

        function snippets(range, isCpp) {
            var s = [
                { label: "for", detail: "for 迴圈", insertText: "for (int ${1:i} = 0; ${1:i} < ${2:n}; ++${1:i}) {\n\t$0\n}" },
                { label: "while", detail: "while 迴圈", insertText: "while (${1:cond}) {\n\t$0\n}" },
                { label: "if", detail: "if", insertText: "if (${1:cond}) {\n\t$0\n}" }
            ];
            if (isCpp) {
                s.push({ label: "main", detail: "C++ main", insertText: "int main() {\n\t$0\n\treturn 0;\n}" });
                s.push({ label: "cout", detail: "輸出", insertText: "std::cout << ${1:x} << std::endl;$0" });
                s.push({ label: "cin", detail: "輸入", insertText: "std::cin >> ${1:x};$0" });
                s.push({ label: "class", detail: "類別", insertText: "class ${1:Name} {\npublic:\n\t$0\n};" });
            } else {
                s.push({ label: "main", detail: "C main", insertText: "int main() {\n\t$0\n\treturn 0;\n}" });
                s.push({ label: "printf", detail: "輸出", insertText: "printf(\"${1:%d}\\n\", ${2:x});$0" });
                s.push({ label: "scanf", detail: "輸入", insertText: "scanf(\"${1:%d}\", &${2:x});$0" });
            }
            return s.map(function (x) {
                return {
                    label: x.label, kind: monaco.languages.CompletionItemKind.Snippet,
                    detail: x.detail, insertText: x.insertText,
                    insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                    range: range
                };
            });
        }

        function provider(isCpp) {
            return {
                provideCompletionItems: function (model, position) {
                    var word = model.getWordUntilPosition(position);
                    var range = {
                        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
                        startColumn: word.startColumn, endColumn: word.endColumn
                    };
                    var sug = []
                        .concat(items(cKeywords, K.Keyword, range))
                        .concat(items(cLib, K.Function, range))
                        .concat(items(headers, K.Module, range))
                        .concat(snippets(range, isCpp));
                    if (isCpp) {
                        sug = sug
                            .concat(items(cppKeywords, K.Keyword, range))
                            .concat(items(cppLib, K.Function, range));
                    }
                    return { suggestions: sug };
                }
            };
        }

        monaco.languages.registerCompletionItemProvider("c", provider(false));
        monaco.languages.registerCompletionItemProvider("cpp", provider(true));
        // .h 檔用 "c" language ID，但以防萬一也掛一個
        try { monaco.languages.registerCompletionItemProvider("h", provider(false)); } catch(e) {}
    }
})();
