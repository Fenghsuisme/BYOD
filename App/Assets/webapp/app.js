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
            { cat: "stdio.h — I/O", items: [
                ["int printf(const char* fmt, ...)", "Write formatted output to stdout. Returns number of characters written."],
                ["int scanf(const char* fmt, ...)", "Read formatted input from stdin. Pass addresses (&x). Returns number of items read."],
                ["int puts(const char* s)", "Write string s and a newline to stdout."],
                ["int getchar(void)", "Read the next character from stdin. Returns EOF on end-of-file."],
                ["int putchar(int c)", "Write character c to stdout."],
                ["FILE* fopen(const char* path, const char* mode)", "Open a file. mode: \"r\" read, \"w\" write, \"a\" append, \"rb\"/\"wb\" binary."],
                ["int fclose(FILE* fp)", "Close the file. Returns 0 on success."],
                ["int fscanf(FILE* fp, const char* fmt, ...)", "Read formatted input from file fp."],
                ["int fprintf(FILE* fp, const char* fmt, ...)", "Write formatted output to file fp."],
                ["char* fgets(char* buf, int n, FILE* fp)", "Read at most n-1 characters (including '\\n') from fp into buf."],
                ["int sprintf(char* buf, const char* fmt, ...)", "Write formatted output to string buf."],
                ["int sscanf(const char* buf, const char* fmt, ...)", "Read formatted input from string buf."],
                ["int snprintf(char* buf, size_t n, const char* fmt, ...)", "Like sprintf but writes at most n-1 characters (safe)."],
                ["int feof(FILE* fp)", "Return non-zero if end-of-file indicator is set for fp."],
                ["int fseek(FILE* fp, long off, int origin)", "Set file position. origin: SEEK_SET / SEEK_CUR / SEEK_END."],
                ["long ftell(FILE* fp)", "Return current file position."]
            ]},
            { cat: "string.h — Strings & Memory", items: [
                ["size_t strlen(const char* s)", "Return the length of s (excluding null terminator)."],
                ["int strcmp(const char* a, const char* b)", "Compare strings. Returns <0 if a<b, 0 if equal, >0 if a>b."],
                ["int strncmp(const char* a, const char* b, size_t n)", "Compare at most n characters of a and b."],
                ["char* strcpy(char* dst, const char* src)", "Copy src (including '\\0') to dst. Returns dst."],
                ["char* strncpy(char* dst, const char* src, size_t n)", "Copy at most n characters from src to dst."],
                ["char* strcat(char* dst, const char* src)", "Append src to the end of dst. Returns dst."],
                ["char* strstr(const char* hay, const char* needle)", "Find first occurrence of needle in hay. Returns pointer or NULL."],
                ["char* strchr(const char* s, int c)", "Find first occurrence of c in s. Returns pointer or NULL."],
                ["char* strrchr(const char* s, int c)", "Find last occurrence of c in s. Returns pointer or NULL."],
                ["void* memset(void* ptr, int val, size_t n)", "Fill n bytes at ptr with val. Commonly used to zero arrays."],
                ["void* memcpy(void* dst, const void* src, size_t n)", "Copy n bytes from src to dst. Regions must not overlap."],
                ["void* memmove(void* dst, const void* src, size_t n)", "Copy n bytes; safe when regions overlap."],
                ["int memcmp(const void* a, const void* b, size_t n)", "Compare n bytes. Returns <0 / 0 / >0."]
            ]},
            { cat: "stdlib.h — Memory, Conversion, Utility", items: [
                ["void* malloc(size_t size)", "Allocate size bytes. Returns pointer or NULL on failure."],
                ["void* calloc(size_t n, size_t size)", "Allocate n*size bytes, zero-initialized."],
                ["void* realloc(void* ptr, size_t size)", "Resize allocation at ptr to size bytes."],
                ["void free(void* ptr)", "Deallocate memory previously returned by malloc/calloc/realloc."],
                ["int atoi(const char* s)", "Convert string s to int. No error detection; prefer strtol."],
                ["long atol(const char* s)", "Convert string s to long."],
                ["double atof(const char* s)", "Convert string s to double."],
                ["long strtol(const char* s, char** end, int base)", "Convert string to long with base and error pointer."],
                ["double strtod(const char* s, char** end)", "Convert string to double with error pointer."],
                ["int abs(int n)", "Absolute value of int n."],
                ["long labs(long n)", "Absolute value of long n."],
                ["long long llabs(long long n)", "Absolute value of long long n."],
                ["int rand(void)", "Return pseudo-random integer in [0, RAND_MAX]."],
                ["void srand(unsigned int seed)", "Seed the pseudo-random number generator."],
                ["void qsort(void* base, size_t n, size_t sz, int (*cmp)(const void*,const void*))", "Sort n elements of size sz. cmp returns <0/0/>0."],
                ["void* bsearch(const void* key, const void* base, size_t n, size_t sz, int (*cmp)(const void*,const void*))", "Binary search in sorted array. Returns pointer to match or NULL."],
                ["void exit(int code)", "Terminate the process with exit code."]
            ]},
            { cat: "math.h — Mathematics", items: [
                ["double sqrt(double x)", "Square root of x."],
                ["double pow(double x, double y)", "x raised to the power y."],
                ["double fabs(double x)", "Absolute value of floating-point x."],
                ["double ceil(double x)", "Smallest integer not less than x (round up)."],
                ["double floor(double x)", "Largest integer not greater than x (round down)."],
                ["double round(double x)", "Round x to nearest integer, halfway cases away from zero."],
                ["double trunc(double x)", "Round x toward zero (truncate)."],
                ["double fmod(double x, double y)", "Floating-point remainder of x/y."],
                ["double log(double x)", "Natural logarithm (base e) of x."],
                ["double log2(double x)", "Base-2 logarithm of x."],
                ["double log10(double x)", "Base-10 logarithm of x."],
                ["double exp(double x)", "e raised to the power x."],
                ["double sin(double x)", "Sine of x (radians)."],
                ["double cos(double x)", "Cosine of x (radians)."],
                ["double tan(double x)", "Tangent of x (radians)."],
                ["double asin(double x)", "Arc sine of x, result in [-pi/2, pi/2]."],
                ["double acos(double x)", "Arc cosine of x, result in [0, pi]."],
                ["double atan2(double y, double x)", "Arc tangent of y/x, using signs to determine quadrant."],
                ["double hypot(double x, double y)", "Hypotenuse: sqrt(x*x + y*y) without overflow."]
            ]},
            { cat: "C++ — iostream", items: [
                ["std::cout << x", "Insert x into cout (stdout). Chain with << or use '\\n' (faster than endl)."],
                ["std::cin >> x", "Extract from cin (stdin) into x. Skips whitespace."],
                ["std::cerr << x", "Insert x into cerr (stderr, unbuffered)."],
                ["std::getline(std::istream& is, std::string& s)", "Read a full line from is into s (including spaces, excluding '\\n')."],
                ["std::cin.ignore(n, delim)", "Discard up to n characters or until delim. Use after scanf/cin mix."],
                ["std::ios::sync_with_stdio(false); std::cin.tie(nullptr);", "Speed up cin/cout by decoupling from C stdio."]
            ]},
            { cat: "C++ — string", items: [
                ["size_type s.size() / s.length()", "Return number of characters in s."],
                ["bool s.empty()", "Return true if s is empty."],
                ["std::string s.substr(size_type pos, size_type len = npos)", "Return substring starting at pos with length len."],
                ["size_type s.find(const string& t, size_type pos = 0)", "Find first occurrence of t starting at pos. Returns npos if not found."],
                ["size_type s.rfind(const string& t)", "Find last occurrence of t."],
                ["std::string& s.replace(size_type pos, size_type len, const string& t)", "Replace len characters from pos with t."],
                ["std::string& s.insert(size_type pos, const string& t)", "Insert t before position pos."],
                ["std::string& s.erase(size_type pos, size_type len = npos)", "Remove len characters starting at pos."],
                ["void s.push_back(char c)", "Append character c."],
                ["void s.pop_back()", "Remove the last character."],
                ["const char* s.c_str()", "Return null-terminated C string equivalent."],
                ["int s.compare(const string& t)", "Compare s with t. Returns <0 / 0 / >0."],
                ["std::string std::to_string(T val)", "Convert arithmetic value to string."],
                ["int std::stoi(const string& s, size_t* pos=0, int base=10)", "Convert string to int."],
                ["long std::stol(const string& s)", "Convert string to long."],
                ["double std::stod(const string& s)", "Convert string to double."]
            ]},
            { cat: "C++ — vector", items: [
                ["vector<T> v", "Default constructor — empty vector."],
                ["vector<T> v(n, val)", "Construct with n copies of val."],
                ["void v.push_back(const T& x)", "Append x to the end. Amortized O(1)."],
                ["void v.pop_back()", "Remove the last element. O(1)."],
                ["T& v.front() / v.back()", "Reference to first / last element."],
                ["T& v.at(size_type i)", "Element at index i with bounds checking."],
                ["size_type v.size()", "Number of elements."],
                ["bool v.empty()", "Return true if size == 0."],
                ["void v.resize(size_type n, T val = T())", "Resize to n elements; new elements initialized to val."],
                ["void v.reserve(size_type n)", "Reserve capacity for at least n elements (no size change)."],
                ["void v.clear()", "Erase all elements; size becomes 0."],
                ["void v.assign(size_type n, const T& val)", "Replace contents with n copies of val."],
                ["iterator v.begin() / v.end()", "Iterator to first element / one-past-last."],
                ["iterator v.insert(iterator pos, const T& x)", "Insert x before pos."],
                ["iterator v.erase(iterator pos)", "Erase element at pos. Returns iterator to next."]
            ]},
            { cat: "C++ — algorithm", items: [
                ["void sort(It first, It last)", "Sort [first, last) in ascending order. O(n log n)."],
                ["void sort(It first, It last, Comp cmp)", "Sort with custom comparator. cmp(a,b) returns true if a should come before b."],
                ["void stable_sort(It first, It last)", "Sort preserving relative order of equal elements."],
                ["void reverse(It first, It last)", "Reverse elements in [first, last)."],
                ["It min_element(It first, It last)", "Return iterator to the minimum element."],
                ["It max_element(It first, It last)", "Return iterator to the maximum element."],
                ["const T& min(const T& a, const T& b)", "Return the smaller of a and b."],
                ["const T& max(const T& a, const T& b)", "Return the larger of a and b."],
                ["It find(It first, It last, const T& val)", "Linear search for val. Returns end if not found."],
                ["It find_if(It first, It last, Pred p)", "Return first element for which p(elem) is true."],
                ["ptrdiff_t count(It first, It last, const T& val)", "Count occurrences of val."],
                ["It lower_bound(It first, It last, const T& val)", "First position where val can be inserted (>= val). Requires sorted range."],
                ["It upper_bound(It first, It last, const T& val)", "First position after all elements equal to val (> val)."],
                ["bool binary_search(It first, It last, const T& val)", "Return true if val exists in sorted range."],
                ["void fill(It first, It last, const T& val)", "Assign val to every element in [first, last)."],
                ["It copy(It first, It last, It out)", "Copy [first, last) to range starting at out."],
                ["It unique(It first, It last)", "Remove consecutive duplicates; returns new logical end (requires prior sort)."],
                ["bool next_permutation(It first, It last)", "Advance to next lexicographic permutation. Returns false if wrapped around."],
                ["bool prev_permutation(It first, It last)", "Retreat to previous lexicographic permutation."],
                ["void swap(T& a, T& b)", "Exchange values of a and b."],
                ["void nth_element(It first, It nth, It last)", "Rearrange so element at nth is what would be there if sorted; O(n)."]
            ]},
            { cat: "C++ — numeric", items: [
                ["T accumulate(It first, It last, T init)", "Sum elements starting from init. (#include <numeric>)"],
                ["T accumulate(It first, It last, T init, BinOp op)", "Fold with binary operation op."],
                ["void iota(It first, It last, T val)", "Fill with val, val+1, val+2, … (#include <numeric>)"],
                ["void partial_sum(It first, It last, It out)", "Write prefix sums to out. (#include <numeric>)"]
            ]},
            { cat: "C++ — map / unordered_map", items: [
                ["map<K,V>", "Ordered key-value container (red-black tree). O(log n) operations."],
                ["unordered_map<K,V>", "Hash-based key-value container. O(1) amortized operations."],
                ["V& m[key]", "Access or insert element with key (inserts default if absent)."],
                ["V& m.at(key)", "Access element; throws std::out_of_range if absent."],
                ["size_type m.count(key)", "Return 1 if key exists, 0 otherwise."],
                ["iterator m.find(key)", "Return iterator to element, or m.end() if not found."],
                ["pair<It,bool> m.insert({key, val})", "Insert; bool is true if insertion took place."],
                ["size_type m.erase(key)", "Remove element with key. Returns number removed."],
                ["size_type m.size()", "Number of key-value pairs."],
                ["bool m.empty()", "Return true if map is empty."]
            ]},
            { cat: "C++ — set / unordered_set", items: [
                ["set<T>", "Ordered unique elements (red-black tree). O(log n) operations."],
                ["unordered_set<T>", "Hash-based unique elements. O(1) amortized."],
                ["pair<It,bool> s.insert(val)", "Insert val; bool is true if inserted (not duplicate)."],
                ["size_type s.count(val)", "Return 1 if val exists, 0 otherwise."],
                ["iterator s.find(val)", "Return iterator to val, or s.end() if not found."],
                ["size_type s.erase(val)", "Remove val. Returns number removed."],
                ["iterator s.lower_bound(val)", "First element >= val (set only)."],
                ["iterator s.upper_bound(val)", "First element > val (set only)."]
            ]},
            { cat: "C++ — queue / stack / deque", items: [
                ["queue<T>", "FIFO container adaptor (backed by deque by default)."],
                ["void q.push(const T& x)", "Insert x at the back."],
                ["void q.pop()", "Remove the front element."],
                ["T& q.front()", "Access the front element."],
                ["T& q.back()", "Access the back element."],
                ["stack<T>", "LIFO container adaptor."],
                ["void st.push(const T& x)", "Push x onto the top."],
                ["void st.pop()", "Remove the top element."],
                ["T& st.top()", "Access the top element."],
                ["deque<T>", "Double-ended queue. O(1) push/pop at both ends."],
                ["void dq.push_front(x) / dq.pop_front()", "Insert / remove at front."]
            ]},
            { cat: "C++ — priority_queue", items: [
                ["priority_queue<T>", "Max-heap by default. Top element is the largest."],
                ["priority_queue<T, vector<T>, greater<T>>", "Min-heap. Top element is the smallest."],
                ["void pq.push(const T& x)", "Insert x into the heap."],
                ["void pq.pop()", "Remove the top element."],
                ["const T& pq.top()", "Access the top (max or min) element."],
                ["size_type pq.size()", "Number of elements."],
                ["bool pq.empty()", "Return true if empty."]
            ]},
            { cat: "C++ — pair / tuple", items: [
                ["pair<T1,T2> p(a, b)", "Pair of two values. Access via p.first and p.second."],
                ["auto p = make_pair(a, b)", "Construct pair with type deduction."],
                ["tuple<T1,T2,...> t(a,b,...)", "Fixed-size heterogeneous value collection."],
                ["auto t = make_tuple(a, b, c)", "Construct tuple with type deduction."],
                ["T get<I>(tuple)", "Access element at index I (compile-time constant)."],
                ["auto [a, b] = p", "Structured binding (C++17) — unpack pair or tuple."]
            ]}
        ];

        var DICT_DATA = [
            // ── A ──
            ["abandon","放棄；拋棄"],["ability","能力；技能"],["above","在……之上；超過"],
            ["absent","缺席的；不存在的"],["absolute","絕對的；完全的"],
            ["accept","接受；接收"],["access","存取；進入"],["achieve","達成；實現"],
            ["across","穿越；橫跨"],["actual","實際的；真正的"],
            ["add","加；添加"],["adjacent","相鄰的"],["adjust","調整；修正"],
            ["advance","前進；進階"],["after","在……之後"],["again","再次；重新"],
            ["against","對抗；相對"],["age","年齡；時代"],["ahead","在前方；提前"],
            ["aim","目標；目的"],["all","所有的"],["allow","允許；使得"],
            ["almost","幾乎；差不多"],["along","沿著；一起"],["already","已經"],
            ["also","也；此外"],["although","雖然；儘管"],["always","總是；永遠"],
            ["among","在……之中"],["amount","數量；總量"],["analyze","分析"],
            ["and","和；並且"],["angle","角度；角"],["answer","答案；回答"],
            ["any","任意；任何"],["apart","分開；相距"],["appear","出現；顯現"],
            ["apply","套用；應用"],["approach","方法；接近"],["approximately","大約"],
            ["area","面積；區域"],["arrange","排列；安排"],["ascending","升冪的；遞增的"],
            ["assign","指定；賦值"],["assume","假設；假定"],["at least","至少"],
            ["at most","至多"],["average","平均值；平均的"],
            // ── B ──
            ["balance","平衡；均衡"],["base","底數；基底；基礎"],["because","因為"],
            ["before","在……之前"],["begin","開始"],["behind","在……後面"],
            ["below","在……之下；低於"],["beside","在……旁邊"],
            ["between","在……之間"],["beyond","超過；在……之外"],
            ["binary","二進位的；二元的"],["both","兩者都"],["bound","界限；邊界"],
            ["boundary","邊界；界線"],["build","建立；構建"],["but","但是；然而"],
            ["by","被；藉由；旁邊"],
            // ── C ──
            ["calculate","計算"],["call","呼叫；稱為"],["can","能夠；可以"],
            ["capacity","容量；上限"],["case","情況；案例"],
            ["cause","原因；導致"],["certain","確定的；某個"],
            ["change","改變；更換"],["character","字元；字符；特性"],
            ["check","檢查；確認"],["child","子節點；子代"],
            ["choose","選擇；挑選"],["circular","循環的；環狀的"],
            ["clear","清除；清晰的"],["close","關閉；接近的"],
            ["closest","最近的"],["collect","收集；蒐集"],
            ["column","欄；直行"],["combine","合併；組合"],
            ["common","共同的；公共的"],["compare","比較"],
            ["complete","完整的；完成"],["complex","複雜的"],
            ["component","元件；組成部分"],["compute","計算；運算"],
            ["condition","條件；狀況"],["connect","連接；連通"],
            ["consecutive","連續的"],["consider","考慮；視為"],
            ["constant","常數；固定的"],["constraint","限制；約束"],
            ["construct","建構；構造"],["contain","包含；容納"],
            ["continue","繼續"],["convert","轉換；轉化"],
            ["copy","複製；副本"],["correct","正確的"],["cost","成本；代價"],
            ["count","計數；數量"],["create","建立；創造"],
            ["current","目前的；當前"],["cycle","循環；環"],
            // ── D ──
            ["data","資料；數據"],["decide","決定"],["decrease","減少；遞減"],
            ["define","定義；規定"],["delete","刪除"],["depth","深度"],
            ["descending","降冪的；遞減的"],["describe","描述"],
            ["design","設計"],["detail","細節；詳細"],
            ["determine","決定；判斷"],["develop","開發；發展"],
            ["difference","差值；差異"],["digit","數字（0–9）"],
            ["directed","有向的"],["direction","方向"],["distance","距離"],
            ["distinct","相異的；不重複的"],["divide","除；分割"],
            ["divisible","可整除的"],["divisor","因數；除數"],
            ["do","做；執行"],["down","向下"],["drop","丟棄；減少"],
            ["duplicate","重複的；副本"],["during","在……期間"],
            // ── E ──
            ["each","每一個"],["early","早期的；提前"],["edge","邊；邊緣"],
            ["effect","效果；影響"],["efficient","有效率的"],
            ["element","元素；成員"],["else","否則；其他"],
            ["empty","空的"],["enable","啟用；使能夠"],["end","結尾；結束"],
            ["enough","足夠的"],["entire","整個的；全部"],
            ["equal","相等的；等號"],["error","錯誤"],
            ["even","偶數的；甚至；平坦"],["every","每個；所有"],
            ["exactly","恰好；確切地"],["example","範例；例子"],
            ["exceed","超過；超出"],["except","除了；排除"],
            ["exist","存在"],["extend","延伸；擴展"],
            // ── F ──
            ["factor","因數；因子"],["fail","失敗；不符合"],
            ["false","假；錯誤的"],["far","遠的；很遠地"],
            ["farthest","最遠的"],["feature","特徵；功能"],
            ["few","幾個；少數"],["fewer","更少的"],["field","欄位；領域"],
            ["final","最終的"],["find","找到；尋找"],["finite","有限的"],
            ["first","第一；首先"],["fixed","固定的"],["floor","地板；向下取整"],
            ["follow","跟隨；遵循"],["for each","對每個"],["force","強制；力"],
            ["form","形成；構成"],["forward","向前"],["frequency","頻率；次數"],
            ["from","從；自"],["full","完整的；滿的"],["further","更遠；進一步"],
            // ── G ──
            ["generate","生成；產生"],["given","給定的"],["goal","目標"],
            ["graph","圖"],["greater","更大；大於"],["greatest","最大的"],
            ["group","群組；分組"],["guarantee","保證；確保"],["guess","猜測"],
            // ── H ──
            ["half","一半"],["happen","發生"],["height","高度"],
            ["help","幫助；協助"],["here","在這裡"],["high","高的"],
            ["hold","持有；保持"],["horizontal","水平的"],["however","然而；不過"],
            // ── I ──
            ["identify","識別；找出"],["if","如果；條件"],
            ["if and only if","若且唯若"],["ignore","忽略"],
            ["implement","實作；實現"],["improve","改善；提升"],
            ["include","包含；納入"],["increase","增加；遞增"],
            ["index","索引；下標"],["indicate","表示；指出"],
            ["individual","個別的；單獨的"],["infinite","無限的"],
            ["initial","初始的；起始"],["input","輸入"],
            ["insert","插入"],["instead","代替；改為"],
            ["integer","整數"],["intersect","相交"],
            ["interval","區間；間隔"],["into","進入；到……裡"],
            ["involve","涉及；包含"],["iterate","遍歷；迭代"],
            // ── J–K ──
            ["join","連接；合併"],["just","恰好；只是"],
            ["keep","保持；持有"],["key","鍵；關鍵"],["kind","種類；類型"],
            ["know","知道"],["known","已知的"],
            // ── L ──
            ["label","標籤；標記"],["large","大的"],["last","最後的"],
            ["layer","層"],["lead","引導；領先"],["least","最小；最少"],
            ["left","左邊；左側"],["length","長度"],["less","更少；小於"],
            ["level","層次；等級"],["lexicographic","字典序的"],
            ["limit","限制；上限"],["link","連接；鏈"],["list","列表；串列"],
            ["local","局部的；本地的"],["long","長的；長整數"],
            ["loop","迴圈"],["lower","較低的；下界"],
            // ── M ──
            ["make","製造；使得"],["many","許多的"],["mark","標記"],
            ["match","匹配；符合"],["matrix","矩陣"],["maximum","最大值；最大的"],
            ["merge","合併"],["minimum","最小值；最小的"],["minus","減去；負號"],
            ["mode","眾數；模式"],["modify","修改；修正"],["modulo","模；取餘數"],
            ["more","更多"],["most","最多；大多數"],["move","移動"],
            ["multiple","倍數；多個"],["multiply","乘以"],["must","必須"],
            // ── N ──
            ["natural","自然的；天然的"],["need","需要"],
            ["negative","負數；負的"],["neighbor","鄰居；鄰近"],
            ["neither","兩者都不"],["never","從不；絕不"],
            ["new","新的"],["next","下一個；接下來"],["no","否；沒有"],
            ["node","節點"],["none","無；沒有"],["nor","也不"],
            ["not","不；非"],["now","現在"],["number","數；數量"],
            // ── O ──
            ["obtain","獲得；得到"],["occur","發生；出現"],["odd","奇數的"],
            ["of","的；屬於"],["often","經常；頻繁"],["once","一次；一旦"],
            ["only","只有；僅"],["operation","操作；運算"],
            ["optimal","最優的；最佳的"],["order","順序；排序"],
            ["or","或；否則"],["other","其他的"],["otherwise","否則；另一方面"],
            ["output","輸出"],["outside","外部；在外面"],
            ["over","超過；在……之上"],["overlap","重疊"],["own","自己的；擁有"],
            // ── P ──
            ["pair","對；配對"],["parent","父節點；父代"],["part","部分"],
            ["pass","通過；傳遞"],["path","路徑"],["pattern","模式；規律"],
            ["per","每；按照"],["permutation","排列"],["pick","挑選；取得"],
            ["plus","加上；正號"],["point","點；分數"],
            ["position","位置"],["positive","正數；正的"],["possible","可能的"],
            ["power","次方；冪；能力"],["prefix","前綴"],
            ["previous","之前的；前一個"],["prime","質數；首要的"],
            ["print","輸出；列印"],["problem","問題"],
            ["process","處理；過程"],["product","乘積"],["proper","適當的"],
            ["provided","假設；條件為"],["prove","證明"],["put","放置；使"],
            // ── Q ──
            ["query","查詢"],["quite","相當；完全"],["quotient","商"],
            // ── R ──
            ["random","隨機的"],["range","範圍；區間"],["reach","到達；可達"],
            ["read","讀取"],["real","實數；真實的"],["record","記錄"],
            ["reduce","減少；化簡"],["remain","剩餘；保持"],
            ["remainder","餘數"],["remove","移除；刪除"],
            ["replace","替換"],["represent","代表；表示"],
            ["require","需要；要求"],["rest","其餘；剩下"],
            ["result","結果"],["return","回傳；返回"],
            ["reverse","反轉；反向"],["right","右邊；正確的"],
            ["root","根節點；根"],["round","四捨五入；回合"],["row","行"],
            ["run","執行；跑"],
            // ── S ──
            ["same","相同的"],["satisfy","滿足"],["save","儲存；節省"],
            ["search","搜尋"],["select","選取；選擇"],
            ["separate","分開；區隔"],["sequence","序列；數列"],
            ["set","集合；設定"],["shortest","最短的"],
            ["show","顯示；說明"],["simple","簡單的"],["since","自從；因為"],
            ["single","單一的"],["size","大小；尺寸"],["small","小的"],
            ["solution","解答；解法"],["some","某些；一些"],["sort","排序"],
            ["source","來源；起點"],["space","空間；空格"],
            ["split","分割；切分"],["start","開始；起點"],
            ["step","步驟；步長"],["still","仍然；靜止的"],
            ["store","儲存；存放"],["string","字串"],["structure","結構"],
            ["subarray","子陣列"],["subset","子集合"],["subtree","子樹"],
            ["such that","使得；滿足條件"],["sum","總和；和"],["support","支援；支持"],
            ["suppose","假設；假定"],["sure","確定的"],["swap","交換"],
            // ── T ──
            ["take","取得；花費"],["target","目標"],["term","項；術語"],
            ["test","測試；測試資料"],["than","比；超過"],
            ["then","然後；那麼"],["there","在那裡"],
            ["therefore","因此；所以"],["through","穿越；透過"],
            ["thus","因此；從而"],["together","一起；合在一起"],
            ["total","總計；合計"],["toward","朝向；趨近"],
            ["track","追蹤"],["traverse","遍歷"],["tree","樹"],
            ["true","真；正確"],["try","嘗試"],["twice","兩次；兩倍"],
            ["two","二；兩個"],["type","類型；型別"],
            // ── U ──
            ["under","在……之下；少於"],["union","聯集；合併"],
            ["unique","唯一的；不重複的"],["unless","除非"],["until","直到"],
            ["update","更新"],["upper","較高的；上界"],["use","使用"],
            // ── V ──
            ["valid","有效的；合法的"],["value","值；數值"],["vary","變化；不同"],
            ["verify","驗證；確認"],["vertex","頂點"],["very","非常"],["visit","走訪；訪問"],
            // ── W ──
            ["weight","權重"],["when","當……時"],["whether","是否"],
            ["which","哪個；哪些"],["while","當……期間；迴圈"],
            ["will","將會；意志"],["with","與；具有"],["without","沒有；不含"],
            ["work","工作；運作"],["write","寫入"],
            // ── Y–Z ──
            ["yet","還沒；然而"],["zero","零"],["zero-indexed","從 0 開始索引的"],
            // ── 數學/邏輯術語 ──
            ["absolute value","絕對值"],["arithmetic","算術；等差的"],
            ["ceiling","上取整（往正無限大）"],["coefficient","係數"],
            ["combination","組合"],["decimal","小數；十進位"],
            ["denominator","分母"],["diagonal","對角線；對角的"],
            ["equation","方程式"],["exponent","指數"],["factorial","階乘"],
            ["fraction","分數；片段"],["GCD / greatest common divisor","最大公因數"],
            ["inequality","不等式"],["LCM / least common multiple","最小公倍數"],
            ["logarithm","對數"],["median","中位數"],["modulus","模；絕對值"],
            ["numerator","分子"],["parity","奇偶性"],["prime number","質數"],
            ["probability","機率"],["proportion","比例"],["ratio","比率；比值"],
            ["series","數列；級數"],["square root","平方根"],["triangle","三角形"],
            ["undirected","無向的"],["weighted","有權重的"],
            // ── 題目用語 ──
            ["at most K times","最多 K 次"],["at least once","至少一次"],
            ["construct an answer","構造一組解"],["does not exist","不存在"],
            ["find the minimum","求最小值"],["find the maximum","求最大值"],
            ["guarantee","保證"],["hence","因此；由此"],
            ["if no such … exists","若不存在這樣的……"],
            ["in ascending order","按升冪排列"],["in non-decreasing order","非遞減順序"],
            ["in non-increasing order","非遞增順序"],
            ["modulo 10^9+7","對 10^9+7 取模"],["note that","注意"],
            ["otherwise","否則"],["output -1 if impossible","若不可能則輸出 -1"],
            ["print the answer","輸出答案"],["such that","使得"],
            ["the number of","……的個數"],["without loss of generality","不失一般性"]
        ];

        // 動態建立兩個面板 DOM（避免靜態 HTML 干擾 body flex 排版）
        makePanelEl("panel-lib", "C/C++ Library", "lib-search", "lib-body");
        makePanelEl("panel-dict", "英漢字典", "dict-search", "dict-body");

        function makeLibRow(sig, desc) {
            var row = document.createElement("div");
            row.className = "ref-item-lib";
            var s = document.createElement("div");
            s.className = "ref-sig";
            s.textContent = sig;
            var d = document.createElement("div");
            d.className = "ref-lib-desc";
            d.textContent = desc;
            row.appendChild(s);
            row.appendChild(d);
            return row;
        }

        function makeDictRow(en, zh) {
            var row = document.createElement("div");
            row.className = "ref-item-dict";
            var e = document.createElement("span");
            e.className = "ref-en";
            e.textContent = en;
            var z = document.createElement("span");
            z.className = "ref-zh";
            z.textContent = zh;
            row.appendChild(e);
            row.appendChild(z);
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
                title.textContent = sec.cat;
                section.appendChild(title);
                matched.forEach(function (it) { section.appendChild(makeLibRow(it[0], it[1])); });
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
            matched.forEach(function (it) { section.appendChild(makeDictRow(it[0], it[1])); });
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
