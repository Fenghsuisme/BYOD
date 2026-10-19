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
        var languageSelect = document.getElementById("language-select");
        var tabsEl = document.getElementById("tabs");
        var newTabBtn = document.getElementById("new-tab");

        var tabs = [];
        var activeId = null;
        var tabSeq = 0;

        function extForLang(lang) { return lang === "c" ? ".c" : lang === "h" ? ".h" : ".cpp"; }
        function langForName(name) { return /\.c$/i.test(name) ? "c" : /\.h$/i.test(name) ? "h" : "cpp"; }
        // Monaco 沒有 "h" 語言 ID，統一用 "c" 做語法標色
        function monacoLang(lang) { return lang === "h" ? "c" : lang; }
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
            if (tabs.length === 0) { createTab(null, "", languageSelect.value); return; }
            if (activeId === id) { activateTab(tabs[Math.max(0, idx - 1)].id); }
            else { renderTabs(); }
        }

        function renameTab(tab) {
            var name = window.prompt ? window.prompt("檔名：", tab.name) : null;
            if (!name) { return; }
            tab.name = name;
            tab.lang = langForName(name);
            monaco.editor.setModelLanguage(tab.model, monacoLang(tab.lang));
            if (tab.id === activeId && languageSelect.value !== tab.lang) { languageSelect.value = tab.lang; }
            renderTabs();
        }

        newTabBtn.addEventListener("click", function () { createTab(null, "", languageSelect.value); });

        languageSelect.addEventListener("change", function () {
            var tab = activeTab();
            if (!tab) { return; }
            tab.lang = languageSelect.value;
            tab.name = tab.name.replace(/\.(cpp|c|h)$/i, extForLang(tab.lang));
            monaco.editor.setModelLanguage(tab.model, monacoLang(tab.lang));
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
            tab.name = tab.name.replace(/\.(cpp|c|h)$/i, extForLang(lang));
            monaco.editor.setModelLanguage(tab.model, monacoLang(lang));
            if (languageSelect.value !== lang) { languageSelect.value = lang; }
            renderTabs();
        };

        // 供 C# 端呼叫：把判題頁複製的文字寫入內部剪貼簿（允許貼上，仍擋外部系統剪貼簿）
        window.__setClipboard = function (text) {
            internalClipboard = text || "";
        };

        // 供 C# 端呼叫：收集所有分頁內容（結束考試時繳交）
        window.__collectFiles = function () {
            return JSON.stringify(tabs.map(function (t) {
                return { name: t.name, content: t.model.getValue(), lang: t.lang };
            }));
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
        var runTimeout = null;

        function finishRun() {
            running = false;
            runBtn.disabled = false;
            runStatus.textContent = "";
            if (runTimeout) { clearTimeout(runTimeout); runTimeout = null; }
        }

        function runCode() {
            if (running) { return; }
            var b = bridge();
            if (!b || !b.runCode) { setOutput("橋接未就緒，無法執行。", "error"); return; }

            running = true;
            runBtn.disabled = true;
            runStatus.textContent = "編譯執行中…";
            setOutput("");

            // fire-and-forget：結果由 C# 呼叫 window.__runResult 推回
            // 同時傳送所有分頁，讓 .h 等輔助檔案能被 #include
            try {
                var filesJson = window.__collectFiles ? window.__collectFiles() : "[]";
                b.runCode(languageSelect.value, editor.getValue(), stdinEl.value || "", filesJson);
            } catch (e) {
                finishRun();
                setOutput("執行失敗：" + e, "error");
                return;
            }

            // 安全逾時：萬一未收到回傳也解鎖（放寬到 60s）
            runTimeout = setTimeout(function () {
                if (running) { finishRun(); setOutput("執行逾時，未收到結果。", "error"); }
            }, 60000);
        }

        // 供 C# 端呼叫：接收編譯 / 執行結果
        window.__runResult = function (json) {
            var r;
            try { r = JSON.parse(json); } catch (e) { r = null; }
            finishRun();
            renderResult(r);
        };

        runBtn.addEventListener("click", runCode);
        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, runCode);

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

        // ================= 參考面板 =================
        setupRefPanels();

        // 通知 C# 編輯器已就緒
        notifyReady();
    });

    // ================= 參考面板資料與邏輯 =================
    // panel div 在此動態建立（不放在 HTML 靜態結構中，避免干擾 body flex 佈局）
    function makePanelEl(id, title, searchId, bodyId) {
        var panel = document.createElement("div");
        panel.id = id;
        panel.className = "ref-panel hidden";

        var hdr = document.createElement("div");
        hdr.className = "ref-panel-header";

        var titleEl = document.createElement("span");
        titleEl.textContent = title;

        var inp = document.createElement("input");
        inp.id = searchId;
        inp.type = "text";
        inp.placeholder = "搜尋…";
        inp.autocomplete = "off";

        var closeBtn = document.createElement("button");
        closeBtn.className = "ref-close";
        closeBtn.textContent = "✕";
        closeBtn.addEventListener("click", function () { panel.classList.add("hidden"); });

        hdr.appendChild(titleEl);
        hdr.appendChild(inp);
        hdr.appendChild(closeBtn);

        var body = document.createElement("div");
        body.id = bodyId;
        body.className = "ref-panel-body";

        panel.appendChild(hdr);
        panel.appendChild(body);
        document.body.appendChild(panel);
        return panel;
    }

    function setupRefPanels() {
        var LIB_DATA = [
            { cat: "輸出 / 輸入 <stdio.h>", items: [
                ["printf(fmt, ...)", "格式化輸出到 stdout"],
                ["scanf(fmt, ...)", "從 stdin 讀入（需傳指標 &x）"],
                ["puts(s)", "輸出字串並換行"],
                ["getchar()", "讀一個字元"],
                ["putchar(c)", "輸出一個字元"],
                ["fopen(path, mode)", "開啟檔案，mode: r/w/a"],
                ["fclose(fp)", "關閉檔案"],
                ["fscanf(fp, fmt, ...)", "從檔案讀入"],
                ["fprintf(fp, fmt, ...)", "格式化輸出到檔案"],
                ["fgets(buf, n, fp)", "讀一行（含 '\\n'），安全版 gets"],
                ["sprintf(buf, fmt, ...)", "格式化輸出到字串"],
                ["sscanf(buf, fmt, ...)", "從字串讀入"]
            ]},
            { cat: "字串 <string.h>", items: [
                ["strlen(s)", "字串長度（不含 '\\0'）"],
                ["strcmp(a, b)", "比較字串，<0 / 0 / >0"],
                ["strncmp(a, b, n)", "比較前 n 個字元"],
                ["strcpy(dst, src)", "複製字串"],
                ["strncpy(dst, src, n)", "最多複製 n 個字元"],
                ["strcat(dst, src)", "串接字串"],
                ["strstr(hay, needle)", "在 hay 中找 needle，回傳指標"],
                ["strchr(s, c)", "找第一個字元 c"],
                ["memset(ptr, val, n)", "設定 n 個 byte 為 val（常用清零）"],
                ["memcpy(dst, src, n)", "複製 n 個 byte"],
                ["memmove(dst, src, n)", "可重疊的 memcpy"]
            ]},
            { cat: "記憶體 <stdlib.h>", items: [
                ["malloc(size)", "配置 size bytes，回傳 void*"],
                ["calloc(n, size)", "配置 n*size bytes 並清零"],
                ["realloc(ptr, size)", "調整配置大小"],
                ["free(ptr)", "釋放記憶體"],
                ["atoi(s)", "字串轉 int"],
                ["atof(s)", "字串轉 double"],
                ["abs(n)", "整數絕對值"],
                ["rand()", "隨機整數 [0, RAND_MAX]"],
                ["srand(seed)", "設定亂數種子"],
                ["qsort(arr, n, sz, cmp)", "快速排序，cmp(a,b): <0升冪"],
                ["exit(code)", "結束程式"]
            ]},
            { cat: "數學 <math.h>", items: [
                ["sqrt(x)", "平方根"],
                ["pow(x, y)", "x 的 y 次方"],
                ["fabs(x)", "浮點絕對值"],
                ["ceil(x)", "往上取整"],
                ["floor(x)", "往下取整"],
                ["round(x)", "四捨五入"],
                ["log(x)", "自然對數 ln"],
                ["log2(x)", "以 2 為底"],
                ["log10(x)", "以 10 為底"],
                ["sin/cos/tan(x)", "三角函式（弧度）"]
            ]},
            { cat: "C++ iostream", items: [
                ["std::cout << x << '\\n'", "輸出（比 endl 快）"],
                ["std::cin >> x", "輸入"],
                ["std::cin.ignore()", "吃掉換行符"],
                ["std::getline(cin, s)", "讀一整行到 string"]
            ]},
            { cat: "C++ string", items: [
                ["s.size() / s.length()", "字串長度"],
                ["s.substr(pos, len)", "子字串"],
                ["s.find(t)", "找子字串，未找到回傳 string::npos"],
                ["s.replace(pos, len, t)", "取代子字串"],
                ["s.push_back(c)", "尾端加字元"],
                ["s.pop_back()", "移除末字元"],
                ["std::to_string(n)", "數字轉字串"],
                ["std::stoi(s)", "字串轉 int"],
                ["std::stod(s)", "字串轉 double"]
            ]},
            { cat: "C++ vector", items: [
                ["vector<T> v(n, val)", "建立 n 個初值 val"],
                ["v.push_back(x)", "尾端加元素"],
                ["v.pop_back()", "移除末元素"],
                ["v.size()", "元素個數"],
                ["v.empty()", "是否為空"],
                ["v.resize(n)", "調整大小"],
                ["v.clear()", "清空"],
                ["v.begin() / v.end()", "迭代器（供 algorithm 用）"]
            ]},
            { cat: "C++ algorithm", items: [
                ["sort(begin, end)", "排序（升冪）"],
                ["sort(begin, end, cmp)", "自訂排序"],
                ["reverse(begin, end)", "反轉"],
                ["max(a, b) / min(a, b)", "最大/最小值"],
                ["max_element(b, e)", "最大元素迭代器"],
                ["find(b, e, val)", "線性搜尋"],
                ["lower_bound(b, e, val)", "二分搜尋（≥val 的第一個）"],
                ["upper_bound(b, e, val)", "二分搜尋（>val 的第一個）"],
                ["fill(b, e, val)", "全部填 val"],
                ["accumulate(b, e, init)", "求和（需 #include <numeric>）"],
                ["unique(b, e)", "移除連續重複（需先排序）"]
            ]},
            { cat: "C++ map / set", items: [
                ["map<K,V> m", "有序鍵值對（紅黑樹）"],
                ["unordered_map<K,V> m", "雜湊鍵值對，O(1) 均攤"],
                ["m[key] = val", "插入/更新"],
                ["m.count(key)", "是否存在（0或1）"],
                ["m.find(key)", "回傳迭代器"],
                ["m.erase(key)", "刪除"],
                ["set<T> s", "有序不重複集合"],
                ["s.insert(x)", "插入"],
                ["s.count(x)", "是否存在"],
                ["s.erase(x)", "刪除"]
            ]},
            { cat: "C++ queue / stack / priority_queue", items: [
                ["queue<T> q", "FIFO 佇列"],
                ["q.push(x) / q.pop()", "入列/出列"],
                ["q.front() / q.back()", "前端/後端"],
                ["stack<T> st", "LIFO 堆疊"],
                ["st.push(x) / st.pop()", "推入/彈出"],
                ["st.top()", "頂端元素"],
                ["priority_queue<T> pq", "最大堆（預設）"],
                ["priority_queue<T,vector<T>,greater<T>> pq", "最小堆"]
            ]}
        ];

        var DICT_DATA = [
            ["algorithm", "演算法"],
            ["array", "陣列"],
            ["assertion", "斷言"],
            ["binary search", "二分搜尋"],
            ["boolean", "布林值"],
            ["breadth-first search (BFS)", "廣度優先搜尋"],
            ["brute force", "暴力法"],
            ["buffer", "緩衝區"],
            ["callback", "回呼函式"],
            ["class", "類別"],
            ["compile", "編譯"],
            ["complexity", "複雜度（時間/空間）"],
            ["conditional", "條件判斷"],
            ["constant", "常數"],
            ["constructor", "建構子"],
            ["data structure", "資料結構"],
            ["depth-first search (DFS)", "深度優先搜尋"],
            ["destructor", "解構子"],
            ["dynamic programming (DP)", "動態規劃"],
            ["encapsulation", "封裝"],
            ["exception", "例外"],
            ["function", "函式"],
            ["graph", "圖"],
            ["greedy", "貪婪法"],
            ["heap", "堆積"],
            ["inheritance", "繼承"],
            ["iterator", "迭代器"],
            ["linked list", "鏈結串列"],
            ["loop", "迴圈"],
            ["macro", "巨集"],
            ["namespace", "命名空間"],
            ["null pointer", "空指標"],
            ["object", "物件"],
            ["overflow", "溢位"],
            ["overload", "多載"],
            ["override", "覆寫"],
            ["pointer", "指標"],
            ["polymorphism", "多型"],
            ["queue", "佇列"],
            ["recursion", "遞迴"],
            ["reference", "參考（引用）"],
            ["runtime error", "執行期錯誤"],
            ["scope", "範圍（作用域）"],
            ["segment fault", "區段錯誤"],
            ["stack", "堆疊"],
            ["stack overflow", "堆疊溢位"],
            ["string", "字串"],
            ["struct", "結構"],
            ["template", "樣板"],
            ["time limit exceeded (TLE)", "超過時間限制"],
            ["traverse", "走訪/遍歷"],
            ["tree", "樹"],
            ["typedef", "型別別名"],
            ["variable", "變數"],
            ["vector", "動態陣列"],
            ["virtual", "虛擬"],
            ["void", "無（回傳型別）"]
        ];

        // 動態建立兩個面板 DOM（避免靜態 HTML 干擾 body flex 排版）
        makePanelEl("panel-lib", "C/C++ 函式庫速查", "lib-search", "lib-body");
        makePanelEl("panel-dict", "英漢程式術語", "dict-search", "dict-body");

        function makeRow(nameText, descText) {
            var row = document.createElement("div");
            row.className = "ref-item";
            var n = document.createElement("span");
            n.className = "ref-name";
            n.textContent = nameText;
            var d = document.createElement("span");
            d.className = "ref-desc";
            d.textContent = descText;
            row.appendChild(n);
            row.appendChild(d);
            return row;
        }

        function buildLib(filter) {
            var body = document.getElementById("lib-body");
            body.innerHTML = "";
            var f = (filter || "").toLowerCase();
            LIB_DATA.forEach(function (sec) {
                var matched = sec.items.filter(function (it) {
                    return !f || it[0].toLowerCase().includes(f) || it[1].toLowerCase().includes(f);
                });
                if (!matched.length) { return; }
                var section = document.createElement("div");
                section.className = "ref-section";
                var title = document.createElement("div");
                title.className = "ref-section-title";
                title.textContent = sec.cat;     // textContent：避免 <stdio.h> 被當 tag
                section.appendChild(title);
                matched.forEach(function (it) { section.appendChild(makeRow(it[0], it[1])); });
                body.appendChild(section);
            });
        }

        function buildDict(filter) {
            var body = document.getElementById("dict-body");
            body.innerHTML = "";
            var f = (filter || "").toLowerCase();
            var matched = DICT_DATA.filter(function (it) {
                return !f || it[0].toLowerCase().includes(f) || it[1].includes(f);
            });
            var section = document.createElement("div");
            section.className = "ref-section";
            matched.forEach(function (it) { section.appendChild(makeRow(it[0], it[1])); });
            body.appendChild(section);
        }

        buildLib("");
        buildDict("");

        document.getElementById("lib-search").addEventListener("input", function () { buildLib(this.value); });
        document.getElementById("dict-search").addEventListener("input", function () { buildDict(this.value); });

        document.getElementById("ref-lib-btn").addEventListener("click", function () {
            var p = document.getElementById("panel-lib");
            var d = document.getElementById("panel-dict");
            d.classList.add("hidden");
            p.classList.toggle("hidden");
            if (!p.classList.contains("hidden")) document.getElementById("lib-search").focus();
        });
        document.getElementById("ref-dict-btn").addEventListener("click", function () {
            var p = document.getElementById("panel-dict");
            var l = document.getElementById("panel-lib");
            l.classList.add("hidden");
            p.classList.toggle("hidden");
            if (!p.classList.contains("hidden")) document.getElementById("dict-search").focus();
        });
    }

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
