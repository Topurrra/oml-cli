/*! OUI theme: light or dark (html[data-theme]) and the accent palette (html[data-accent]), saved per browser. */
(function () {
    var root = document.documentElement;
    var OUI = window.OUI = window.OUI || {};
    function read(key) { try { return window.localStorage.getItem(key); } catch (e) { return null; } }
    function write(key, value) { try { if (value) { window.localStorage.setItem(key, value); } else { window.localStorage.removeItem(key); } } catch (e) { } }
    function reduced() { return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches; }

    var saved = read("oui-theme");
    if (saved === "dark" || saved === "light") { root.setAttribute("data-theme", saved); }
    else if (root.getAttribute("data-theme") === "dark" || root.getAttribute("data-theme") === "light") { }
    else if (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches) { root.setAttribute("data-theme", "dark"); }
    else { root.setAttribute("data-theme", "light"); }
    var accent = read("oui-accent");
    if (accent) { root.setAttribute("data-accent", accent); }

    OUI.theme = {
        get: function () { return root.getAttribute("data-theme") === "dark" ? "dark" : "light"; },
        set: function (theme, fromElementId) {
            var apply = function () { root.setAttribute("data-theme", theme); write("oui-theme", theme); };
            if (!document.startViewTransition || reduced()) { apply(); return; }
            var from = fromElementId && document.getElementById(fromElementId);
            var box = from ? from.getBoundingClientRect() : { left: window.innerWidth / 2, top: 0, width: 0, height: 0 };
            var x = box.left + box.width / 2, y = box.top + box.height / 2;
            var r = Math.hypot(Math.max(x, window.innerWidth - x), Math.max(y, window.innerHeight - y));
            var transition = document.startViewTransition(apply);
            transition.ready.then(function () {
                root.animate({ clipPath: ["circle(0px at " + x + "px " + y + "px)", "circle(" + r + "px at " + x + "px " + y + "px)"] },
                    { duration: 520, easing: "cubic-bezier(.2, .8, .2, 1)", pseudoElement: "::view-transition-new(root)" });
            });
        },
        toggle: function (fromElementId) { OUI.theme.set(OUI.theme.get() === "dark" ? "light" : "dark", fromElementId); },
        accent: function (name) {
            if (!name || name === "brand") { root.removeAttribute("data-accent"); write("oui-accent", null); }
            else { root.setAttribute("data-accent", name); write("oui-accent", name); }
        }
    };
})();

/* Top bar links: a mouse wheel scrolls the link row sideways, and the edges fade while more links are out of view. */
(function () {
    function edges(nav) {
        nav.classList.toggle("is-more-start", nav.scrollLeft > 2);
        nav.classList.toggle("is-more-end", nav.scrollLeft + nav.clientWidth < nav.scrollWidth - 2);
    }
    document.addEventListener("wheel", function (e) {
        var nav = e.target.closest && e.target.closest(".oui-topbar__nav");
        if (!nav || nav.scrollWidth <= nav.clientWidth || Math.abs(e.deltaX) > Math.abs(e.deltaY)) { return; }
        nav.scrollLeft += e.deltaY;
        e.preventDefault();
    }, { passive: false });
    document.addEventListener("scroll", function (e) {
        if (e.target.classList && e.target.classList.contains("oui-topbar__nav")) { edges(e.target); }
    }, true);
    function all() { document.querySelectorAll(".oui-topbar__nav").forEach(function (nav) {
        var active = nav.querySelector(".is-active");
        if (active && !nav.__ouiShown) { nav.__ouiShown = true; nav.scrollLeft = active.offsetLeft - nav.offsetLeft - (nav.clientWidth - active.offsetWidth) / 2; }
        edges(nav);
    }); }
    window.addEventListener("resize", all);
    var queued = false;
    new MutationObserver(function () { if (!queued) { queued = true; requestAnimationFrame(function () { queued = false; all(); }); } }).observe(document.documentElement, { subtree: true, childList: true });
    all();
})();

/*! OUI apps: kanban drag and drop, week calendar, gantt, node editor, chat composer keys. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    if (OUI.kanban) { return; }
    function byId(id) { return id ? document.getElementById(id) : null; }
    function keep(el, cleanup) { if (el._ouiCleanup) { el._ouiCleanup(); } el._ouiCleanup = cleanup; }
    function h(tag, cls, text) { var e = document.createElement(tag); if (cls) { e.className = cls; } if (text !== undefined) { e.textContent = text; } return e; }
    function rows(t) { return (t || "").split(";").map(function (r) { return r.split("|").map(function (x) { return x.trim(); }); }).filter(function (r) { return r[0] !== ""; }); }
    var NS = "http://www.w3.org/2000/svg";
    function sv(tag, attrs, parent) { var e = document.createElementNS(NS, tag); for (var k in attrs) { e.setAttribute(k, attrs[k]); } if (parent) { parent.appendChild(e); } return e; }

    // Kanban: cards [data-oui-card] dragged onto columns [data-oui-column]; onMove(cardKey, columnKey).
    OUI.kanban = {
        mount: function (id, onMove) {
            var root = byId(id); if (!root) { return; }
            var dragging = null, ghost = null, startX = 0, startY = 0, over = null;
            function down(e) {
                var card = e.target.closest("[data-oui-card]"); if (!card || e.button !== 0 || e.target.closest("button, a, input")) { return; }
                dragging = card; startX = e.clientX; startY = e.clientY;
                card.setPointerCapture(e.pointerId);
            }
            function move(e) {
                if (!dragging) { return; }
                var dx = e.clientX - startX, dy = e.clientY - startY;
                if (!ghost && Math.abs(dx) + Math.abs(dy) < 6) { return; }
                if (!ghost) { ghost = dragging; ghost.classList.add("is-dragging"); root.classList.add("is-dragging"); }
                ghost.style.transform = "translate(" + dx + "px," + dy + "px) rotate(1.5deg)";
                ghost.style.pointerEvents = "none";
                var col = document.elementFromPoint(e.clientX, e.clientY); col = col && col.closest("[data-oui-column]");
                if (over && over !== col) { over.classList.remove("is-over"); }
                over = col; if (over) { over.classList.add("is-over"); }
            }
            function up() {
                if (!dragging) { return; }
                if (ghost) {
                    ghost.classList.remove("is-dragging"); ghost.style.transform = ""; ghost.style.pointerEvents = ""; root.classList.remove("is-dragging");
                    if (over) { over.classList.remove("is-over"); var to = over.getAttribute("data-oui-column"), from = dragging.closest("[data-oui-column]"); if (!from || from.getAttribute("data-oui-column") !== to) { onMove(dragging.getAttribute("data-oui-card"), to); } }
                }
                dragging = null; ghost = null; over = null;
            }
            function key(e) {
                var card = e.target.closest("[data-oui-card]"); if (!card || (e.key !== "ArrowLeft" && e.key !== "ArrowRight") || !e.altKey) { return; }
                var cols = Array.prototype.slice.call(root.querySelectorAll("[data-oui-column]")), i = cols.indexOf(card.closest("[data-oui-column]")), j = i + (e.key === "ArrowLeft" ? -1 : 1);
                if (j >= 0 && j < cols.length) { e.preventDefault(); onMove(card.getAttribute("data-oui-card"), cols[j].getAttribute("data-oui-column")); }
            }
            root.addEventListener("pointerdown", down); root.addEventListener("pointermove", move); root.addEventListener("pointerup", up); root.addEventListener("pointercancel", up); root.addEventListener("keydown", key);
            keep(root, function () { root.removeEventListener("pointerdown", down); root.removeEventListener("pointermove", move); root.removeEventListener("pointerup", up); root.removeEventListener("pointercancel", up); root.removeEventListener("keydown", key); });
        }
    };

    // Week calendar: events "Day(1-7)|Start(HH:MM)|End|Title|Tone;..."; onPick(index) when an event is clicked.
    OUI.week = {
        render: function (id, events, startHour, endHour, days, onPick) {
            var root = byId(id); if (!root) { return; }
            while (root.firstChild) { root.removeChild(root.firstChild); }
            var names = (days || "Mon,Tue,Wed,Thu,Fri,Sat,Sun").split(","), from = startHour || 8, to = endHour || 18, hours = to - from;
            var grid = h("div", "oui-week__grid"); grid.style.setProperty("--oui-days", names.length); grid.style.setProperty("--oui-hours", hours);
            grid.appendChild(h("div", "oui-week__corner"));
            names.forEach(function (d) { grid.appendChild(h("div", "oui-week__day", d)); });
            for (var i = 0; i < hours; i++) { var lbl = h("div", "oui-week__hour", (from + i) + ":00"); lbl.style.gridRow = String(i + 2); grid.appendChild(lbl); }
            names.forEach(function (d, c) { var col = h("div", "oui-week__col"); col.style.gridColumn = String(c + 2); col.style.gridRow = "2 / span " + hours; grid.appendChild(col); });
            function mins(t) { var p = (t || "0:0").split(":"); return parseInt(p[0], 10) * 60 + (parseInt(p[1], 10) || 0); }
            rows(events).forEach(function (r, idx) {
                var day = parseInt(r[0], 10), s = mins(r[1]), e = mins(r[2]);
                if (!(day >= 1 && day <= names.length)) { return; }
                var ev = h("button", "oui-week__event oui-week__event--" + (r[4] || "accent"));
                ev.type = "button";
                ev.style.gridColumn = String(day + 1);
                ev.style.top = "calc(" + ((s - from * 60) / (hours * 60) * 100) + "% )";
                ev.style.height = "calc(" + (Math.max(20, e - s) / (hours * 60) * 100) + "% - 3px)";
                ev.appendChild(h("span", "oui-week__event-title", r[3] || ""));
                ev.appendChild(h("span", "oui-week__event-time", r[1] + " - " + r[2]));
                ev.setAttribute("aria-label", (r[3] || "") + ", " + names[day - 1] + " " + r[1] + " to " + r[2]);
                ev.addEventListener("click", function () { onPick(idx); });
                grid.querySelectorAll(".oui-week__col")[day - 1].appendChild(ev);
            });
            var now = new Date(), nowDay = (now.getDay() + 6) % 7, nowMin = now.getHours() * 60 + now.getMinutes();
            if (nowDay < names.length && nowMin >= from * 60 && nowMin <= to * 60) { var line = h("div", "oui-week__now"); line.style.top = ((nowMin - from * 60) / (hours * 60) * 100) + "%"; grid.querySelectorAll(".oui-week__col")[nowDay].appendChild(line); }
            root.appendChild(grid);
        }
    };

    // Gantt: tasks "Name|Start(yyyy-mm-dd)|End|Progress(0-100)|DependsOn(index, 1-based);..."
    OUI.gantt = {
        render: function (id, tasks) {
            var root = byId(id); if (!root) { return; }
            while (root.firstChild) { root.removeChild(root.firstChild); }
            var list = rows(tasks).map(function (r) { return { name: r[0], s: new Date(r[1] + "T00:00:00"), e: new Date(r[2] + "T00:00:00"), p: parseFloat(r[3]) || 0, dep: parseInt(r[4], 10) || 0 }; });
            if (!list.length) { return; }
            var min = new Date(Math.min.apply(null, list.map(function (t) { return t.s; }))), max = new Date(Math.max.apply(null, list.map(function (t) { return t.e; })));
            min.setDate(min.getDate() - 1); max.setDate(max.getDate() + 2);
            var span = (max - min) / 864e5, rowH = 36, labelW = 170, w = Math.max(560, root.clientWidth || 800), cw = w - labelW, H = 28 + list.length * rowH;
            var svg = sv("svg", { viewBox: "0 0 " + w + " " + H, class: "oui-gantt__svg", role: "img", "aria-label": "Plan" }, root);
            function x(d) { return labelW + (d - min) / 864e5 / span * cw; }
            for (var d = new Date(min); d <= max; d.setDate(d.getDate() + 1)) {
                var gx = x(d), weekend = d.getDay() === 0 || d.getDay() === 6;
                if (weekend) { sv("rect", { x: gx, y: 24, width: cw / span, height: H - 24, class: "oui-gantt__weekend" }, svg); }
                if (d.getDate() === 1 || span < 40) { sv("line", { x1: gx, x2: gx, y1: 20, y2: H, class: "oui-gantt__tick" }, svg); var t = sv("text", { x: gx + 3, y: 14, class: "oui-gantt__axis" }, svg); t.textContent = span < 40 ? String(d.getDate()) : d.toLocaleString(undefined, { month: "short" }); }
            }
            var today = new Date(); if (today >= min && today <= max) { sv("line", { x1: x(today), x2: x(today), y1: 20, y2: H, class: "oui-gantt__today" }, svg); }
            list.forEach(function (t, i) {
                var y = 28 + i * rowH, name = sv("text", { x: 4, y: y + rowH / 2 + 4, class: "oui-gantt__name" }, svg); name.textContent = t.name;
                var bx = x(t.s), bw = Math.max(6, x(t.e) - bx);
                sv("rect", { x: bx, y: y + 8, width: bw, height: rowH - 16, rx: 6, class: "oui-gantt__bar" }, svg);
                sv("rect", { x: bx, y: y + 8, width: bw * Math.min(1, t.p / 100), height: rowH - 16, rx: 6, class: "oui-gantt__done" }, svg);
                if (t.dep > 0 && list[t.dep - 1]) { var p = list[t.dep - 1], py = 28 + (t.dep - 1) * rowH + rowH / 2, px = x(p.e); var ty = y + rowH / 2, top = y + 8, dep = bx >= px + 10 ? "M" + px + "," + py + " C" + (px + 18) + "," + py + " " + (bx - 18) + "," + ty + " " + bx + "," + ty : "M" + Math.min(px - 8, bx + 10) + "," + (py + rowH / 2 - 8) + " V" + top; sv("path", { d: dep, class: "oui-gantt__dep" }, svg); }
                var tt = document.createElementNS(NS, "title"); tt.textContent = t.name + ": " + t.p + "%"; svg.lastChild.appendChild(tt);
            });
        }
    };

    // Node editor: nodes "id|label|x|y|kind;...", links "a>b;..." Drag nodes; drag from an output to an input to link.
    OUI.nodes = {
        mount: function (id, nodesText, linksText, onChange) {
            var root = byId(id); if (!root) { return; }
            while (root.firstChild) { root.removeChild(root.firstChild); }
            var nodes = rows(nodesText).map(function (r) { return { id: r[0], label: r[1] || r[0], x: parseFloat(r[2]) || 0, y: parseFloat(r[3]) || 0, kind: r[4] || "" }; });
            var links = (linksText || "").split(";").map(function (l) { var p = l.split(">"); return p.length === 2 ? { a: p[0].trim(), b: p[1].trim() } : null; }).filter(Boolean);
            var view = { x: 0, y: 0, k: 1 }, canvas = h("div", "oui-nodes__canvas"), svg = sv("svg", { class: "oui-nodes__wires" }), els = {};
            root.appendChild(canvas); canvas.appendChild(svg);
            function apply() { canvas.style.transform = "translate(" + view.x + "px," + view.y + "px) scale(" + view.k + ")"; root.style.backgroundPosition = view.x + "px " + view.y + "px"; root.style.backgroundSize = 20 * view.k + "px " + 20 * view.k + "px"; }
            function port(n, out) { return { x: n.x + (out ? 180 : 0), y: n.y + 38 }; }
            function wires() {
                while (svg.firstChild) { svg.removeChild(svg.firstChild); }
                links.forEach(function (l) {
                    var a = nodes.filter(function (n) { return n.id === l.a; })[0], b = nodes.filter(function (n) { return n.id === l.b; })[0]; if (!a || !b) { return; }
                    var p = port(a, true), q = port(b, false), dx = Math.max(40, Math.abs(q.x - p.x) / 2);
                    sv("path", { d: "M" + p.x + "," + p.y + " C" + (p.x + dx) + "," + p.y + " " + (q.x - dx) + "," + q.y + " " + q.x + "," + q.y, class: "oui-nodes__wire" }, svg);
                });
            }
            function emit() { onChange(nodes.map(function (n) { return [n.id, n.label, Math.round(n.x), Math.round(n.y), n.kind].join("|"); }).join(";"), links.map(function (l) { return l.a + ">" + l.b; }).join(";")); }
            nodes.forEach(function (n) {
                var el = h("div", "oui-node oui-node--" + (n.kind || "default")); el.style.left = n.x + "px"; el.style.top = n.y + "px"; el.tabIndex = 0;
                el.appendChild(h("div", "oui-node__title", n.label)); el.appendChild(h("div", "oui-node__kind", n.kind || "step"));
                var pin = h("span", "oui-node__port oui-node__port--in"), pout = h("span", "oui-node__port oui-node__port--out");
                pin.setAttribute("data-port-in", n.id); pout.setAttribute("data-port-out", n.id); el.appendChild(pin); el.appendChild(pout);
                canvas.appendChild(el); els[n.id] = el;
                var sx, sy, ox, oy, moving = false;
                el.addEventListener("pointerdown", function (e) { if (e.target.closest("[data-port-out]")) { return; } e.stopPropagation(); moving = true; sx = e.clientX; sy = e.clientY; ox = n.x; oy = n.y; el.setPointerCapture(e.pointerId); el.classList.add("is-moving"); });
                el.addEventListener("pointermove", function (e) { if (!moving) { return; } n.x = ox + (e.clientX - sx) / view.k; n.y = oy + (e.clientY - sy) / view.k; el.style.left = n.x + "px"; el.style.top = n.y + "px"; wires(); });
                el.addEventListener("pointerup", function () { if (moving) { moving = false; el.classList.remove("is-moving"); emit(); } });
                el.addEventListener("keydown", function (e) { var step = e.shiftKey ? 20 : 5, d = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[e.key]; if (d) { e.preventDefault(); n.x += d[0]; n.y += d[1]; el.style.left = n.x + "px"; el.style.top = n.y + "px"; wires(); emit(); } });
            });
            var linking = null, temp = null;
            root.addEventListener("pointerdown", function (e) {
                var out = e.target.closest("[data-port-out]");
                if (out) { e.stopPropagation(); linking = out.getAttribute("data-port-out"); temp = sv("path", { class: "oui-nodes__wire is-temp" }, svg); root.setPointerCapture(e.pointerId); return; }
                if (e.target === root || e.target === canvas || e.target === svg) { var sx = e.clientX, sy = e.clientY, vx = view.x, vy = view.y; root._pan = function (ev) { view.x = vx + ev.clientX - sx; view.y = vy + ev.clientY - sy; apply(); }; root.setPointerCapture(e.pointerId); root.classList.add("is-panning"); }
            });
            root.addEventListener("pointermove", function (e) {
                if (root._pan) { root._pan(e); return; }
                if (linking && temp) { var a = nodes.filter(function (n) { return n.id === linking; })[0], p = port(a, true), r = root.getBoundingClientRect(), qx = (e.clientX - r.left - view.x) / view.k, qy = (e.clientY - r.top - view.y) / view.k; temp.setAttribute("d", "M" + p.x + "," + p.y + " C" + (p.x + 60) + "," + p.y + " " + (qx - 60) + "," + qy + " " + qx + "," + qy); }
            });
            root.addEventListener("pointerup", function (e) {
                if (root._pan) { root._pan = null; root.classList.remove("is-panning"); return; }
                if (linking) { var target = document.elementFromPoint(e.clientX, e.clientY), inp = target && target.closest("[data-port-in]"); if (inp && inp.getAttribute("data-port-in") !== linking) { links.push({ a: linking, b: inp.getAttribute("data-port-in") }); emit(); } linking = null; if (temp) { temp.remove(); temp = null; } wires(); }
            });
            root.addEventListener("wheel", function (e) { e.preventDefault(); var r = root.getBoundingClientRect(), mx = e.clientX - r.left, my = e.clientY - r.top, k = Math.max(.4, Math.min(2, view.k * (e.deltaY < 0 ? 1.1 : .9))); view.x = mx - (mx - view.x) * k / view.k; view.y = my - (my - view.y) * k / view.k; view.k = k; apply(); }, { passive: false });
            // Fit on start: when the nodes do not fit the box, scale the view down (not below .5) and centre them.
            function fit() {
                var W = root.clientWidth, H = root.clientHeight; if (!W || !H || !nodes.length) { return; }
                var x0 = Math.min.apply(null, nodes.map(function (n) { return n.x; })), y0 = Math.min.apply(null, nodes.map(function (n) { return n.y; }));
                var x1 = Math.max.apply(null, nodes.map(function (n) { return n.x + 184; })), y1 = Math.max.apply(null, nodes.map(function (n) { return n.y + 76; }));
                var k = Math.max(.5, Math.min(1, (W - 32) / (x1 - x0), (H - 32) / (y1 - y0)));
                if (k >= 1 && x1 <= W - 8 && y1 <= H - 8) { return; }
                view.k = k; view.x = (W - (x1 - x0) * k) / 2 - x0 * k; view.y = (H - (y1 - y0) * k) / 2 - y0 * k; apply();
            }
            apply(); wires(); requestAnimationFrame(fit);
        }
    };

    // App shell (OuiLayoutSide): buttons with data-oui-shell="nav" (phone drawer), "collapse" (fold the menu to icons,
    // remembered per browser), "aside" (the right panel) and "close" (the scrim). Escape closes the drawer and the
    // panel; a menu item clicked in the phone drawer closes it. The panel starts open on wide screens when asked.
    OUI.shell = {
        mount: function (id) {
            var root = byId(id); if (!root) { return; }
            function read(k) { try { return window.localStorage.getItem(k); } catch (e) { return null; } }
            function write(k, v) { try { window.localStorage.setItem(k, v); } catch (e) { } }
            function phone() { return window.matchMedia("(max-width: 767px)").matches; }
            function sync() {
                root.querySelectorAll('[data-oui-shell="nav"]').forEach(function (b) { b.setAttribute("aria-expanded", root.classList.contains("is-nav-open") ? "true" : "false"); });
                root.querySelectorAll('[data-oui-shell="aside"]').forEach(function (b) { b.setAttribute("aria-expanded", root.classList.contains("is-aside-open") ? "true" : "false"); });
                root.querySelectorAll('[data-oui-shell="collapse"]').forEach(function (b) { b.setAttribute("aria-pressed", root.classList.contains("is-collapsed") ? "true" : "false"); });
            }
            if (read("oui-shell-collapsed") === "1") { root.classList.add("is-collapsed"); }
            if (root.getAttribute("data-aside-open") === "true" && window.matchMedia("(min-width: 1280px)").matches) { root.classList.add("is-aside-open"); }
            function click(e) {
                var t = e.target.closest("[data-oui-shell]");
                if (t && root.contains(t)) {
                    var kind = t.getAttribute("data-oui-shell");
                    if (kind === "nav") { root.classList.toggle("is-nav-open"); }
                    else if (kind === "aside") { root.classList.toggle("is-aside-open"); }
                    else if (kind === "collapse") { root.classList.toggle("is-collapsed"); write("oui-shell-collapsed", root.classList.contains("is-collapsed") ? "1" : "0"); }
                    else if (kind === "close") { root.classList.remove("is-nav-open", "is-aside-open"); }
                    sync(); return;
                }
                if (phone() && root.classList.contains("is-nav-open") && e.target.closest(".oui-shell__nav .oui-nav-item")) { root.classList.remove("is-nav-open"); sync(); }
            }
            function key(e) { if (e.key === "Escape" && (root.classList.contains("is-nav-open") || (root.classList.contains("is-aside-open") && !window.matchMedia("(min-width: 1025px)").matches))) { root.classList.remove("is-nav-open", "is-aside-open"); sync(); } }
            // Side panel width: drag the left edge, arrow keys on the handle, double click for the default. Desktop only.
            var aside = root.querySelector(".oui-shell__aside"), handle = null, desk = window.matchMedia("(min-width: 1025px)");
            function clamp(w) { return Math.round(Math.max(300, Math.min(Math.min(860, window.innerWidth * 0.62), w))); }
            function applyWidth() {
                var w = parseInt(read("oui-shell-aside-w") || "", 10);
                if (desk.matches && w) { root.style.setProperty("--oui-shell-aside", clamp(w) + "px"); } else { root.style.removeProperty("--oui-shell-aside"); }
                if (handle && aside) { handle.setAttribute("aria-valuenow", String(Math.round(aside.getBoundingClientRect().width))); }
            }
            function setWidth(w) { write("oui-shell-aside-w", String(clamp(w))); applyWidth(); }
            function current() { return parseInt(read("oui-shell-aside-w") || "", 10) || aside.getBoundingClientRect().width; }
            var dragging = false;
            function downH(e) { dragging = true; handle.setPointerCapture(e.pointerId); root.classList.add("is-resizing"); e.preventDefault(); }
            function moveH(e) { if (dragging) { setWidth(root.getBoundingClientRect().right - e.clientX); } }
            function upH() { dragging = false; root.classList.remove("is-resizing"); }
            function keyH(e) { var d = e.key === "ArrowLeft" ? 24 : e.key === "ArrowRight" ? -24 : 0; if (d) { setWidth(current() + d); e.preventDefault(); } }
            function resetH() { try { window.localStorage.removeItem("oui-shell-aside-w"); } catch (e) { } applyWidth(); }
            if (aside) {
                handle = document.createElement("div"); handle.className = "oui-shell__resize"; handle.tabIndex = 0; handle.title = "Drag to resize";
                handle.setAttribute("role", "separator"); handle.setAttribute("aria-orientation", "vertical"); handle.setAttribute("aria-label", "Resize the side panel");
                aside.appendChild(handle);
                handle.addEventListener("pointerdown", downH); handle.addEventListener("pointermove", moveH); handle.addEventListener("pointerup", upH); handle.addEventListener("pointercancel", upH);
                handle.addEventListener("keydown", keyH); handle.addEventListener("dblclick", resetH);
                window.addEventListener("resize", applyWidth); applyWidth();
            }
            root.addEventListener("click", click); document.addEventListener("keydown", key); sync();
            keep(root, function () { root.removeEventListener("click", click); document.removeEventListener("keydown", key); window.removeEventListener("resize", applyWidth); if (handle && handle.parentNode) { handle.parentNode.removeChild(handle); } });
        }
    };

    // Selection to AI: select text (in the page, a text area or a text input) inside Scope, a CSS selector (the whole
    // page when empty), and a small bar offers "Send to AI" and quick actions; onSend(text, action, targetId).
    // targetId is the nearest data-ai-target value, else the nearest id, around the selection: where an answer can go.
    // The bar is a manual popover (top layer), so no ancestor clips it; it keeps the selection while it is pressed.
    OUI.selectionAsk = {
        mount: function (id, scope, minLength, onSend) {
            var bar = byId(id); if (!bar) { return; }
            var current = null, timer = null;
            function within(node) { var el = node && (node.nodeType === 1 ? node : node.parentElement); return !!el && !bar.contains(el) && (!scope || !!el.closest(scope)); }
            function targetOf(node) {
                var el = node && (node.nodeType === 1 ? node : node.parentElement), t = el && el.closest("[data-ai-target]");
                if (t) { return t.getAttribute("data-ai-target"); }
                t = el && el.closest("[id]"); return t ? t.id : "";
            }
            function read() {
                var a = document.activeElement;
                if (a && (a.tagName === "TEXTAREA" || (a.tagName === "INPUT" && /^(text|search|email|url)$/i.test(a.type))) && a.selectionEnd > a.selectionStart) {
                    var picked = a.value.slice(a.selectionStart, a.selectionEnd);
                    return within(a) && picked.trim().length >= minLength ? { text: picked, rect: a.getBoundingClientRect(), target: targetOf(a) } : null;
                }
                var s = window.getSelection(); if (!s || s.isCollapsed || !s.rangeCount) { return null; }
                var text = s.toString().trim(), range = s.getRangeAt(0);
                if (text.length < minLength || !within(range.commonAncestorContainer)) { return null; }
                var rects = range.getClientRects();
                return { text: text, rect: rects.length ? rects[rects.length - 1] : range.getBoundingClientRect(), target: targetOf(range.commonAncestorContainer) };
            }
            function hide() { current = null; bar.classList.remove("is-open"); try { if (bar.matches(":popover-open")) { bar.hidePopover(); } } catch (e) { } }
            function show() {
                var sel = read(); if (!sel) { hide(); return; }
                current = sel;
                try { if (!bar.matches(":popover-open")) { bar.showPopover(); } } catch (e) { }
                var w = bar.offsetWidth, hgt = bar.offsetHeight, r = sel.rect;
                var x = Math.min(window.innerWidth - w - 8, Math.max(8, r.right - w / 2)), y = r.bottom + 8;
                if (y + hgt > window.innerHeight - 8) { y = Math.max(8, r.top - hgt - 8); }
                bar.style.left = Math.round(x) + "px"; bar.style.top = Math.round(y) + "px";
                requestAnimationFrame(function () { bar.classList.add("is-open"); });
            }
            function later() { clearTimeout(timer); timer = setTimeout(show, 160); }
            function down(e) { if (!bar.contains(e.target)) { hide(); } }
            function key(e) { if (e.key === "Escape") { hide(); } else if (e.shiftKey || e.key === "Shift" || (e.ctrlKey && (e.key === "a" || e.key === "A"))) { later(); } }
            function press(e) { e.preventDefault(); }
            function click(e) {
                var b = e.target.closest("[data-oui-ask]"); if (!b || !current) { return; }
                var c = current; hide();
                if (window.getSelection) { window.getSelection().removeAllRanges(); }
                onSend(c.text, b.getAttribute("data-oui-ask"), c.target);
            }
            document.addEventListener("mouseup", later); document.addEventListener("keyup", key);
            document.addEventListener("pointerdown", down, true); window.addEventListener("scroll", hide, true); window.addEventListener("resize", hide);
            bar.addEventListener("mousedown", press); bar.addEventListener("click", click);
            keep(bar, function () {
                clearTimeout(timer);
                document.removeEventListener("mouseup", later); document.removeEventListener("keyup", key);
                document.removeEventListener("pointerdown", down, true); window.removeEventListener("scroll", hide, true); window.removeEventListener("resize", hide);
                bar.removeEventListener("mousedown", press); bar.removeEventListener("click", click); hide();
            });
        }
    };

    // Chat composer: Enter sends, Shift+Enter makes a new line, the text area grows.
    OUI.composer = {
        mount: function (id, onSend) {
            var root = byId(id); if (!root) { return; }
            var area = root.querySelector("textarea"); if (!area) { return; }
            function fit() { area.style.height = "auto"; area.style.height = Math.min(200, area.scrollHeight) + "px"; }
            function key(e) { if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); onSend(); } }
            area.addEventListener("keydown", key); area.addEventListener("input", fit); fit();
            keep(root, function () { area.removeEventListener("keydown", key); area.removeEventListener("input", fit); });
        },
        focus: function (id) { var root = byId(id); var area = root && root.querySelector("textarea"); if (area) { area.style.height = "auto"; area.focus(); } }
    };

    // Scrolls a chat thread to the newest message when the user is already near the bottom.
    OUI.stickBottom = function (id) { var el = byId(id); if (el && el.scrollHeight - el.scrollTop - el.clientHeight < 160) { el.scrollTo({ top: el.scrollHeight, behavior: "smooth" }); } };
})();

/* Chat threads stay at the newest message: when first shown, and when a message arrives while the reader is at the end. */
(function () {
    function stick(thread) {
        if (thread.__ouiStick) { return; }
        thread.__ouiStick = true;
        var atEnd = true;
        thread.addEventListener("scroll", function () { atEnd = thread.scrollHeight - thread.scrollTop - thread.clientHeight < 40; });
        new MutationObserver(function () { if (atEnd) { thread.scrollTop = thread.scrollHeight; } }).observe(thread, { childList: true, subtree: true });
        thread.scrollTop = thread.scrollHeight;
    }
    var queued = false;
    function scan() { queued = false; document.querySelectorAll(".oui-thread").forEach(stick); }
    new MutationObserver(function () { if (!queued) { queued = true; requestAnimationFrame(scan); } }).observe(document.documentElement, { childList: true, subtree: true });
    scan();
})();

/* AI targets: when the text inside a [data-ai-target] element changes after it was shown (an AI edit written back),
   it gets .is-ai-updated for a moment, so the reader sees what changed. The first render does not flash. */
(function () {
    var seen = new WeakMap();
    function flash(el) {
        el.classList.remove("is-ai-updated"); void el.offsetWidth; el.classList.add("is-ai-updated");
        clearTimeout(el.__ouiFlash); el.__ouiFlash = setTimeout(function () { el.classList.remove("is-ai-updated"); }, 2000);
    }
    new MutationObserver(function (records) {
        var now = Date.now();
        records.forEach(function (r) {
            var node = r.target.nodeType === 1 ? r.target : r.target.parentElement;
            var el = node && node.closest("[data-ai-target]"); if (!el) { return; }
            var first = seen.get(el); if (first === undefined) { seen.set(el, now); return; }
            if (now - first > 800 && el.textContent !== el.__ouiText) { flash(el); }
            el.__ouiText = el.textContent;
        });
        document.querySelectorAll("[data-ai-target]").forEach(function (el) { if (!seen.has(el)) { seen.set(el, now); el.__ouiText = el.textContent; } });
    }).observe(document.documentElement, { childList: true, subtree: true, characterData: true });
})();


/* OUI.agent: a chat model that can call MCP tools. The loop runs in the browser, one short server call per step; keys stay on
   the server. o: transcript (JSON text, the provider's own messages), text (the user's message), and functions that return
   promises: list() -> {Json, Provider, IsError, Error}; step(messagesJson, toolsJson) -> {Text, Model, InputTokens,
   OutputTokens, IsError, Error}; call(name, argumentsJson) -> {Json, IsError, Error}; onToolStart(name, argumentsJson);
   onToolEnd(output, status). Resolves to {text, transcript, usage}. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    // ASCII-only JSON: the HTTP layers between here and the provider cannot change the text's meaning, whatever encoding they assume.
    function ascii(value) { return JSON.stringify(value).replace(/[\u007f-\uffff]/g, function (c) { return "\\u" + ("0000" + c.charCodeAt(0).toString(16)).slice(-4); }); }
    function text(v) { return typeof v === "string" ? v : Array.isArray(v) ? v.map(function (x) { return x && x.text ? x.text : ""; }).join("") : ""; }
    function toolDefs(provider, tools) {
        return tools.map(function (t) {
            return provider === "anthropic" ? { name: t.name, description: t.description || "", input_schema: t.inputSchema || { type: "object" } }
                : { type: "function", "function": { name: t.name, description: t.description || "", parameters: t.inputSchema || { type: "object" } } };
        });
    }
    function toolText(raw) {
        var doc = JSON.parse(raw);
        if (doc.error) { return { text: doc.error.message || "Tool error", isError: true }; }
        if (!doc.result) { doc = { result: doc }; }   // ToolRun answers with the result itself, a JSON-RPC server wraps it
        var extras = (doc.result.content || []).map(function (c) {
            var r = c.resource;
            if (c.type === "resource_link") { return "[" + (c.name || c.uri) + "](" + c.uri + ")"; }
            if (c.type === "resource" && r) {
                var name = (r.uri || "file").split(/[?#]/)[0].split("/").pop() || "file";
                return "```file:" + name.replace(/\s+/g, "_") + (r.blob ? " base64" : "") + "\n" + (r.blob || r.text || "") + "\n```";
            }
            return "";
        }).filter(Boolean);
        return { text: (doc.result.content || []).map(function (c) { return c.text || ""; }).join("\n"), isError: !!doc.result.isError, extras: extras };
    }
    // The user's message with its files, in the provider's own format. Text files become part of the text.
    function userContent(provider, message, files) {
        var body = message;
        files.filter(function (f) { return f.kind === "text"; }).forEach(function (f) { body += "\n\n--- " + f.name + " ---\n" + f.data; });
        var binary = files.filter(function (f) { return f.kind !== "text"; });
        if (!binary.length) { return body; }
        var parts = binary.map(function (f) {
            if (provider === "anthropic") { return { type: f.kind === "pdf" ? "document" : "image", source: { type: "base64", media_type: f.mime, data: f.data.slice(f.data.indexOf(",") + 1) } }; }
            return f.kind === "pdf" ? { type: "document_url", document_url: f.data } : { type: "image_url", image_url: f.data };
        });
        parts.push({ type: "text", text: body });
        return parts;
    }
    var stopped = false, pendingAnswer = null;
    // One model turn from an OpenAI-style event stream, in the shape of a normal answer. onDelta gets the text so far, at most every 80 ms.
    function parseStream(onLine) {
        var buffer = "";
        return function (chunk, flush) {
            buffer += chunk; var parts = buffer.split("\n"); buffer = flush ? "" : parts.pop();
            parts.forEach(function (l) { if (l.indexOf("data:") === 0) { var d = l.slice(5).trim(); if (d && d !== "[DONE]") { onLine(JSON.parse(d)); } } });
        };
    }
    function collector(onDelta) {
        var msg = { role: "assistant", content: "", tool_calls: [] }, usage = { prompt_tokens: 0, completion_tokens: 0 }, last = 0;
        return {
            add: function (ev) {
                if (ev.usage) { usage = ev.usage; }
                var d = ev.choices && ev.choices[0] && ev.choices[0].delta; if (!d) { return; }
                if (typeof d.content === "string" && d.content) { msg.content += d.content; if (onDelta && Date.now() - last > 80) { last = Date.now(); onDelta(msg.content); } }
                (d.tool_calls || []).forEach(function (t) {
                    var slot = msg.tool_calls[t.index || 0] = msg.tool_calls[t.index || 0] || { id: "", type: "function", "function": { name: "", arguments: "" } };
                    if (t.id) { slot.id = t.id; } if (t["function"]) { slot["function"].name += t["function"].name || ""; slot["function"].arguments += t["function"].arguments || ""; }
                });
            },
            done: function () { if (onDelta && msg.content) { onDelta(msg.content); } if (!msg.tool_calls.length) { delete msg.tool_calls; } return { choices: [{ message: msg }], usage: usage }; }
        };
    }
    function streamOpenAi(url, body, onDelta) {
        return fetch(url, { method: "POST", credentials: "include", headers: { "Content-Type": "application/json", Accept: "text/event-stream" }, body: body }).then(function (res) {
            if (!res.ok) { throw new Error(res.status + " " + res.statusText); }
            var reader = res.body.getReader(), dec = new TextDecoder(), c = collector(onDelta), feed = parseStream(c.add);
            function pump() { return reader.read().then(function (r) { if (r.done) { feed("\n", true); return c.done(); } feed(dec.decode(r.value, { stream: true })); return pump(); }); }
            return pump();
        });
    }
    OUI.agent = {
        _stream: { parseStream: parseStream, collector: collector },
        stop: function () { stopped = true; if (pendingAnswer) { var a = pendingAnswer; pendingAnswer = null; a(false); } },
        answer: function (yes) { if (pendingAnswer) { var a = pendingAnswer; pendingAnswer = null; a(!!yes); } },
        // The transcript without the latest user message and all that followed: for edit and resend.
        dropLastUser: function (json) {
            var t = json ? JSON.parse(json) : [], k = t.length - 1;
            while (k >= 0 && !(t[k].role === "user" && !(Array.isArray(t[k].content) && t[k].content.some(function (c) { return c.type === "tool_result"; })))) { k--; }
            return JSON.stringify(k >= 0 ? t.slice(0, k) : t);
        },
        // The text of the page's AI targets, for the first words of a message.
        pageContext: function () { return Array.prototype.map.call(document.querySelectorAll("[data-ai-target]"), function (n) { return n.innerText.trim(); }).filter(Boolean).join("\n\n").slice(0, 6000); },
        run: function (o) {
            stopped = false; pendingAnswer = null;
            var meta = {}, attachments = [], transcript = o.transcript ? JSON.parse(o.transcript) : [], provider = "mistral", defs = [], notice = "", rounds = 0, input = 0, output = 0, model = "", max = o.maxRounds || 6;
            function approve(name, argsJson) {
                if (!o.onApproval) { return Promise.resolve(false); }
                return Promise.resolve(o.onApproval(name, argsJson)).then(function () { return new Promise(function (done) { pendingAnswer = done; }); });
            }
            function runChain(name, argsJson) {
                var previous = "", i = 1;
                function next() {
                    if (stopped) { return Promise.resolve({ text: "Stopped by the user.", isError: true }); }
                    return Promise.resolve(o.chainNext(name, i, argsJson, previous)).then(function (st) {
                        if (st.Done) { return { text: previous || "The chain finished.", isError: false }; }
                        return callOne(st.ToolName, st.ArgumentsJson, st.NeedsApproval).then(function (res) { if (res.isError) { return res; } previous = res.text; i++; return next(); });
                    });
                }
                return next();
            }
            function runTool(name, args) {
                var argsJson = ascii(typeof args === "string" ? (args ? JSON.parse(args) : {}) : (args || {})), m = meta[name] || {};
                return m.chain ? runChain(name, argsJson) : callOne(name, argsJson, m.approval);
            }
            function callOne(name, argsJson, needsApproval) {
                var gate = needsApproval ? approve(name, argsJson) : Promise.resolve(true);
                return gate.then(function (yes) {
                    if (!yes) { return Promise.resolve(o.onToolStart(name, argsJson)).then(function () { return o.onToolEnd("The user declined.", "failed"); }).then(function () { return { text: "The user declined this action.", isError: true }; }); }
                    return callApproved(name, argsJson, !!needsApproval);
                });
            }
            function callApproved(name, argsJson, approved) {
                return Promise.resolve(o.onToolStart(name, argsJson)).then(function () { return o.call(name, argsJson, approved); }).then(function (r) {
                    var res = r.IsError ? { text: r.Error, isError: true } : toolText(r.Json);
                    attachments = attachments.concat(res.extras || []);
                    return Promise.resolve(o.onToolEnd(res.text, res.isError ? "failed" : "done")).then(function () { return res; });
                }, function (e) { var res = { text: (e && e.message) || "The tool failed", isError: true }; return Promise.resolve(o.onToolEnd(res.text, "failed")).then(function () { return res; }); });
            }
            function sequence(items, fn) { var out = []; return items.reduce(function (p, it) { return p.then(function () { return fn(it); }).then(function (r) { out.push(r); }); }, Promise.resolve()).then(function () { return out; }); }
            function finish(answer) { return { text: [answer].concat(attachments).join("\n\n"), transcript: JSON.stringify(transcript), usage: model + " · " + input + " → " + output + " tokens" + (rounds > 1 ? " · " + rounds + " steps" : "") + notice }; }
            function modelStep() {
                if (provider === "anthropic" || !o.streamUrl) { return Promise.resolve(o.step(ascii(transcript), ascii(defs))); }
                return Promise.resolve(o.streamBody(ascii(transcript), ascii(defs))).then(function (b) {
                    if (b.IsError) { return b; }
                    return streamOpenAi(o.streamUrl, b.Text, o.onDelta).then(function (doc) {
                        if (o.logUsage) { o.logUsage(b.Model, doc.usage.prompt_tokens, doc.usage.completion_tokens); }
                        return { Text: JSON.stringify(doc), Model: b.Model, InputTokens: doc.usage.prompt_tokens, OutputTokens: doc.usage.completion_tokens, IsError: false, Error: "" };
                    });
                });
            }
            function round() {
                rounds++;
                if (stopped) { return Promise.resolve(finish("Stopped.")); }
                return modelStep().then(function (r) {
                    var res = r; if (res.IsError) { throw new Error(res.Error); }
                    var doc = JSON.parse(res.Text); model = res.Model; input += res.InputTokens; output += res.OutputTokens;
                    if (provider === "anthropic") {
                        var blocks = doc.content || [], uses = blocks.filter(function (b) { return b.type === "tool_use"; });
                        transcript.push({ role: "assistant", content: blocks });
                        var said = blocks.filter(function (b) { return b.type === "text"; }).map(function (b) { return b.text; }).join("");
                        if (!uses.length || rounds >= max) { return finish(said); }
                        return sequence(uses, function (u) { return runTool(u.name, u.input).then(function (x) { return { type: "tool_result", tool_use_id: u.id, content: x.text, is_error: x.isError }; }); })
                            .then(function (results) { transcript.push({ role: "user", content: results }); return round(); });
                    }
                    var msg = doc.choices[0].message, calls = msg.tool_calls || [];
                    var keep = { role: "assistant", content: msg.content || "" }; if (calls.length) { keep.tool_calls = calls; } transcript.push(keep);
                    if (!calls.length || rounds >= max) { return finish(text(msg.content)); }
                    return sequence(calls, function (c) { return runTool(c["function"].name, c["function"].arguments).then(function (x) { transcript.push({ role: "tool", name: c["function"].name, tool_call_id: c.id, content: x.text }); }); }).then(round);
                });
            }
            return Promise.resolve(o.list()).then(function (l) {
                provider = l.Provider || provider;
                if (l.IsError) { notice = " · tools unavailable: " + l.Error; }
                else {
                    var all = JSON.parse(l.Json), tools = ((all.result || all).tools || []).filter(function (t) { return (o.deny || []).indexOf(t.name) < 0; });
                    tools.forEach(function (t) { meta[t.name] = t._meta || {}; });
                    defs = toolDefs(provider, tools);
                }
                if (o.retry) {
                    var k = transcript.length - 1;
                    while (k >= 0 && !(transcript[k].role === "user" && !(Array.isArray(transcript[k].content) && transcript[k].content.some(function (c) { return c.type === "tool_result"; })))) { k--; }
                    transcript = transcript.slice(0, k + 1);
                } else { transcript.push({ role: "user", content: userContent(provider, o.text, o.files || []) }); }
                return round();
            });
        }
    };
})();


/* OUI.attach: files for a chat message, kept in the browser until take(). */
(function () {
    var OUI = window.OUI = window.OUI || {};
    var current = null;
    function kindOf(f) {
        if (/^image\/(png|jpe?g|gif|webp)$/i.test(f.type)) { return "image"; }
        if (f.type === "application/pdf" || /\.pdf$/i.test(f.name)) { return "pdf"; }
        if (/^text\//i.test(f.type) || /\.(txt|md|csv|json|xml|log)$/i.test(f.name)) { return "text"; }
        return null;
    }
    OUI.attach = {
        mount: function (id, accept, maxMb, maxFiles) {
            var root = document.getElementById(id); if (!root) { return; }
            var state = { files: [], chips: null, error: null }; current = state;
            var composer = root.closest(".oui-composer"), host = composer || root.parentNode;
            var box = document.createElement("div"); box.className = "oui-attach__chips";
            var err = document.createElement("div"); err.className = "oui-attach__error";
            host.insertBefore(err, host.firstChild); host.insertBefore(box, host.firstChild);
            var input = document.createElement("input"); input.type = "file"; input.multiple = true; input.accept = accept || ""; input.hidden = true; root.appendChild(input);
            function say(t) { err.textContent = t || ""; }
            function draw() {
                while (box.firstChild) { box.removeChild(box.firstChild); }
                state.files.forEach(function (f, i) {
                    var chip = document.createElement("div"); chip.className = "oui-attach__chip";
                    if (f.kind === "image") { var img = document.createElement("img"); img.className = "oui-attach__thumb"; img.alt = ""; img.src = f.data; chip.appendChild(img); }
                    else { var k = document.createElement("span"); k.className = "oui-attach__kind"; k.textContent = (f.name.split(".").pop() || f.kind).slice(0, 4); chip.appendChild(k); }
                    var n = document.createElement("span"); n.className = "oui-attach__name"; n.textContent = f.name; n.title = f.name; chip.appendChild(n);
                    var x = document.createElement("button"); x.type = "button"; x.className = "oui-attach__remove"; x.textContent = "\u00d7"; x.setAttribute("aria-label", "Remove " + f.name);
                    x.addEventListener("click", function () { state.files.splice(i, 1); draw(); }); chip.appendChild(x);
                    box.appendChild(chip);
                });
            }
            state.draw = draw; state.say = say;
            function add(list) {
                say("");
                Array.prototype.forEach.call(list, function (f) {
                    var kind = kindOf(f);
                    if (!kind) { say(f.name + ": this file type is not supported."); return; }
                    if (f.size > maxMb * 1048576) { say(f.name + " is larger than " + maxMb + " MB."); return; }
                    if (state.files.length >= maxFiles) { say("At most " + maxFiles + " files on one message."); return; }
                    var entry = { name: f.name, mime: f.type || (kind === "pdf" ? "application/pdf" : "text/plain"), kind: kind, data: "" };
                    state.files.push(entry);
                    var r = new FileReader();
                    r.onload = function () { entry.data = r.result; draw(); };
                    r.onerror = function () { state.files.splice(state.files.indexOf(entry), 1); say("Could not read " + f.name + "."); draw(); };
                    if (kind === "text") { r.readAsText(f); } else { r.readAsDataURL(f); }
                });
                draw();
            }
            function pick() { input.click(); }
            function changed() { add(input.files); input.value = ""; }
            function over(e) { if (e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types, "Files") >= 0) { e.preventDefault(); host.classList.add("is-dropping"); } }
            function leave() { host.classList.remove("is-dropping"); }
            function drop(e) { if (e.dataTransfer && e.dataTransfer.files.length) { e.preventDefault(); leave(); add(e.dataTransfer.files); } }
            function paste(e) { if (e.clipboardData && e.clipboardData.files.length) { e.preventDefault(); add(e.clipboardData.files); } }
            var button = root.querySelector(".oui-attach__button");
            button.addEventListener("click", pick); input.addEventListener("change", changed);
            host.addEventListener("dragover", over); host.addEventListener("dragleave", leave); host.addEventListener("drop", drop); host.addEventListener("paste", paste);
            root._ouiCleanup = function () {
                button.removeEventListener("click", pick); host.removeEventListener("dragover", over); host.removeEventListener("dragleave", leave); host.removeEventListener("drop", drop); host.removeEventListener("paste", paste);
                [box, err, input].forEach(function (n) { if (n.parentNode) { n.parentNode.removeChild(n); } });
                if (current === state) { current = null; }
            };
        },
        // The ready files of the composer, and clears them. A file still being read is left out.
        take: function () {
            if (!current) { return []; }
            var ready = current.files.filter(function (f) { return f.data; }); current.files = current.files.filter(function (f) { return !f.data; });
            current.draw(); current.say("");
            return ready;
        }
    };
})();

/* OUI.composer.insert: puts text into the page's chat composer (after any draft), opens the side panel when the composer is in it,
   and focuses the box. The text is not sent. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    OUI.composer = OUI.composer || {};
    OUI.composer.insert = function (text) {
        var area = document.querySelector(".oui-composer textarea"); if (!area) { return; }
        var shell = area.closest(".oui-shell");
        if (shell && area.closest(".oui-shell__aside") && !shell.classList.contains("is-aside-open")) { var open = shell.querySelector('[data-oui-shell="aside"]'); if (open) { open.click(); } }
        var value = area.value ? area.value.replace(/\s+$/, "") + "\n\n" + text : text;
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(area, value);
        area.dispatchEvent(new Event("input", { bubbles: true }));
        area.focus(); area.setSelectionRange(value.length, value.length);
    };
})();


/* OUI.composer.commands: a menu in the chat composer. "/" lists prompts and inserts their text, "@" lists tools and inserts their name.
   items: [{ trigger: "/" or "@", key, title, insert }]. Arrow keys move, Enter or Tab picks, Escape closes. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    OUI.composer = OUI.composer || {};
    function setValue(area, value, caret) { Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(area, value); area.dispatchEvent(new Event("input", { bubbles: true })); area.setSelectionRange(caret, caret); }
    OUI.composer.commands = function (items) {
        var area = document.querySelector(".oui-composer textarea"); if (!area) { return; }
        var host = area.closest(".oui-composer");
        if (host._ouiCommands) { host._ouiCommands.items = items; return; }
        var state = host._ouiCommands = { items: items, open: [], at: 0, start: 0 }, menu = document.createElement("div");
        menu.className = "oui-cmdmenu"; menu.setAttribute("role", "listbox"); menu.hidden = true; host.appendChild(menu);
        function close() { menu.hidden = true; state.open = []; }
        function draw() {
            while (menu.firstChild) { menu.removeChild(menu.firstChild); }
            state.open.forEach(function (it, i) {
                var row = document.createElement("button"); row.type = "button"; row.className = "oui-cmdmenu__row" + (i === state.at ? " is-active" : ""); row.setAttribute("role", "option");
                var k = document.createElement("span"); k.className = "oui-cmdmenu__key"; k.textContent = it.trigger + it.key; row.appendChild(k);
                var t = document.createElement("span"); t.className = "oui-cmdmenu__title"; t.textContent = it.title; row.appendChild(t);
                row.addEventListener("mousedown", function (e) { e.preventDefault(); pick(i); });
                menu.appendChild(row);
            });
            menu.hidden = !state.open.length;
        }
        function pick(i) {
            var it = state.open[i], before = area.value.slice(0, state.start), after = area.value.slice(area.selectionStart), text = it.insert;
            setValue(area, before + text + after, (before + text).length); close(); area.focus();
        }
        function input() {
            var head = area.value.slice(0, area.selectionStart), m = /(^|\s)([\/@])([\w-]*)$/.exec(head);
            if (!m) { close(); return; }
            state.start = head.length - m[3].length - 1; state.at = 0;
            state.open = state.items.filter(function (x) { return x.trigger === m[2] && x.key.toLowerCase().indexOf(m[3].toLowerCase()) === 0; }).slice(0, 8);
            draw();
        }
        function key(e) {
            if (menu.hidden) { return; }
            if (e.key === "ArrowDown" || e.key === "ArrowUp") { state.at = (state.at + (e.key === "ArrowDown" ? 1 : state.open.length - 1)) % state.open.length; draw(); e.preventDefault(); e.stopImmediatePropagation(); }
            else if (e.key === "Enter" || e.key === "Tab") { pick(state.at); e.preventDefault(); e.stopImmediatePropagation(); }
            else if (e.key === "Escape") { close(); e.stopImmediatePropagation(); }
        }
        area.addEventListener("input", input); area.addEventListener("keydown", key, true); area.addEventListener("blur", function () { setTimeout(close, 120); });
    };
})();

/* OUI.voice.toggle: records from the microphone; the second call stops and passes 16 kHz mono WAV as base64 to onDone. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    var active = null;
    function toWav(buffer) {
        var data = buffer.getChannelData(0), out = new DataView(new ArrayBuffer(44 + data.length * 2)), rate = buffer.sampleRate;
        function text(at, s) { for (var i = 0; i < s.length; i++) { out.setUint8(at + i, s.charCodeAt(i)); } }
        text(0, "RIFF"); out.setUint32(4, 36 + data.length * 2, true); text(8, "WAVEfmt "); out.setUint32(16, 16, true); out.setUint16(20, 1, true); out.setUint16(22, 1, true);
        out.setUint32(24, rate, true); out.setUint32(28, rate * 2, true); out.setUint16(32, 2, true); out.setUint16(34, 16, true); text(36, "data"); out.setUint32(40, data.length * 2, true);
        for (var i = 0; i < data.length; i++) { var v = Math.max(-1, Math.min(1, data[i])); out.setInt16(44 + i * 2, v < 0 ? v * 32768 : v * 32767, true); }
        return new Uint8Array(out.buffer);
    }
    function b64(bytes) { var s = ""; for (var i = 0; i < bytes.length; i += 32768) { s += String.fromCharCode.apply(null, bytes.subarray(i, i + 32768)); } return btoa(s); }
    OUI.voice = {
        isRecording: function () { return !!active; },
        toggle: function (onDone, onError) {
            if (active) { active.stop(); active = null; return; }
            navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
                var rec = new MediaRecorder(stream), parts = []; active = rec;
                rec.ondataavailable = function (e) { parts.push(e.data); };
                rec.onstop = function () {
                    stream.getTracks().forEach(function (t) { t.stop(); });
                    new Blob(parts).arrayBuffer().then(function (ab) { return new (window.OfflineAudioContext)(1, 16000 * 600, 16000).decodeAudioData(ab); }).then(function (decoded) {
                        var ctx = new OfflineAudioContext(1, Math.ceil(decoded.duration * 16000), 16000), src = ctx.createBufferSource(); src.buffer = decoded; src.connect(ctx.destination); src.start();
                        return ctx.startRendering();
                    }).then(function (mono) { onDone(b64(toWav(mono))); }, function (e) { if (onError) { onError(String(e)); } });
                };
                rec.start();
            }, function (e) { if (onError) { onError("Microphone: " + e.message); } });
        }
    };
})();

/*! OUI charts: SVG drawn from plain text inputs. OUI.chart.render(id, type, options). Colours come from --oui-c1..c8. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    if (OUI.chart) { return; }
    var NS = "http://www.w3.org/2000/svg";
    function byId(id) { return id ? document.getElementById(id) : null; }
    function s(tag, attrs, parent) { var e = document.createElementNS(NS, tag); for (var k in attrs) { if (attrs[k] !== undefined) { e.setAttribute(k, attrs[k]); } } if (parent) { parent.appendChild(e); } return e; }
    function list(t) { return (t || "").split(",").map(function (x) { return x.trim(); }).filter(function (x) { return x !== ""; }); }
    function nums(t) { return list(t).map(parseFloat).map(function (n) { return isNaN(n) ? 0 : n; }); }
    function series(t) { return (t || "").split("|").map(function (part, i) { var c = part.indexOf(":"); return c >= 0 ? { name: part.slice(0, c).trim(), values: nums(part.slice(c + 1)) } : { name: "Series " + (i + 1), values: nums(part) }; }).filter(function (x) { return x.values.length; }); }
    function color(i) { return "var(--oui-c" + (i % 8 + 1) + ")"; }
    function fmt(n) { return Math.abs(n) >= 1000 ? (n / 1000).toFixed(Math.abs(n) >= 10000 ? 0 : 1).replace(/\.0$/, "") + "k" : String(Math.round(n * 100) / 100); }
    function reduced() { return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches; }
    function nice(max) { if (max <= 0) { return 1; } var p = Math.pow(10, Math.floor(Math.log10(max))), f = max / p, steps = [1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]; for (var i = 0; i < steps.length; i++) { if (f <= steps[i]) { return steps[i] * p; } } return 10 * p; }

    function tooltip(root) {
        var tip = root.querySelector(".oui-chart__tip");
        if (!tip) { tip = document.createElement("div"); tip.className = "oui-chart__tip"; tip.setAttribute("role", "status"); root.appendChild(tip); }
        return {
            show: function (e, title, lines) {
                tip.textContent = "";
                var t = document.createElement("div"); t.className = "oui-chart__tip-title"; t.textContent = title; tip.appendChild(t);
                lines.forEach(function (l) { var row = document.createElement("div"); row.className = "oui-chart__tip-row"; var dot = document.createElement("span"); dot.className = "oui-chart__dot"; dot.style.background = l[2]; row.appendChild(dot); row.appendChild(document.createTextNode(l[0] + ": " + l[1])); tip.appendChild(row); });
                var r = root.getBoundingClientRect(); tip.style.left = Math.min(r.width - 10, Math.max(10, e.clientX - r.left)) + "px"; tip.style.top = (e.clientY - r.top) + "px"; tip.classList.add("is-shown");
            },
            hide: function () { tip.classList.remove("is-shown"); }
        };
    }
    function legend(root, items) {
        if (items.length < 2) { return; }
        var lg = document.createElement("div"); lg.className = "oui-chart__legend";
        items.forEach(function (it, i) { var x = document.createElement("span"); var dot = document.createElement("span"); dot.className = "oui-chart__dot"; dot.style.background = color(i); x.appendChild(dot); x.appendChild(document.createTextNode(it)); lg.appendChild(x); });
        root.appendChild(lg);
    }

    var draw = {};

    draw.line = function (root, o, w, h) {
        var labels = list(o.labels), ser = series(o.series), pad = { l: 36, r: 12, t: 12, b: 24 };
        var all = [].concat.apply([], ser.map(function (x) { return x.values; }));
        var max = nice(Math.max.apply(null, all.concat([0]))), min = Math.min(0, Math.min.apply(null, all.concat([0])));
        var svg = s("svg", { viewBox: "0 0 " + w + " " + h, class: "oui-chart__svg", role: "img", "aria-label": o.title || "Line chart" }, root);
        var iw = w - pad.l - pad.r, ih = h - pad.t - pad.b, n = Math.max(labels.length, ser[0] ? ser[0].values.length : 0);
        function x(i) { return pad.l + (n <= 1 ? iw / 2 : i * iw / (n - 1)); }
        function y(v) { return pad.t + ih - (v - min) / (max - min || 1) * ih; }
        for (var g = 0; g <= 4; g++) { var gv = min + (max - min) * g / 4; s("line", { x1: pad.l, x2: w - pad.r, y1: y(gv), y2: y(gv), class: "oui-chart__grid" }, svg); s("text", { x: pad.l - 6, y: y(gv) + 4, class: "oui-chart__axis", "text-anchor": "end" }, svg).textContent = fmt(gv); }
        labels.forEach(function (l, i) { if (n > 12 && i % Math.ceil(n / 8)) { return; } s("text", { x: x(i), y: h - 6, class: "oui-chart__axis", "text-anchor": "middle" }, svg).textContent = l; });
        ser.forEach(function (sr, k) {
            var pts = sr.values.map(function (v, i) { return [x(i), y(v)]; });
            var d = pts.map(function (p, i) {
                if (i === 0 || !o.smooth) { return (i ? "L" : "M") + p[0] + "," + p[1]; }
                var p0 = pts[i - 1], cx = (p0[0] + p[0]) / 2; return "C" + cx + "," + p0[1] + " " + cx + "," + p[1] + " " + p[0] + "," + p[1];
            }).join(" ");
            if (o.area) { s("path", { d: d + " L" + pts[pts.length - 1][0] + "," + y(Math.max(0, min)) + " L" + pts[0][0] + "," + y(Math.max(0, min)) + " Z", fill: color(k), class: "oui-chart__area" }, svg); }
            var path = s("path", { d: d, stroke: color(k), class: "oui-chart__line" }, svg);
            if (!reduced()) { var len = path.getTotalLength ? path.getTotalLength() : 1000; path.style.strokeDasharray = len; path.style.strokeDashoffset = len; path.getBoundingClientRect(); path.style.transition = "stroke-dashoffset 900ms cubic-bezier(.2,.8,.2,1) " + k * 120 + "ms"; path.style.strokeDashoffset = "0"; }
        });
        var tip = tooltip(root), guide = s("line", { y1: pad.t, y2: pad.t + ih, class: "oui-chart__guide" }, svg), dots = ser.map(function (sr, k) { return s("circle", { r: 4, fill: color(k), class: "oui-chart__point" }, svg); });
        svg.addEventListener("pointermove", function (e) {
            var r = svg.getBoundingClientRect(), px = (e.clientX - r.left) / r.width * w, i = Math.max(0, Math.min(n - 1, Math.round((px - pad.l) / (iw / Math.max(1, n - 1)))));
            guide.setAttribute("x1", x(i)); guide.setAttribute("x2", x(i)); guide.classList.add("is-shown");
            dots.forEach(function (d, k) { var v = ser[k].values[i]; if (v === undefined) { return; } d.setAttribute("cx", x(i)); d.setAttribute("cy", y(v)); d.classList.add("is-shown"); });
            tip.show(e, labels[i] || "", ser.map(function (sr, k) { return [sr.name, fmt(sr.values[i] || 0), color(k)]; }));
        });
        svg.addEventListener("pointerleave", function () { tip.hide(); guide.classList.remove("is-shown"); dots.forEach(function (d) { d.classList.remove("is-shown"); }); });
        legend(root, ser.map(function (x) { return x.name; }));
    };

    draw.bar = function (root, o, w, h) {
        var labels = list(o.labels), ser = series(o.series), pad = { l: o.horizontal ? 90 : 36, r: 12, t: 12, b: 24 }, n = labels.length || (ser[0] ? ser[0].values.length : 0);
        var totals = []; for (var i = 0; i < n; i++) { totals.push(ser.reduce(function (a, x) { return a + (x.values[i] || 0); }, 0)); }
        var max = nice(o.stacked ? Math.max.apply(null, totals.concat([0])) : Math.max.apply(null, [].concat.apply([], ser.map(function (x) { return x.values; })).concat([0])));
        var svg = s("svg", { viewBox: "0 0 " + w + " " + h, class: "oui-chart__svg", role: "img", "aria-label": o.title || "Bar chart" }, root), tip = tooltip(root);
        var iw = w - pad.l - pad.r, ih = h - pad.t - pad.b, band = (o.horizontal ? ih : iw) / Math.max(1, n), inner = band * .64, gap = (band - inner) / 2;
        for (var g = 0; g <= 4; g++) {
            var gv = max * g / 4;
            if (o.horizontal) { var gx = pad.l + gv / max * iw; s("line", { x1: gx, x2: gx, y1: pad.t, y2: pad.t + ih, class: "oui-chart__grid" }, svg); s("text", { x: gx, y: h - 6, class: "oui-chart__axis", "text-anchor": "middle" }, svg).textContent = fmt(gv); }
            else { var gy = pad.t + ih - gv / max * ih; s("line", { x1: pad.l, x2: w - pad.r, y1: gy, y2: gy, class: "oui-chart__grid" }, svg); s("text", { x: pad.l - 6, y: gy + 4, class: "oui-chart__axis", "text-anchor": "end" }, svg).textContent = fmt(gv); }
        }
        labels.forEach(function (l, i) {
            if (o.horizontal) { s("text", { x: pad.l - 8, y: pad.t + i * band + band / 2 + 4, class: "oui-chart__axis", "text-anchor": "end" }, svg).textContent = l; }
            else { s("text", { x: pad.l + i * band + band / 2, y: h - 6, class: "oui-chart__axis", "text-anchor": "middle" }, svg).textContent = l; }
        });
        for (i = 0; i < n; i++) {
            var acc = 0;
            ser.forEach(function (sr, k) {
                var v = sr.values[i] || 0, len = v / max * (o.horizontal ? iw : ih), thick = o.stacked ? inner : inner / ser.length, off = o.stacked ? 0 : k * thick, start = o.stacked ? acc : 0, rect;
                if (o.horizontal) { rect = s("rect", { x: pad.l + start / max * iw, y: pad.t + i * band + gap + off, width: Math.max(0, len), height: thick - (o.stacked ? 0 : 2), rx: 3, fill: color(k), class: "oui-chart__bar" }, svg); rect.style.transformOrigin = "left"; }
                else { rect = s("rect", { x: pad.l + i * band + gap + off, y: pad.t + ih - (start / max * ih) - len, width: thick - (o.stacked ? 0 : 2), height: Math.max(0, len), rx: 3, fill: color(k), class: "oui-chart__bar" }, svg); rect.style.transformOrigin = "bottom"; }
                rect.style.transformBox = "fill-box";
                if (!reduced()) { rect.style.animationDelay = (i * 40 + k * 60) + "ms"; rect.classList.add(o.horizontal ? "is-grow-x" : "is-grow-y"); }
                (function (lbl, name, val, c) { rect.addEventListener("pointermove", function (e) { tip.show(e, lbl, [[name, fmt(val), c]]); }); rect.addEventListener("pointerleave", tip.hide); })(labels[i] || "", sr.name, v, color(k));
                acc += v;
            });
        }
        legend(root, ser.map(function (x) { return x.name; }));
    };

    draw.donut = function (root, o, w, h) {
        var labels = list(o.labels), vals = nums(o.values), total = vals.reduce(function (a, b) { return a + b; }, 0) || 1, size = Math.min(w, h), r = size / 2 - 8, inner = r * (o.pie ? 0 : .64);
        var svg = s("svg", { viewBox: "0 0 " + size + " " + size, class: "oui-chart__svg oui-chart__svg--square", role: "img", "aria-label": o.title || "Donut chart" }, root), tip = tooltip(root), a0 = -Math.PI / 2, cx = size / 2, cy = size / 2;
        vals.forEach(function (v, i) {
            var a1 = a0 + v / total * Math.PI * 2, large = a1 - a0 > Math.PI ? 1 : 0, gapA = vals.length > 1 ? .012 : 0;
            function pt(rad, ang) { return [cx + rad * Math.cos(ang), cy + rad * Math.sin(ang)]; }
            var p1 = pt(r, a0 + gapA), p2 = pt(r, a1 - gapA), p3 = pt(inner, a1 - gapA), p4 = pt(inner, a0 + gapA);
            var d = "M" + p1 + " A" + r + "," + r + " 0 " + large + " 1 " + p2 + (inner > 0 ? " L" + p3 + " A" + inner + "," + inner + " 0 " + large + " 0 " + p4 : " L" + cx + "," + cy) + " Z";
            var seg = s("path", { d: d, fill: color(i), class: "oui-chart__slice" }, svg);
            if (!reduced()) { seg.style.animationDelay = i * 70 + "ms"; seg.classList.add("is-pop"); }
            seg.addEventListener("pointermove", function (e) { tip.show(e, labels[i] || "", [["Value", fmt(v), color(i)], ["Share", Math.round(v / total * 100) + "%", color(i)]]); });
            seg.addEventListener("pointerleave", tip.hide);
            a0 = a1;
        });
        if (o.center) { var t = s("text", { x: cx, y: cy - 2, class: "oui-chart__center", "text-anchor": "middle" }, svg); t.textContent = o.center; s("text", { x: cx, y: cy + 16, class: "oui-chart__axis", "text-anchor": "middle" }, svg).textContent = o.centerCaption || ""; }
        legend(root, labels);
    };

    draw.sparkline = function (root, o, w, h) {
        var v = nums(o.values), max = Math.max.apply(null, v.concat([0])), min = Math.min.apply(null, v.concat([0])), svg = s("svg", { viewBox: "0 0 " + w + " " + h, class: "oui-chart__svg", preserveAspectRatio: "none", role: "img", "aria-label": o.title || "Trend" }, root);
        var pts = v.map(function (x, i) { return [i * w / Math.max(1, v.length - 1), h - 2 - (x - min) / (max - min || 1) * (h - 4)]; }), d = pts.map(function (p, i) { return (i ? "L" : "M") + p[0] + "," + p[1]; }).join(" ");
        s("path", { d: d + " L" + w + "," + h + " L0," + h + " Z", fill: "var(--oui-c1)", class: "oui-chart__area" }, svg);
        s("path", { d: d, stroke: "var(--oui-c1)", class: "oui-chart__line", "vector-effect": "non-scaling-stroke" }, svg);
        if (pts.length) { s("circle", { cx: pts[pts.length - 1][0], cy: pts[pts.length - 1][1], r: 2.5, fill: "var(--oui-c1)" }, svg); }
    };

    draw.heatmap = function (root, o, w) {
        var rows = list(o.rows), cols = list(o.columns), vals = nums(o.values), max = Math.max.apply(null, vals.concat([1])), cell = Math.max(14, Math.min(40, (w - 80) / Math.max(1, cols.length))), tip = tooltip(root);
        var svg = s("svg", { viewBox: "0 0 " + (80 + cols.length * cell) + " " + (20 + rows.length * cell), class: "oui-chart__svg", role: "img", "aria-label": o.title || "Heatmap" }, root);
        cols.forEach(function (c, j) { s("text", { x: 80 + j * cell + cell / 2, y: 12, class: "oui-chart__axis", "text-anchor": "middle" }, svg).textContent = c; });
        rows.forEach(function (r, i) {
            s("text", { x: 72, y: 20 + i * cell + cell / 2 + 4, class: "oui-chart__axis", "text-anchor": "end" }, svg).textContent = r;
            cols.forEach(function (c, j) { var v = vals[i * cols.length + j] || 0, rect = s("rect", { x: 80 + j * cell + 1, y: 20 + i * cell + 1, width: cell - 2, height: cell - 2, rx: 4, fill: "var(--oui-c1)", "fill-opacity": .08 + .92 * v / max, class: "oui-chart__cell" }, svg);
                rect.addEventListener("pointermove", function (e) { tip.show(e, r + ", " + c, [["Value", fmt(v), "var(--oui-c1)"]]); }); rect.addEventListener("pointerleave", tip.hide); });
        });
    };

    draw.calendar = function (root, o, w) {
        var map = {}; list(o.data).forEach(function (p) { var k = p.split(":"); map[k[0]] = parseFloat(k[1]) || 0; });
        var weeks = o.weeks || 26, end = o.end ? new Date(o.end + "T00:00:00") : new Date(), start = new Date(end); start.setDate(end.getDate() - weeks * 7 + 1); start.setDate(start.getDate() - ((start.getDay() + 6) % 7));
        var max = Math.max.apply(null, Object.keys(map).map(function (k) { return map[k]; }).concat([1])), cell = Math.max(10, Math.min(16, (w - 30) / weeks)), tip = tooltip(root);
        var svg = s("svg", { viewBox: "0 0 " + (30 + weeks * cell) + " " + (16 + 7 * cell), class: "oui-chart__svg", role: "img", "aria-label": o.title || "Activity calendar" }, root);
        ["Mon", "", "Wed", "", "Fri", "", ""].forEach(function (d, i) { if (d) { s("text", { x: 0, y: 16 + i * cell + cell - 3, class: "oui-chart__axis" }, svg).textContent = d; } });
        for (var d = new Date(start), idx = 0; d <= end; d.setDate(d.getDate() + 1), idx++) {
            var key = d.toISOString().slice(0, 10), v = map[key] || 0, col = Math.floor(idx / 7), row = idx % 7;
            if (row === 0 && d.getDate() <= 7) { s("text", { x: 30 + col * cell, y: 10, class: "oui-chart__axis" }, svg).textContent = d.toLocaleString(undefined, { month: "short" }); }
            var rect = s("rect", { x: 30 + col * cell + 1, y: 16 + row * cell + 1, width: cell - 2, height: cell - 2, rx: 2.5, fill: v ? "var(--oui-c1)" : "var(--oui-sunken)", "fill-opacity": v ? .2 + .8 * v / max : 1, class: "oui-chart__cell" }, svg);
            (function (k, val) { rect.addEventListener("pointermove", function (e) { tip.show(e, k, [["Value", fmt(val), "var(--oui-c1)"]]); }); rect.addEventListener("pointerleave", tip.hide); })(key, v);
        }
    };

    draw.treemap = function (root, o, w, h) {
        var items = list(o.labels).map(function (l, i) { return { label: l, value: nums(o.values)[i] || 0, i: i }; }).filter(function (x) { return x.value > 0; }).sort(function (a, b) { return b.value - a.value; });
        var svg = s("svg", { viewBox: "0 0 " + w + " " + h, class: "oui-chart__svg", role: "img", "aria-label": o.title || "Treemap" }, root), tip = tooltip(root);
        function layout(arr, x, y, ww, hh) {
            if (!arr.length) { return; }
            if (arr.length === 1) { place(arr[0], x, y, ww, hh); return; }
            var total = arr.reduce(function (a, b) { return a + b.value; }, 0), acc = 0, cut = 0;
            for (; cut < arr.length - 1; cut++) { acc += arr[cut].value; if (acc >= total / 2) { break; } }
            var first = arr.slice(0, cut + 1), rest = arr.slice(cut + 1), share = first.reduce(function (a, b) { return a + b.value; }, 0) / total;
            if (ww >= hh) { layout(first, x, y, ww * share, hh); layout(rest, x + ww * share, y, ww * (1 - share), hh); }
            else { layout(first, x, y, ww, hh * share); layout(rest, x, y + hh * share, ww, hh * (1 - share)); }
        }
        function place(it, x, y, ww, hh) {
            var r = s("rect", { x: x + 1, y: y + 1, width: Math.max(0, ww - 2), height: Math.max(0, hh - 2), rx: 6, fill: color(it.i), class: "oui-chart__tile" }, svg);
            if (ww > 60 && hh > 30) { s("text", { x: x + 10, y: y + 20, class: "oui-chart__tile-label" }, svg).textContent = it.label; s("text", { x: x + 10, y: y + 36, class: "oui-chart__tile-value" }, svg).textContent = fmt(it.value); }
            r.addEventListener("pointermove", function (e) { tip.show(e, it.label, [["Value", fmt(it.value), color(it.i)]]); }); r.addEventListener("pointerleave", tip.hide);
        }
        layout(items, 0, 0, w, h);
    };

    draw.radar = function (root, o, w, h) {
        var axes = list(o.labels), ser = series(o.series), size = Math.min(w, h), cx = size / 2, cy = size / 2, r = size / 2 - 34, max = nice(Math.max.apply(null, [].concat.apply([], ser.map(function (x) { return x.values; })).concat([1])));
        var svg = s("svg", { viewBox: "0 0 " + size + " " + size, class: "oui-chart__svg oui-chart__svg--square", role: "img", "aria-label": o.title || "Radar chart" }, root);
        function pt(i, v) { var a = -Math.PI / 2 + i * 2 * Math.PI / axes.length; return [cx + r * v / max * Math.cos(a), cy + r * v / max * Math.sin(a)]; }
        for (var g = 1; g <= 4; g++) { s("polygon", { points: axes.map(function (a, i) { return pt(i, max * g / 4).join(","); }).join(" "), class: "oui-chart__grid oui-chart__ring" }, svg); }
        axes.forEach(function (a, i) { var p = pt(i, max), q = pt(i, max * 1.16); s("line", { x1: cx, y1: cy, x2: p[0], y2: p[1], class: "oui-chart__grid" }, svg); s("text", { x: q[0], y: q[1] + 4, class: "oui-chart__axis", "text-anchor": "middle" }, svg).textContent = a; });
        ser.forEach(function (sr, k) { var poly = s("polygon", { points: sr.values.map(function (v, i) { return pt(i, v).join(","); }).join(" "), fill: color(k), stroke: color(k), class: "oui-chart__radar" }, svg); if (!reduced()) { poly.classList.add("is-pop"); poly.style.animationDelay = k * 120 + "ms"; } });
        legend(root, ser.map(function (x) { return x.name; }));
    };

    draw.funnel = function (root, o, w) {
        var labels = list(o.labels), vals = nums(o.values), max = Math.max.apply(null, vals.concat([1])), rowH = 44, h = labels.length * rowH;
        var svg = s("svg", { viewBox: "0 0 " + w + " " + h, class: "oui-chart__svg", role: "img", "aria-label": o.title || "Funnel" }, root);
        vals.forEach(function (v, i) {
            var bw = Math.max(40, v / max * (w - 160)), x = (w - 160 - bw) / 2;
            var r = s("rect", { x: x, y: i * rowH + 4, width: bw, height: rowH - 8, rx: 8, fill: "var(--oui-c1)", "fill-opacity": 1 - i * .12, class: "oui-chart__bar is-grow-x" }, svg); r.style.transformOrigin = "center"; r.style.transformBox = "fill-box"; r.style.animationDelay = i * 70 + "ms";
            s("text", { x: w - 150, y: i * rowH + rowH / 2 + 4, class: "oui-chart__label" }, svg).textContent = labels[i] + "  " + fmt(v) + (i ? "  (" + Math.round(v / vals[0] * 100) + "%)" : "");
        });
    };

    draw.sankey = function (root, o, w, h) {
        var links = list(o.links).map(function (p) { var m = /^(.+?)>(.+?):([\d.]+)$/.exec(p); return m ? { a: m[1].trim(), b: m[2].trim(), v: parseFloat(m[3]) } : null; }).filter(Boolean);
        var left = [], right = []; links.forEach(function (l) { if (left.indexOf(l.a) < 0) { left.push(l.a); } if (right.indexOf(l.b) < 0) { right.push(l.b); } });
        var total = links.reduce(function (a, l) { return a + l.v; }, 0) || 1, gap = 10, nodeW = 14, svg = s("svg", { viewBox: "0 0 " + w + " " + h, class: "oui-chart__svg", role: "img", "aria-label": o.title || "Flow" }, root), tip = tooltip(root);
        function column(names, side) { var avail = h - gap * (names.length - 1), y = 0, pos = {}; names.forEach(function (n) { var v = links.filter(function (l) { return (side ? l.b : l.a) === n; }).reduce(function (a, l) { return a + l.v; }, 0), hh = v / total * avail; pos[n] = { y: y, h: hh, used: 0 }; y += hh + gap; }); return pos; }
        var L = column(left, 0), R = column(right, 1), x1 = 110, x2 = w - 110;
        links.forEach(function (l, i) {
            var hh = l.v / total * (h - gap * (Math.max(left.length, right.length) - 1)), ya = L[l.a].y + L[l.a].used + hh / 2, yb = R[l.b].y + R[l.b].used + hh / 2; L[l.a].used += hh; R[l.b].used += hh;
            var p = s("path", { d: "M" + (x1 + nodeW) + "," + ya + " C" + (x1 + x2) / 2 + "," + ya + " " + (x1 + x2) / 2 + "," + yb + " " + x2 + "," + yb, stroke: color(left.indexOf(l.a)), "stroke-width": Math.max(1, hh), class: "oui-chart__flow" }, svg);
            p.addEventListener("pointermove", function (e) { tip.show(e, l.a + " to " + l.b, [["Value", fmt(l.v), color(left.indexOf(l.a))]]); }); p.addEventListener("pointerleave", tip.hide);
        });
        left.forEach(function (n, i) { s("rect", { x: x1, y: L[n].y, width: nodeW, height: Math.max(2, L[n].h), rx: 3, fill: color(i) }, svg); s("text", { x: x1 - 8, y: L[n].y + L[n].h / 2 + 4, class: "oui-chart__label", "text-anchor": "end" }, svg).textContent = n; });
        right.forEach(function (n) { s("rect", { x: x2, y: R[n].y, width: nodeW, height: Math.max(2, R[n].h), rx: 3, fill: "var(--oui-text-3)" }, svg); s("text", { x: x2 + nodeW + 8, y: R[n].y + R[n].h / 2 + 4, class: "oui-chart__label" }, svg).textContent = n; });
    };

    draw.scatter = function (root, o, w, h) {
        var pts = list(o.points).map(function (p) { var a = p.split(":").map(parseFloat); return { x: a[0] || 0, y: a[1] || 0, r: a[2] || 0, s: a[3] || 0 }; }), pad = { l: 36, r: 12, t: 12, b: 24 };
        var mx = nice(Math.max.apply(null, pts.map(function (p) { return p.x; }).concat([1]))), my = nice(Math.max.apply(null, pts.map(function (p) { return p.y; }).concat([1]))), mr = Math.max.apply(null, pts.map(function (p) { return p.r; }).concat([1]));
        var svg = s("svg", { viewBox: "0 0 " + w + " " + h, class: "oui-chart__svg", role: "img", "aria-label": o.title || "Scatter chart" }, root), tip = tooltip(root), iw = w - pad.l - pad.r, ih = h - pad.t - pad.b;
        for (var g = 0; g <= 4; g++) { var gy = pad.t + ih - ih * g / 4, gx = pad.l + iw * g / 4; s("line", { x1: pad.l, x2: w - pad.r, y1: gy, y2: gy, class: "oui-chart__grid" }, svg); s("text", { x: pad.l - 6, y: gy + 4, class: "oui-chart__axis", "text-anchor": "end" }, svg).textContent = fmt(my * g / 4); s("text", { x: gx, y: h - 6, class: "oui-chart__axis", "text-anchor": "middle" }, svg).textContent = fmt(mx * g / 4); }
        pts.forEach(function (p, i) { var c = s("circle", { cx: pad.l + p.x / mx * iw, cy: pad.t + ih - p.y / my * ih, r: p.r ? 4 + 14 * p.r / mr : 5, fill: color(p.s), class: "oui-chart__bubble" }, svg); if (!reduced()) { c.classList.add("is-pop"); c.style.animationDelay = i * 25 + "ms"; }
            c.addEventListener("pointermove", function (e) { tip.show(e, "Point " + (i + 1), [["x", fmt(p.x), color(p.s)], ["y", fmt(p.y), color(p.s)]].concat(p.r ? [["size", fmt(p.r), color(p.s)]] : [])); }); c.addEventListener("pointerleave", tip.hide); });
    };

    draw.rings = function (root, o, w, h) {
        var vals = nums(o.values), labels = list(o.labels), size = Math.min(w, h), cx = size / 2, cy = size / 2, stroke = Math.max(10, size / 12);
        var svg = s("svg", { viewBox: "0 0 " + size + " " + size, class: "oui-chart__svg oui-chart__svg--square", role: "img", "aria-label": o.title || "Activity rings" }, root);
        vals.forEach(function (v, i) {
            var r = size / 2 - stroke / 2 - 2 - i * (stroke + 4), c = 2 * Math.PI * r;
            if (r <= stroke) { return; }
            s("circle", { cx: cx, cy: cy, r: r, stroke: color(i), "stroke-opacity": .16, "stroke-width": stroke, fill: "none" }, svg);
            var arc = s("circle", { cx: cx, cy: cy, r: r, stroke: color(i), "stroke-width": stroke, fill: "none", "stroke-linecap": "round", transform: "rotate(-90 " + cx + " " + cy + ")", "stroke-dasharray": c, "stroke-dashoffset": c, class: "oui-chart__ring-arc" }, svg);
            var target = c * (1 - Math.min(1, v / 100));
            if (reduced()) { arc.setAttribute("stroke-dashoffset", target); } else { requestAnimationFrame(function () { arc.style.transition = "stroke-dashoffset 1100ms cubic-bezier(.2,.8,.2,1) " + i * 150 + "ms"; arc.setAttribute("stroke-dashoffset", target); }); }
            var t = document.createElementNS(NS, "title"); t.textContent = (labels[i] || "Ring " + (i + 1)) + ": " + v + "%"; arc.appendChild(t);
        });
        legend(root, labels);
    };

    function div(cls, parent, text) { var e = document.createElement("div"); e.className = cls; if (text !== undefined) { e.textContent = text; } if (parent) { parent.appendChild(e); } return e; }

    draw.barlist = function (root, o) {
        var labels = list(o.labels), vals = nums(o.values), max = Math.max.apply(null, vals.concat([1])), box = div("oui-barlist", root);
        vals.forEach(function (v, i) {
            var row = div("oui-barlist__row", box), bar = div("oui-barlist__bar", row);
            bar.style.setProperty("--oui-w", (v / max * 100) + "%"); bar.style.animationDelay = i * 50 + "ms";
            div("oui-barlist__label", bar, labels[i] || ""); div("oui-barlist__value", row, fmt(v));
        });
    };

    draw.splitbar = function (root, o) {
        var labels = list(o.labels), vals = nums(o.values), total = vals.reduce(function (a, b) { return a + b; }, 0) || 1, box = div("oui-splitbar", root), bar = div("oui-splitbar__bar", box), leg = div("oui-chart__legend", box), tip = tooltip(root);
        vals.forEach(function (v, i) {
            var seg = div("oui-splitbar__seg", bar); seg.style.flexGrow = v; seg.style.background = color(i);
            seg.addEventListener("pointermove", function (e) { tip.show(e, labels[i] || "", [["Share", Math.round(v / total * 100) + "%", color(i)], ["Value", fmt(v), color(i)]]); }); seg.addEventListener("pointerleave", tip.hide);
            var item = document.createElement("span"), dot = document.createElement("span"); dot.className = "oui-chart__dot"; dot.style.background = color(i); item.appendChild(dot); item.appendChild(document.createTextNode((labels[i] || "") + " " + Math.round(v / total * 100) + "%")); leg.appendChild(item);
        });
    };

    draw.waffle = function (root, o) {
        var v = Math.max(0, Math.min(100, Math.round(parseFloat(o.value) || 0))), box = div("oui-waffle", root);
        for (var i = 0; i < 100; i++) { var c = div("oui-waffle__cell" + (i < v ? " is-on" : ""), box); if (i < v && !reduced()) { c.style.animationDelay = i * 6 + "ms"; } }
        box.setAttribute("role", "img"); box.setAttribute("aria-label", (o.title || "Share") + ": " + v + "%");
    };

    OUI.chart = {
        render: function (id, type, o) {
            var root = byId(id); if (!root || !draw[type]) { return; }
            root.querySelectorAll("svg, .oui-chart__legend, .oui-barlist, .oui-splitbar, .oui-waffle").forEach(function (e) { e.remove(); });
            var w = Math.max(220, Math.round(root.clientWidth || 600)), h = o.height || 240;
            draw[type](root, o, w, h);
        },
        mount: function (id, type, o) {
            var root = byId(id); if (!root) { return; }
            OUI.chart.render(id, type, o);
            var last = root.clientWidth, timer = null;
            var ro = window.ResizeObserver ? new ResizeObserver(function () { if (Math.abs(root.clientWidth - last) < 8) { return; } last = root.clientWidth; clearTimeout(timer); timer = setTimeout(function () { OUI.chart.render(id, type, root._ouiOptions || o); }, 120); }) : null;
            root._ouiOptions = o;
            if (ro) { ro.observe(root); }
            if (root._ouiCleanup) { root._ouiCleanup(); }
            root._ouiCleanup = function () { if (ro) { ro.disconnect(); } clearTimeout(timer); };
        },
        update: function (id, type, o) { var root = byId(id); if (root) { root._ouiOptions = o; } OUI.chart.render(id, type, o); }
    };
})();

/*! OUI controls: hold to confirm, sliders, clipboard, timers. Each mount returns nothing and is undone by unmount(id). */
(function () {
    var OUI = window.OUI = window.OUI || {};
    if (OUI.unmount) { return; }

    function byId(id) { return id ? document.getElementById(id) : null; }
    function keep(el, cleanup) { if (el._ouiCleanup) { el._ouiCleanup(); } el._ouiCleanup = cleanup; }

    OUI.unmount = function (id) {
        var el = byId(id);
        if (el && el._ouiCleanup) { el._ouiCleanup(); el._ouiCleanup = null; }
    };

    OUI.copy = function (text) {
        if (navigator.clipboard && window.isSecureContext) { return navigator.clipboard.writeText(text); }
        return new Promise(function (resolve, reject) {
            var area = document.createElement("textarea");
            area.value = text; area.setAttribute("readonly", ""); area.style.position = "fixed"; area.style.opacity = "0";
            document.body.appendChild(area); area.select();
            try { document.execCommand("copy") ? resolve() : reject(new Error("copy failed")); } catch (e) { reject(e); }
            document.body.removeChild(area);
        });
    };

    OUI.wait = function (ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); };

    // Polling: calls tick every intervalMs while the page is visible. Hidden tabs do not poll; on return it polls at once.
    OUI.poll = {
        mount: function (id, intervalMs, tick, onCountdown, onFinish) {
            var el = byId(id);
            if (!el) { return; }
            var timer = null, ms = Math.max(1000, intervalMs || 3000), reloading = false;
            function run() {
                if (document.visibilityState !== "visible") { return; }
                if (el.getAttribute("data-active") === "true") { tick(); return; }
                if (el.getAttribute("data-finish") === "true" && onFinish) { onFinish(); }
                var seconds = parseInt(el.getAttribute("data-reload") || "0", 10);
                if (seconds > 0 && !reloading) { reloading = true; OUI.reload.start(seconds, onCountdown); }
                if (seconds === 0) { reloading = false; }
            }
            function start() { stop(); timer = setInterval(run, ms); }
            function stop() { if (timer) { clearInterval(timer); timer = null; } }
            function onVisible() { if (document.visibilityState === "visible") { run(); start(); } else { stop(); } }
            document.addEventListener("visibilitychange", onVisible);
            start();
            keep(el, function () { stop(); document.removeEventListener("visibilitychange", onVisible); });
        }
    };

    // Reloads the page after a countdown; onTick(secondsLeft) runs each second. OUI.reload.cancel() stops it.
    OUI.reload = {
        timer: null,
        start: function (seconds, onTick) {
            OUI.reload.cancel();
            var left = Math.max(0, seconds || 0);
            if (onTick) { onTick(left); }
            OUI.reload.timer = setInterval(function () {
                left -= 1;
                if (onTick) { onTick(left); }
                if (left <= 0) { OUI.reload.cancel(); window.location.reload(); }
            }, 1000);
        },
        cancel: function () { if (OUI.reload.timer) { clearInterval(OUI.reload.timer); OUI.reload.timer = null; } },
        now: function () { window.location.reload(); }
    };

    OUI.hold = {
        mount: function (id, ms, done) {
            var el = byId(id);
            if (!el) { return; }
            var timer = null;
            el.style.setProperty("--oui-hold", (ms || 1200) + "ms");
            function start(e) {
                if (el.disabled || timer || (e.type === "pointerdown" && e.button !== 0)) { return; }
                if (e.type === "keydown" && e.key !== " " && e.key !== "Enter") { return; }
                if (e.type === "keydown") { e.preventDefault(); }
                el.classList.add("is-holding");
                timer = setTimeout(function () {
                    timer = null;
                    el.classList.remove("is-holding");
                    el.classList.add("is-done");
                    setTimeout(function () { el.classList.remove("is-done"); }, 900);
                    done();
                }, ms || 1200);
            }
            function stop() { if (timer) { clearTimeout(timer); timer = null; } el.classList.remove("is-holding"); }
            var events = [["pointerdown", start], ["keydown", start], ["pointerup", stop], ["pointerleave", stop], ["pointercancel", stop], ["keyup", stop], ["blur", stop]];
            events.forEach(function (p) { el.addEventListener(p[0], p[1]); });
            keep(el, function () { stop(); events.forEach(function (p) { el.removeEventListener(p[0], p[1]); }); });
        }
    };

    OUI.slider = {
        mount: function (id, onChange) {
            var root = byId(id);
            if (!root) { return; }
            var inputs = root.querySelectorAll("input[type=range]");
            function paint() {
                var min = parseFloat(inputs[0].min || 0), max = parseFloat(inputs[0].max || 100), span = (max - min) || 1;
                var a = (parseFloat(inputs[0].value) - min) / span * 100;
                if (inputs.length > 1) {
                    var b = (parseFloat(inputs[1].value) - min) / span * 100;
                    root.style.setProperty("--oui-from", Math.min(a, b) + "%");
                    root.style.setProperty("--oui-to", Math.max(a, b) + "%");
                } else {
                    root.style.setProperty("--oui-from", "0%");
                    root.style.setProperty("--oui-to", a + "%");
                }
                var out = root.querySelectorAll("[data-oui-output]");
                for (var i = 0; i < out.length; i++) { out[i].textContent = inputs[i] ? inputs[i].value : ""; }
            }
            function onInput() {
                if (inputs.length > 1 && parseFloat(inputs[0].value) > parseFloat(inputs[1].value)) {
                    if (this === inputs[0]) { inputs[0].value = inputs[1].value; } else { inputs[1].value = inputs[0].value; }
                }
                paint();
            }
            function onCommit() { onChange(inputs[0].value, inputs.length > 1 ? inputs[1].value : inputs[0].value); }
            OUI.slider.set(id);
            for (var i = 0; i < inputs.length; i++) { inputs[i].addEventListener("input", onInput); inputs[i].addEventListener("change", onCommit); }
            root._ouiPaint = paint;
            keep(root, function () { for (var j = 0; j < inputs.length; j++) { inputs[j].removeEventListener("input", onInput); inputs[j].removeEventListener("change", onCommit); } });
        },
        set: function (id) {
            var root = byId(id);
            if (!root) { return; }
            var inputs = root.querySelectorAll("input[type=range]");
            for (var i = 0; i < inputs.length; i++) {
                var v = inputs[i].getAttribute("data-value");
                if (v !== null && v !== "" && document.activeElement !== inputs[i]) { inputs[i].value = v; }
            }
            if (root._ouiPaint) { root._ouiPaint(); }
            else if (inputs.length) {
                var min = parseFloat(inputs[0].min || 0), max = parseFloat(inputs[0].max || 100), span = (max - min) || 1;
                root.style.setProperty("--oui-from", inputs.length > 1 ? (Math.min(inputs[0].value, inputs[1].value) - min) / span * 100 + "%" : "0%");
                root.style.setProperty("--oui-to", ((inputs.length > 1 ? Math.max(inputs[0].value, inputs[1].value) : inputs[0].value) - min) / span * 100 + "%");
            }
        }
    };
})();

/*! OUI data and code: renderers that build DOM from text (never innerHTML of user text), reorder, swipe, context menu, countdown, log. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    if (OUI.render) { return; }
    function byId(id) { return id ? document.getElementById(id) : null; }
    function keep(el, cleanup) { if (el._ouiCleanup) { el._ouiCleanup(); } el._ouiCleanup = cleanup; }
    function h(tag, cls, text) { var e = document.createElement(tag); if (cls) { e.className = cls; } if (text !== undefined && text !== null) { e.textContent = text; } return e; }
    function clear(el) { while (el.firstChild) { el.removeChild(el.firstChild); } }

    // ------------------------------------------------------------ code highlighting (small, language-neutral)
    var KEYWORDS = /^(function|return|var|let|const|if|else|for|while|switch|case|break|new|class|public|private|static|void|int|string|bool|true|false|null|undefined|select|from|where|join|on|and|or|not|insert|update|delete|into|values|order|by|group|as|import|export|async|await|try|catch|finally|throw|def|in|is|None|True|False)$/i;
    function highlightLine(line, lang) {
        var frag = document.createDocumentFragment(), re = /(\/\/.*$|--.*$|#.*$|"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'|`[^`]*`|\b\d+(?:\.\d+)?\b|\b[A-Za-z_][\w]*\b|\s+|.)/g, m;
        while ((m = re.exec(line)) !== null) {
            var t = m[0], cls = null;
            if (/^(\/\/|--)/.test(t) || (t[0] === "#" && lang !== "css")) { cls = "c"; }
            else if (/^["'`]/.test(t)) { cls = "s"; }
            else if (/^\d/.test(t)) { cls = "n"; }
            else if (KEYWORDS.test(t)) { cls = "k"; }
            else if (/^[A-Z][\w]*$/.test(t)) { cls = "t"; }
            frag.appendChild(cls ? h("span", "oui-tok-" + cls, t) : document.createTextNode(t));
        }
        return frag;
    }

    // KaTeX and Mermaid are big, so they are separate scripts of this module, next to OuiCore, loaded the first time an answer needs them.
    var loaded = {};
    function library(name) {
        if (!loaded[name]) {
            loaded[name] = new Promise(function (resolve, reject) {
                // Only the module that owns a script serves it when no block requires it: <Module>.OuiCore.js names that module.
                var core = Array.prototype.map.call(document.scripts, function (x) { return /\/scripts\/([^.\/]+)\.OuiCore\.js/.exec(x.src); }).filter(Boolean)[0];
                if (!core) { reject(new Error("OuiCore not found")); return; }
                var url = "/" + core[1] + "/scripts/" + core[1] + "." + name + ".js";
                // OutSystems pages use AMD, which would take these libraries' globals. Hide define while the script runs; it is fetched
                // first, so the moment is short and no OutSystems module can load during it.
                fetch(url, { credentials: "same-origin" }).then(function () {
                    var tag = document.createElement("script"), amd = window.define;
                    function restore() { window.define = amd; }
                    tag.src = url;
                    tag.onload = function () { restore(); resolve(); };
                    tag.onerror = function () { restore(); loaded[name] = null; reject(new Error("Could not load " + name)); };
                    window.define = undefined;
                    document.head.appendChild(tag);
                }, function () { loaded[name] = null; reject(new Error("Could not load " + name)); });
            });
        }
        return loaded[name];
    }
    function renderMath(node, tex, display) {
        library("OuiKatex").then(function () {
            if (!node.isConnected) { return; }
            node.textContent = "";
            window.katex.render(tex, node, { displayMode: display, throwOnError: false, output: "mathml", trust: false, strict: "ignore", maxSize: 50, maxExpand: 1000 });
            node.classList.add("is-ready");
        }).catch(function () { });
    }
    var diagramQueue = Promise.resolve(), diagramId = 0;
    function renderDiagram(view, code, onFail) {
        diagramQueue = diagramQueue.then(function () { return library("OuiMermaid"); }).then(function () {
            if (!view.isConnected) { return; }
            var mm = window.mermaid || (window.__esbuild_esm_mermaid_nm && (window.__esbuild_esm_mermaid_nm.mermaid.default || window.__esbuild_esm_mermaid_nm.mermaid));
            var dark = getComputedStyle(view).color.match(/\d+/g).slice(0, 3).reduce(function (a, b) { return a + (+b); }, 0) > 380, uid = "oui-mermaid-" + (++diagramId);
            mm.initialize({ startOnLoad: false, securityLevel: "strict", theme: dark ? "dark" : "default", fontFamily: "inherit", maxTextSize: 50000 });
            return mm.render(uid, code).then(function (r) { view.innerHTML = r.svg; view.classList.add("is-ready"); }, function () { var junk = document.getElementById("d" + uid); if (junk) { junk.remove(); } onFail(); });
        }).catch(function () { onFail(); });
    }
    var FILE_LINK = /\.(pdf|csv|tsv|xlsx?|docx?|pptx?|zip|7z|rar|gz|tar|txt|json|xml|md|png|jpe?g|gif|webp|svg|mp3|mp4|wav)(?:$|[?#])/i;
    function humanSize(n) { return n < 1024 ? n + " B" : n < 1048576 ? (n / 1024).toFixed(n < 10240 ? 1 : 0) + " KB" : (n / 1048576).toFixed(1) + " MB"; }
    function dataBlob(uri) {
        var comma = uri.indexOf(","), head = uri.slice(5, comma), body = uri.slice(comma + 1);
        return /;base64/i.test(head) ? base64Blob(body) : new Blob([decodeURIComponent(body)], { type: "application/octet-stream" });
    }
    // Always octet-stream: the file is saved, never opened by the page, so an HTML or SVG file from a model cannot run here.
    function base64Blob(b64) {
        var bin = atob(b64.replace(/\s+/g, "")), bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) { bytes[i] = bin.charCodeAt(i); }
        return new Blob([bytes], { type: "application/octet-stream" });
    }
    function saveBlob(blob, name) { var url = URL.createObjectURL(blob), a = document.createElement("a"); a.href = url; a.download = name; document.body.appendChild(a); a.click(); a.remove(); setTimeout(function () { URL.revokeObjectURL(url); }, 4000); }

    OUI.render = {
        code: function (id, code, lang, numbers) {
            var el = byId(id); if (!el) { return; }
            clear(el);
            (code || "").replace(/\r\n/g, "\n").split("\n").forEach(function (line, i) {
                var row = h("div", "oui-code__line");
                if (numbers) { row.appendChild(h("span", "oui-code__num", String(i + 1))); }
                var text = h("span", "oui-code__text"); text.appendChild(highlightLine(line, (lang || "").toLowerCase())); row.appendChild(text);
                el.appendChild(row);
            });
        },
        diff: function (id, before, after, mode) {
            var el = byId(id); if (!el) { return; }
            clear(el);
            var a = (before || "").replace(/\r\n/g, "\n").split("\n"), b = (after || "").replace(/\r\n/g, "\n").split("\n");
            // Longest common subsequence table; inputs here are short (a code review, a config change).
            var n = a.length, m = b.length, L = [];
            for (var i = 0; i <= n; i++) { L.push(new Array(m + 1).fill(0)); }
            for (i = n - 1; i >= 0; i--) { for (var j = m - 1; j >= 0; j--) { L[i][j] = a[i] === b[j] ? L[i + 1][j + 1] + 1 : Math.max(L[i + 1][j], L[i][j + 1]); } }
            var rows = []; i = 0; j = 0;
            while (i < n || j < m) {
                if (i < n && j < m && a[i] === b[j]) { rows.push([" ", a[i], i + 1, j + 1]); i++; j++; }
                else if (j < m && (i >= n || L[i][j + 1] >= L[i + 1][j])) { rows.push(["+", b[j], null, j + 1]); j++; }
                else { rows.push(["-", a[i], i + 1, null]); i++; }
            }
            el.classList.toggle("is-split", mode === "split");
            rows.forEach(function (r) {
                var row = h("div", "oui-diff__row oui-diff__row--" + (r[0] === "+" ? "add" : r[0] === "-" ? "del" : "same"));
                row.appendChild(h("span", "oui-diff__num", r[2] === null ? "" : String(r[2])));
                row.appendChild(h("span", "oui-diff__num", r[3] === null ? "" : String(r[3])));
                row.appendChild(h("span", "oui-diff__sign", r[0] === " " ? "" : r[0]));
                var t = h("span", "oui-diff__text"); t.appendChild(highlightLine(r[1], "")); row.appendChild(t);
                el.appendChild(row);
            });
        },
        markdown: function (id, text) {
            // GitHub-flavoured Markdown, built as DOM (no innerHTML). Blocks nest: a list item or a quote can hold paragraphs, code, tables, lists.
            // Headings (# and underlined), paragraphs, bold, italic, strike, code, links, bare and <autolinks>, <br>, backslash escapes, lists, tasks,
            // quotes, GitHub alerts (> [!NOTE]), rules, fenced and indented code (language, highlighting, diff colours, Copy), tables.
            // An image is shown as a link, never loaded: a remote image in an AI answer would send data to that server when it loads.
            var el = byId(id); if (!el) { return; }
            clear(el);
            function safeHref(href) { return /^(https?:|mailto:|\/|#)/i.test(href) ? href : null; }
            var sources = [], notes = {}, noteOrder = [], prefix = id + "-note-";
            function link(label, href, plain, cls) {
                var a = h("a", cls), ok = safeHref(href);
                if (plain) { a.textContent = label; } else { inline(a, label); }
                if (/^data:/i.test(href)) { a.href = "#"; a.className = "oui-md__filelink"; a.addEventListener("click", function (e) { e.preventDefault(); saveBlob(dataBlob(href), a.textContent); }); return a; }
                if (ok) { a.href = ok; a.rel = "noopener noreferrer"; if (/^https?:/i.test(ok)) { a.target = "_blank"; if (cls !== "oui-md__image") { sources.push(ok); } } }
                return a;
            }
            function mathNode(tex, display) { var n = h(display ? "div" : "span", "oui-md__math" + (display ? " oui-md__math--block" : ""), tex); renderMath(n, tex, display); return n; }
            function jump(a, target) { a.href = "#"; a.addEventListener("click", function (e) { e.preventDefault(); var t = document.getElementById(target); if (t) { t.scrollIntoView({ block: "center", behavior: "smooth" }); } }); }
            function noteRef(key, raw) {
                if (notes[key] === undefined) { return document.createTextNode(raw); }
                var at = noteOrder.indexOf(key); if (at < 0) { noteOrder.push(key); at = noteOrder.length - 1; }
                var sup = h("sup", "oui-md__noteref"), a = h("a", null, String(at + 1)); a.id = prefix + "ref-" + at; jump(a, prefix + at); sup.appendChild(a); return sup;
            }
            function collectNotes(ls) {
                var out = [], fence = null, i = 0, m;
                while (i < ls.length) {
                    var f = /^ {0,3}(`{3,}|~{3,})/.exec(ls[i]);
                    if (fence) { if (closes(ls[i], fence)) { fence = null; } out.push(ls[i++]); continue; }
                    if (f) { fence = f[1]; out.push(ls[i++]); continue; }
                    if ((m = /^ {0,3}\[\^([^\]\s]+)\]:\s*(.*)$/.exec(ls[i]))) {
                        var body = [m[2]]; i++;
                        while (i < ls.length && /^ {2,}\S/.test(ls[i])) { body.push(ls[i].trim()); i++; }
                        notes[m[1]] = body; continue;
                    }
                    out.push(ls[i++]);
                }
                return out;
            }
            function fileCard(name, meta, action) {
                name = name.replace(/[*`]/g, "");
                var card = h("div", "oui-md__file"), ext = (name.split(".").pop() || "file").slice(0, 4), info = h("div", "oui-md__file-info");
                card.appendChild(h("span", "oui-md__file-kind", ext)); info.appendChild(h("span", "oui-md__file-name", name)); if (meta) { info.appendChild(h("span", "oui-md__file-meta", meta)); }
                card.appendChild(info); card.appendChild(action); return card;
            }
            function saveButton(blob, name) { var b = h("button", "oui-md__file-save", "Download"), clean = name.replace(/[*`]/g, "").replace(/^Download\s+/i, ""); b.type = "button"; b.addEventListener("click", function () { saveBlob(blob(), clean); }); return b; }
            function fileFromFence(name, rest, body) {
                var isB64 = /\bbase64\b/i.test(rest);
                var size = isB64 ? Math.floor(body.replace(/\s+/g, "").length * 3 / 4) : new Blob([body]).size;
                return fileCard(name, humanSize(size), saveButton(function () { return isB64 ? base64Blob(body) : new Blob([body], { type: "application/octet-stream" }); }, name));
            }
            // A paragraph that is only a link to a file is a download card; a link inside a sentence stays a link.
            function fileLinkCard(line) {
                var m = /^\[([^\]]+)\]\(([^)\s]+)\)$/.exec(line.trim()) || (/^https?:\/\/\S+$/.test(line.trim()) ? [0, null, line.trim()] : null);
                if (!m) { return null; }
                var href = m[2], isData = /^data:/i.test(href);
                if (!isData && !(safeHref(href) && FILE_LINK.test(href))) { return null; }
                var name = m[1] || decodeURIComponent(href.split(/[?#]/)[0].split("/").pop());
                if (isData) { return fileCard(name, "File", saveButton(function () { return dataBlob(href); }, name)); }
                var a = h("a", "oui-md__file-save", "Download"); a.href = href; a.setAttribute("download", name); a.rel = "noopener noreferrer"; a.target = "_blank"; sources.push(href);
                var host = ""; try { host = new URL(href, location.href).hostname; } catch (e) { }
                return fileCard(name, host, a);
            }
            function diagram(code) {
                var wrap = h("div", "oui-md__diagram"), view = h("div", "oui-md__diagram-view"), src = h("pre", "oui-md__pre oui-md__diagram-source", code);
                view.appendChild(src); wrap.appendChild(view);
                renderDiagram(view, code, function () { if (view.isConnected) { wrap.classList.add("is-failed"); } });
                return wrap;
            }
            var INLINE = new RegExp([
                "\\\\\\((?<m1>[\\s\\S]+?)\\\\\\)",
                "(?<![\\\\$\\w])\\$(?<m2>[^\\s$](?:[^$\\n]*?[^\\s$\\\\])?)\\$(?![\\d$])",
                "\\[\\^(?<fn>[^\\]\\s]+)\\]",
                "\\\\(?<esc>[\\\\`*_{}\\[\\]()#+\\-.!|~>])",
                "(?<tick>`+)(?<code>[\\s\\S]*?[^`])\\k<tick>(?!`)",
                "(?<br><br\\s*\\/?>)",
                "<(?<auto>https?:\\/\\/[^\\s<>]+|mailto:[^\\s<>]+)>",
                "!\\[(?<alt>[^\\]]*)\\]\\((?<src>[^)\\s]+)(?:\\s+\"[^\"]*\")?\\)",
                "\\[(?<label>[^\\]]+)\\]\\((?<href>[^)\\s]+)(?:\\s+\"[^\"]*\")?\\)",
                "\\*\\*\\*(?<bi>[^\\s*](?:[\\s\\S]*?[^\\s*])?)\\*\\*\\*",
                "\\*\\*(?<b1>[^\\s](?:[\\s\\S]*?[^\\s])?)\\*\\*",
                "(?<![\\w_])__(?<b2>[^\\s_](?:[\\s\\S]*?[^\\s_])?)__(?![\\w_])",
                "~~(?<del>[\\s\\S]+?)~~",
                "(?<![\\w*])\\*(?<i1>[^\\s*](?:[^*]*?[^\\s*])?)\\*(?![\\w*])",
                "(?<![\\w_])_(?<i2>[^\\s_](?:[^_]*?[^\\s_])?)_(?![\\w_])",
                "(?<url>https?:\\/\\/[^\\s<>()]+[^\\s<>().,;:!?'\"])"
            ].join("|"), "g");
            function inline(parent, s) {
                var last = 0, m, re = new RegExp(INLINE.source, "g");
                function wrap(tag, inner) { var n = h(tag); inline(n, inner); parent.appendChild(n); }
                while ((m = re.exec(s)) !== null) {
                    var g = m.groups;
                    if (m.index > last) { parent.appendChild(document.createTextNode(s.slice(last, m.index))); }
                    if (g.m1 !== undefined || g.m2 !== undefined) { parent.appendChild(mathNode(g.m1 !== undefined ? g.m1 : g.m2, false)); }
                    else if (g.fn !== undefined) { parent.appendChild(noteRef(g.fn, m[0])); }
                    else if (g.esc !== undefined) { parent.appendChild(document.createTextNode(g.esc)); }
                    else if (g.code !== undefined) { parent.appendChild(h("code", null, g.code.replace(/^ (.*) $/, "$1"))); }
                    else if (g.br !== undefined) { parent.appendChild(h("br")); }
                    else if (g.auto !== undefined) { parent.appendChild(link(g.auto, g.auto, true)); }
                    else if (g.src !== undefined) { parent.appendChild(link("Image: " + (g.alt || g.src), g.src, true, "oui-md__image")); }
                    else if (g.label !== undefined) { parent.appendChild(link(g.label, g.href)); }
                    else if (g.bi !== undefined) { var st = h("strong"), em = h("em"); inline(em, g.bi); st.appendChild(em); parent.appendChild(st); }
                    else if (g.b1 !== undefined || g.b2 !== undefined) { wrap("strong", g.b1 !== undefined ? g.b1 : g.b2); }
                    else if (g.del !== undefined) { wrap("del", g.del); }
                    else if (g.i1 !== undefined || g.i2 !== undefined) { wrap("em", g.i1 !== undefined ? g.i1 : g.i2); }
                    else if (g.url !== undefined) { parent.appendChild(link(g.url, g.url, true)); }
                    last = m.index + m[0].length;
                }
                if (last < s.length) { parent.appendChild(document.createTextNode(s.slice(last))); }
            }
            function inlineLines(parent, parts) { parts.forEach(function (part, k) { if (k) { parent.appendChild(h("br")); } inline(parent, part.replace(/\s+$/, "")); }); }
            // Splits a table row on pipes, leaving pipes in `code` and escaped pipes alone.
            function cells(row) {
                var s = row.trim().replace(/^\|/, ""), out = [], cur = "", ticks = 0;
                for (var i = 0; i < s.length; i++) {
                    var c = s[i];
                    if (c === "\\" && s[i + 1] === "|") { cur += ticks ? "\\|" : "\\|"; i++; continue; }
                    if (c === "`") { ticks = ticks ? 0 : 1; }
                    if (c === "|" && !ticks) { out.push(cur); cur = ""; continue; }
                    cur += c;
                }
                if (cur.trim() !== "" || !/\|\s*$/.test(s)) { out.push(cur); }
                return out.map(function (x) { return x.trim(); });
            }
            var isBlank = /^\s*$/, isRule = /^ {0,3}([-*_])( *\1){2,} *$/, isFence = /^( {0,3})(`{3,}|~{3,})\s*([^\s`]*)([^`]*)$/, isHead = /^ {0,3}(#{1,6})(?:\s+(.*?))??(?:\s+#+)?\s*$/,
                isItem = /^( *)([-*+]|\d{1,9}[.)])( +|$)(\[[ xX]\](?: +|$))?(.*)$/, isQuote = /^ {0,3}>/, isTableSep = /^ *\|? *:?-+:? *(\| *:?-+:? *)*\|? *$/, isUnder = /^ {0,3}(=+|-+) *$/;
            function indentOf(line) { return line.match(/^ */)[0].length; }
            function dedent(line, n) { var i = 0; while (i < n && line[i] === " ") { i++; } return line.slice(i); }
            function closes(line, fence) { var t = line.trim(); return t[0] === fence[0] && t.length >= fence.length && t.split(t[0]).join("") === ""; }
            function isTableAt(ls, i) { return ls[i].indexOf("|") >= 0 && i + 1 < ls.length && isTableSep.test(ls[i + 1]) && ls[i + 1].indexOf("-") >= 0 && cells(ls[i]).length === cells(ls[i + 1]).length; }
            function codeBlock(lang, body) {
                var wrap = h("div", "oui-md__codeblock"), bar = h("div", "oui-md__codebar"), copy = h("button", "oui-md__copy", "Copy"), low = (lang || "").toLowerCase();
                bar.appendChild(h("span", "oui-md__lang", lang || "text"));
                copy.type = "button";
                copy.addEventListener("click", function () {
                    var done = function () { copy.textContent = "Copied"; copy.classList.add("is-done"); setTimeout(function () { copy.textContent = "Copy"; copy.classList.remove("is-done"); }, 1400); };
                    if (navigator.clipboard) { navigator.clipboard.writeText(body).then(done, function () { }); }
                });
                bar.appendChild(copy); wrap.appendChild(bar);
                var pre = h("pre", "oui-md__pre"), code = h("code");
                body.split("\n").forEach(function (line, k) {
                    if (k) { code.appendChild(document.createTextNode("\n")); }
                    if (low === "diff" && /^[-+]/.test(line) && !/^(---|\+\+\+)/.test(line)) { code.appendChild(h("span", line[0] === "+" ? "oui-md__add" : "oui-md__del", line)); }
                    else { code.appendChild(highlightLine(line, low)); }
                });
                pre.appendChild(code); wrap.appendChild(pre);
                return wrap;
            }
            function table(ls, i, parent) {
                var head = cells(ls[i]), align = cells(ls[i + 1]).map(function (c) { return /^:-+:$/.test(c) ? "center" : /-:$/.test(c) ? "right" : /^:-/.test(c) ? "left" : ""; });
                var wrap = h("div", "oui-md__table"), tbl = h("table"), thead = h("thead"), tr = h("tr"), tbody = h("tbody");
                head.forEach(function (c, k) { var th = h("th"); if (align[k]) { th.style.textAlign = align[k]; } inline(th, c); tr.appendChild(th); });
                thead.appendChild(tr); tbl.appendChild(thead); i += 2;
                while (i < ls.length && ls[i].indexOf("|") >= 0 && !isBlank.test(ls[i])) {
                    var row = h("tr"), values = cells(ls[i]);
                    head.forEach(function (c, k) { var td = h("td"); if (align[k]) { td.style.textAlign = align[k]; } inline(td, values[k] || ""); row.appendChild(td); });
                    tbody.appendChild(row); i++;
                }
                tbl.appendChild(tbody); wrap.appendChild(tbl); parent.appendChild(wrap);
                return i;
            }
            var ALERTS = { NOTE: "Note", TIP: "Tip", IMPORTANT: "Important", WARNING: "Warning", CAUTION: "Caution" };
            function quote(ls, i, parent) {
                var inner = [];
                while (i < ls.length && (isQuote.test(ls[i]) || (inner.length && !isBlank.test(ls[i]) && !isBlank.test(inner[inner.length - 1]) && !startsBlock(ls, i)))) { inner.push(ls[i].replace(/^ {0,3}> ?/, "")); i++; }
                var bq = h("blockquote"), alert = /^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$/i.exec(inner[0] || "");
                if (alert) { var kind = alert[1].toUpperCase(); bq.className = "oui-md__alert oui-md__alert--" + kind.toLowerCase(); bq.appendChild(h("div", "oui-md__alert-title", ALERTS[kind])); inner.shift(); }
                blocks(inner, bq, false);
                parent.appendChild(bq);
                return i;
            }
            // A block that interrupts a paragraph. An ordered item does so only when it starts at 1, so "2024. was a good year" stays text.
            function startsBlock(ls, i) {
                var l = ls[i], m = isItem.exec(l);
                return isFence.test(l) || (isHead.test(l) && /#/.test(l)) || isRule.test(l) || isQuote.test(l) || isTableAt(ls, i) || (!!m && m[5] !== "" && (!/\d/.test(m[2]) || parseInt(m[2], 10) === 1));
            }
            function list(ls, at, parent) {
                var first = isItem.exec(ls[at]), ordered = /\d/.test(first[2]), base = first[1].length, type = first[2].slice(-1);
                var root = h(ordered ? "ol" : "ul");
                if (ordered && parseInt(first[2], 10) !== 1) { root.start = parseInt(first[2], 10); }
                while (at < ls.length) {
                    var m = isItem.exec(ls[at]);
                    if (!m || m[1].length !== base || isRule.test(ls[at]) || /\d/.test(m[2]) !== ordered || (!ordered && m[2] !== first[2]) || (ordered && m[2].slice(-1) !== type)) { break; }
                    var width = base + m[2].length + (m[3].length > 4 || !m[3].length ? 1 : m[3].length), body = [m[5]], fence = null, li = h("li"); at++;
                    if (m[4]) { var box = h("input"); box.type = "checkbox"; box.disabled = true; box.checked = /x/i.test(m[4]); li.className = "oui-md__task"; li.appendChild(box); }
                    var open = isFence.exec(m[5]); if (open) { fence = open[2]; }
                    while (at < ls.length) {
                        var line = ls[at], ind = indentOf(line);
                        if (fence) { if (closes(line, fence)) { fence = null; } body.push(dedent(line, width)); at++; continue; }
                        if (isBlank.test(line)) {
                            var n = at + 1; while (n < ls.length && isBlank.test(ls[n])) { n++; }
                            if (n < ls.length && indentOf(ls[n]) > base && !(isItem.test(ls[n]) && indentOf(ls[n]) === base)) { body.push(""); at++; continue; }
                            break;
                        }
                        var f = isFence.exec(line);
                        if (ind > base) { if (f) { fence = f[2]; } body.push(dedent(line, width)); at++; continue; }
                        // An unindented fenced block between the steps of a list stays in the step: models write this often.
                        if (f && ind === base && fenceThenItem(ls, at, f[2], base)) { fence = f[2]; body.push(line); at++; continue; }
                        if (isBlank.test(ls[at - 1]) || startsBlock(ls, at) || isItem.test(line)) { break; }
                        body.push(line.trim()); at++;   // lazy continuation of the paragraph
                    }
                    while (body.length && isBlank.test(body[body.length - 1])) { body.pop(); }
                    blocks(body, li, true);
                    root.appendChild(li);
                    var skip = at; while (skip < ls.length && isBlank.test(ls[skip])) { skip++; }
                    if (skip < ls.length && isItem.test(ls[skip]) && indentOf(ls[skip]) === base) { at = skip; } else { break; }
                }
                parent.appendChild(root);
                return at;
            }
            function fenceThenItem(ls, at, fence, base) {
                var n = at + 1; while (n < ls.length && !closes(ls[n], fence)) { n++; }
                n++; while (n < ls.length && isBlank.test(ls[n])) { n++; }
                return n < ls.length && isItem.test(ls[n]) && indentOf(ls[n]) === base;
            }
            // tight: inside a list item the first paragraph is not wrapped, so the item's text sits on the marker's line.
            function blocks(ls, parent, tight) {
                var i = 0, firstBlock = true;
                while (i < ls.length) {
                    var line = ls[i], m;
                    if (isBlank.test(line)) { i++; continue; }
                    var wasFirst = firstBlock; firstBlock = false;
                    if (!tight && indentOf(line) >= 4 && !isItem.test(line)) {
                        var code = []; while (i < ls.length && (indentOf(ls[i]) >= 4 || isBlank.test(ls[i]))) { code.push(dedent(ls[i], 4)); i++; }
                        while (code.length && isBlank.test(code[code.length - 1])) { code.pop(); }
                        parent.appendChild(codeBlock("", code.join("\n"))); continue;
                    }
                    if ((m = isFence.exec(line))) {
                        var fence = m[2], pad = m[1].length, body = []; i++;
                        while (i < ls.length && !closes(ls[i], fence)) { body.push(dedent(ls[i], pad)); i++; }
                        i++; while (body.length && isBlank.test(body[body.length - 1])) { body.pop(); }
                        var info = m[3], lower = info.toLowerCase(), text2 = body.join("\n");
                        if (lower === "mermaid") { parent.appendChild(diagram(text2)); }
                        else if (lower === "math" || lower === "latex" && /^\s*\\/.test(text2)) { parent.appendChild(mathNode(text2, true)); }
                        else if (/^file:/i.test(info)) { parent.appendChild(fileFromFence(info.slice(5), m[4], text2)); }
                        else { parent.appendChild(codeBlock(info, text2)); }
                        continue;
                    }
                    var dm = /^ {0,3}(\$\$|\\\[)(.*)$/.exec(line);
                    if (dm) {
                        var endMark = dm[1] === "$$" ? "$$" : "\\]", tex = [], rest = dm[2], closed = false; i++;
                        while (true) {
                            var e2 = rest.indexOf(endMark);
                            if (e2 >= 0) { tex.push(rest.slice(0, e2)); closed = true; break; }
                            tex.push(rest);
                            if (i >= ls.length) { break; }
                            rest = ls[i++];
                        }
                        parent.appendChild(mathNode(tex.join("\n").trim(), true)); if (closed || true) { continue; }
                    }
                    if ((m = isHead.exec(line)) && line.indexOf("#") >= 0) { var hd = h("h" + m[1].length); inline(hd, m[2] || ""); parent.appendChild(hd); i++; continue; }
                    if (isRule.test(line)) { parent.appendChild(h("hr")); i++; continue; }
                    if (isTableAt(ls, i)) { i = table(ls, i, parent); continue; }
                    if (isQuote.test(line)) { i = quote(ls, i, parent); continue; }
                    if (isItem.test(line) && isItem.exec(line)[5] !== "") { i = list(ls, i, parent); continue; }
                    // Small models write the file's name on the line above the block instead of in its info string.
                    var named = /^[*_`]*file:([^\s*`]+?)[*_`]*:?$/i.exec(line.trim());
                    if (named) {
                        var j = i + 1; while (j < ls.length && isBlank.test(ls[j])) { j++; }
                        var nf = j < ls.length ? isFence.exec(ls[j]) : null;
                        if (nf) {
                            var fileBody = []; j++;
                            while (j < ls.length && !closes(ls[j], nf[2])) { fileBody.push(dedent(ls[j], nf[1].length)); j++; }
                            parent.appendChild(fileFromFence(named[1], nf[4], fileBody.join("\n"))); i = j + 1; continue;
                        }
                    }
                    var card = fileLinkCard(line);
                    if (card) { parent.appendChild(card); i++; continue; }
                    var para = [line.trim()]; i++;
                    while (i < ls.length && !isBlank.test(ls[i]) && !startsBlock(ls, i) && !isUnder.test(ls[i])) { para.push(ls[i].trim()); i++; }
                    if (i < ls.length && isUnder.test(ls[i]) && !isTableAt(ls, i - 1)) {
                        var under = h(ls[i].trim()[0] === "=" ? "h1" : "h2"); inlineLines(under, para); parent.appendChild(under); i++; continue;
                    }
                    if (tight && wasFirst) { inlineLines(parent, para); continue; }
                    var p = h("p"); inlineLines(p, para); parent.appendChild(p);
                }
            }
            blocks(collectNotes((text || "").replace(/\r\n?/g, "\n").replace(/\t/g, "    ").split("\n")), el, false);
            if (noteOrder.length) {
                var foot = h("div", "oui-md__notes"), ol = h("ol");
                noteOrder.forEach(function (key, at) {
                    var li = h("li"); li.id = prefix + at; inlineLines(li, notes[key]);
                    var back = h("a", "oui-md__noteback", "\u21a9"); back.setAttribute("aria-label", "Back to the text"); jump(back, prefix + "ref-" + at); li.appendChild(document.createTextNode(" ")); li.appendChild(back);
                    ol.appendChild(li);
                });
                foot.appendChild(ol); el.appendChild(foot);
            }
            // Source chips: put data-oui-sources on a parent of the view. No favicons: they would be requests to those servers.
            if (sources.length && el.closest("[data-oui-sources]")) {
                var row = h("div", "oui-md__sources"), seen = {};
                sources.forEach(function (href) {
                    var host; try { host = new URL(href, location.href).hostname.replace(/^www\./, ""); } catch (e) { return; }
                    if (seen[host] || host === location.hostname || Object.keys(seen).length >= 8) { return; }
                    seen[host] = 1;
                    var chip = h("a", "oui-md__source"); chip.href = href; chip.target = "_blank"; chip.rel = "noopener noreferrer"; chip.title = href;
                    var ic = h("span", "oui-i oui-i--globe"); ic.setAttribute("aria-hidden", "true"); chip.appendChild(ic); chip.appendChild(h("span", null, host)); row.appendChild(chip);
                });
                if (row.firstChild) { el.appendChild(row); }
            }
        },
        json: function (id, text, depth) {
            var el = byId(id); if (!el) { return; }
            clear(el);
            var data;
            try { data = JSON.parse(text || "null"); } catch (e) { el.appendChild(h("div", "oui-json__error", "Not valid JSON: " + e.message)); return; }
            function node(key, value, level) {
                var row = h("div", "oui-json__row");
                if (key !== null) { row.appendChild(h("span", "oui-json__key", key)); row.appendChild(document.createTextNode(": ")); }
                if (value !== null && typeof value === "object") {
                    var isArray = Array.isArray(value), keys = Object.keys(value);
                    var det = h("details", "oui-json__node"); det.open = level < (depth || 2);
                    var sum = h("summary"); sum.appendChild(row);
                    row.appendChild(h("span", "oui-json__meta", (isArray ? "[" : "{") + " " + keys.length + (isArray ? " items ]" : " keys }")));
                    det.appendChild(sum);
                    var kids = h("div", "oui-json__kids");
                    keys.forEach(function (k) { kids.appendChild(node(isArray ? null : k, value[k], level + 1)); });
                    det.appendChild(kids);
                    return det;
                }
                var type = value === null ? "null" : typeof value;
                row.appendChild(h("span", "oui-json__" + type, type === "string" ? "\"" + value + "\"" : String(value)));
                return row;
            }
            el.appendChild(node(null, data, 0));
        },
        hex: function (id, text) {
            var el = byId(id); if (!el) { return; }
            clear(el);
            var bytes = new TextEncoder().encode(text || "");
            for (var off = 0; off < bytes.length; off += 16) {
                var row = h("div", "oui-hex__row"), hexs = [], chars = "";
                for (var k = 0; k < 16; k++) {
                    var b = bytes[off + k];
                    hexs.push(b === undefined ? "  " : (b < 16 ? "0" : "") + b.toString(16).toUpperCase());
                    chars += b === undefined ? "" : (b >= 32 && b < 127 ? String.fromCharCode(b) : ".");
                }
                row.appendChild(h("span", "oui-hex__off", ("0000000" + off.toString(16).toUpperCase()).slice(-8)));
                row.appendChild(h("span", "oui-hex__bytes", hexs.slice(0, 8).join(" ") + "  " + hexs.slice(8).join(" ")));
                row.appendChild(h("span", "oui-hex__chars", chars));
                el.appendChild(row);
            }
        },
        log: function (id, text, filter, follow) {
            var el = byId(id); if (!el) { return; }
            var atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 8;
            clear(el);
            var f = (filter || "").toLowerCase();
            (text || "").replace(/\r\n/g, "\n").split("\n").forEach(function (line) {
                if (!line || (f && line.toLowerCase().indexOf(f) < 0)) { return; }
                var level = /\b(ERROR|FATAL)\b/.test(line) ? "error" : /\bWARN(ING)?\b/.test(line) ? "warn" : /\bDEBUG\b/.test(line) ? "debug" : "info";
                el.appendChild(h("div", "oui-log__line oui-log__line--" + level, line));
            });
            if (follow && atBottom) { el.scrollTop = el.scrollHeight; }
        }
    };

    // Editor helpers: Tab inserts spaces; line numbers follow the text.
    OUI.editor = {
        mount: function (id, onBlur) {
            var root = byId(id); if (!root) { return; }
            var area = root.querySelector("textarea"), gutter = root.querySelector("[data-oui-gutter]");
            if (!area) { return; }
            function lines() { if (gutter) { var n = area.value.split("\n").length, t = ""; for (var i = 1; i <= n; i++) { t += i + "\n"; } gutter.textContent = t; } }
            function onKey(e) {
                if (e.key !== "Tab") { return; }
                e.preventDefault();
                var s = area.selectionStart, end = area.selectionEnd;
                area.value = area.value.slice(0, s) + "    " + area.value.slice(end);
                area.selectionStart = area.selectionEnd = s + 4; lines();
            }
            function onScroll() { if (gutter) { gutter.scrollTop = area.scrollTop; } }
            function blur() { onBlur(area.value); }
            area.addEventListener("keydown", onKey); area.addEventListener("input", lines); area.addEventListener("scroll", onScroll); area.addEventListener("blur", blur);
            lines();
            keep(root, function () { area.removeEventListener("keydown", onKey); area.removeEventListener("input", lines); area.removeEventListener("scroll", onScroll); area.removeEventListener("blur", blur); });
        },
        set: function (id, value) { var root = byId(id); var area = root && root.querySelector("textarea"); if (area && document.activeElement !== area) { area.value = value || ""; area.dispatchEvent(new Event("input")); } }
    };

    // Drag to reorder: children with data-oui-key; onChange gets the new key order, comma separated.
    OUI.reorder = {
        mount: function (id, onChange) {
            var root = byId(id); if (!root) { return; }
            var drag = null, startY = 0, placeholderIndex = 0;
            function items() { return Array.prototype.slice.call(root.querySelectorAll("[data-oui-key]")); }
            function down(e) {
                var handle = e.target.closest("[data-oui-handle]"); if (!handle || !root.contains(handle)) { return; }
                drag = handle.closest("[data-oui-key]"); if (!drag) { return; }
                e.preventDefault(); startY = e.clientY; drag.classList.add("is-dragging"); root.classList.add("is-sorting");
                handle.setPointerCapture(e.pointerId);
            }
            function move(e) {
                if (!drag) { return; }
                drag.style.transform = "translateY(" + (e.clientY - startY) + "px)";
                var list = items(), mid = e.clientY, target = null;
                list.forEach(function (it) { if (it === drag) { return; } var r = it.getBoundingClientRect(); it.classList.remove("is-over"); if (mid > r.top && mid < r.bottom) { target = it; } });
                if (target) { target.classList.add("is-over"); placeholderIndex = list.indexOf(target); }
            }
            function up() {
                if (!drag) { return; }
                var list = items(), from = list.indexOf(drag);
                list.forEach(function (it) { it.classList.remove("is-over"); });
                drag.style.transform = ""; drag.classList.remove("is-dragging"); root.classList.remove("is-sorting");
                var keys = list.map(function (it) { return it.getAttribute("data-oui-key"); });
                if (placeholderIndex !== from && placeholderIndex >= 0) { var k = keys.splice(from, 1)[0]; keys.splice(placeholderIndex, 0, k); onChange(keys.join(",")); }
                drag = null; placeholderIndex = -1;
            }
            function key(e) {
                var handle = e.target.closest("[data-oui-handle]"); if (!handle || (e.key !== "ArrowUp" && e.key !== "ArrowDown")) { return; }
                e.preventDefault();
                var it = handle.closest("[data-oui-key]"), list = items(), i = list.indexOf(it), j = i + (e.key === "ArrowUp" ? -1 : 1);
                if (j < 0 || j >= list.length) { return; }
                var keys = list.map(function (x) { return x.getAttribute("data-oui-key"); }); var k = keys.splice(i, 1)[0]; keys.splice(j, 0, k); onChange(keys.join(","));
            }
            root.addEventListener("pointerdown", down); root.addEventListener("pointermove", move); root.addEventListener("pointerup", up); root.addEventListener("pointercancel", up); root.addEventListener("keydown", key);
            keep(root, function () { root.removeEventListener("pointerdown", down); root.removeEventListener("pointermove", move); root.removeEventListener("pointerup", up); root.removeEventListener("pointercancel", up); root.removeEventListener("keydown", key); });
        }
    };

    // Swipe a row left to show its actions.
    OUI.swipe = {
        mount: function (id) {
            var root = byId(id); if (!root) { return; }
            var front = root.querySelector("[data-oui-front]"), actions = root.querySelector("[data-oui-actions]");
            if (!front || !actions) { return; }
            var x0 = 0, dx = 0, open = false, active = false;
            function down(e) { if (e.pointerType === "mouse" && e.button !== 0) { return; } active = true; x0 = e.clientX; front.style.transition = "none"; }
            function move(e) { if (!active) { return; } dx = Math.min(0, Math.max(-actions.offsetWidth - 24, e.clientX - x0 + (open ? -actions.offsetWidth : 0))); front.style.transform = "translateX(" + dx + "px)"; }
            function up() { if (!active) { return; } active = false; front.style.transition = ""; open = dx < -actions.offsetWidth / 2; front.style.transform = open ? "translateX(" + (-actions.offsetWidth) + "px)" : ""; root.classList.toggle("is-open", open); }
            front.addEventListener("pointerdown", down); window.addEventListener("pointermove", move); window.addEventListener("pointerup", up);
            keep(root, function () { front.removeEventListener("pointerdown", down); window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", up); });
        },
        close: function (id) { var root = byId(id); var front = root && root.querySelector("[data-oui-front]"); if (front) { front.style.transform = ""; root.classList.remove("is-open"); } }
    };

    OUI.contextMenu = {
        mount: function (targetId, panelId) {
            var t = byId(targetId); if (!t) { return; }
            function onMenu(e) { e.preventDefault(); OUI.floating.openAt(panelId, e.clientX, e.clientY); }
            function onKey(e) { if ((e.shiftKey && e.key === "F10") || e.key === "ContextMenu") { e.preventDefault(); var r = t.getBoundingClientRect(); OUI.floating.openAt(panelId, r.left + 12, r.top + 12); } }
            t.addEventListener("contextmenu", onMenu); t.addEventListener("keydown", onKey);
            keep(t, function () { t.removeEventListener("contextmenu", onMenu); t.removeEventListener("keydown", onKey); });
        }
    };

    // Countdown to an ISO date time; writes d, h, m, s into [data-oui-part] elements every second.
    OUI.countdown = {
        mount: function (id, iso, onDone) {
            var root = byId(id); if (!root) { return; }
            var target = new Date(iso).getTime(), timer = null, fired = false;
            function tick() {
                var left = Math.max(0, Math.floor((target - Date.now()) / 1000));
                var parts = { d: Math.floor(left / 86400), h: Math.floor(left % 86400 / 3600), m: Math.floor(left % 3600 / 60), s: left % 60 };
                Object.keys(parts).forEach(function (k) { var e = root.querySelector("[data-oui-part='" + k + "']"); if (e) { var v = (parts[k] < 10 ? "0" : "") + parts[k]; if (e.textContent !== v) { e.textContent = v; e.classList.remove("is-tick"); void e.offsetWidth; e.classList.add("is-tick"); } } });
                if (left === 0 && !fired) { fired = true; clearInterval(timer); onDone(); }
            }
            tick(); timer = setInterval(tick, 1000);
            keep(root, function () { clearInterval(timer); });
        }
    };
})();

/*! OUI floating layers: places [popover].oui-floating next to its trigger, and helpers to open, close and open at a point. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    if (OUI.floating) { return; }

    function anchorOf(pop) {
        if (pop._ouiPoint) { return null; }
        if (pop._ouiAnchor && document.body.contains(pop._ouiAnchor)) { return pop._ouiAnchor; }
        var id = pop.id;
        return id ? document.querySelector("[popovertarget=\"" + (window.CSS && CSS.escape ? CSS.escape(id) : id) + "\"]") : null;
    }

    function place(pop) {
        var gap = 6, w = pop.offsetWidth, h = pop.offsetHeight, top, left;
        var side = pop.getAttribute("data-side") || "bottom";
        var align = pop.getAttribute("data-align") || "start";
        if (pop._ouiPoint) {
            top = pop._ouiPoint.y; left = pop._ouiPoint.x;
            if (top + h > window.innerHeight - 8) { top = Math.max(8, top - h); }
        } else {
            var trigger = anchorOf(pop);
            if (!trigger) { return; }
            var r = trigger.getBoundingClientRect();
            if (side === "right" || side === "left") {
                left = side === "right" ? r.right + gap : r.left - w - gap;
                top = align === "end" ? r.bottom - h : align === "center" ? r.top + (r.height - h) / 2 : r.top;
            } else {
                top = side === "top" ? r.top - h - gap : r.bottom + gap;
                if (side !== "top" && top + h > window.innerHeight - 8 && r.top - h - gap > 8) { top = r.top - h - gap; }
                if (side === "top" && top < 8) { top = r.bottom + gap; }
                left = align === "end" ? r.right - w : align === "center" ? r.left + (r.width - w) / 2 : r.left;
                pop.style.setProperty("--oui-origin", top < r.top ? "bottom" : "top");
                if (pop.hasAttribute("data-match-width")) { pop.style.minWidth = r.width + "px"; }
            }
        }
        left = Math.max(8, Math.min(left, window.innerWidth - w - 8));
        top = Math.max(8, Math.min(top, window.innerHeight - h - 8));
        pop.style.top = top + "px";
        pop.style.left = left + "px";
    }

    function placeOpen() {
        var open = document.querySelectorAll(".oui-floating:popover-open");
        for (var i = 0; i < open.length; i++) { place(open[i]); }
    }

    document.addEventListener("toggle", function (e) {
        var p = e.target;
        if (!p || !p.classList || !p.classList.contains("oui-floating")) { return; }
        if (e.newState === "open") { place(p); var first = p.querySelector("[autofocus], input, [role=menuitem], button"); if (first && p.hasAttribute("data-focus")) { first.focus(); } }
        else { p._ouiPoint = null; }
    }, true);
    window.addEventListener("resize", placeOpen);
    window.addEventListener("scroll", placeOpen, true);

    OUI.floating = {
        place: place,
        open: function (id, anchorId) {
            var p = document.getElementById(id);
            if (!p || !p.showPopover) { return; }
            p._ouiPoint = null;
            p._ouiAnchor = anchorId ? document.getElementById(anchorId) : null;
            if (!p.matches(":popover-open")) { try { p.showPopover(); } catch (e) { } } else { place(p); }
        },
        openAt: function (id, x, y) {
            var p = document.getElementById(id);
            if (!p || !p.showPopover) { return; }
            p._ouiPoint = { x: x, y: y };
            if (p.matches(":popover-open")) { place(p); } else { p.showPopover(); }
        },
        close: function (id) { var p = document.getElementById(id); if (p && p.hidePopover && p.matches(":popover-open")) { p.hidePopover(); } },
        closeFrom: function (elementId) {
            var el = document.getElementById(elementId);
            var p = el && el.closest("[popover]");
            if (p && p.hidePopover && p.matches(":popover-open")) { p.hidePopover(); }
        }
    };
})();

/*! OUI inputs: set an OutSystems-bound input from code, clear, step, reveal, OTP, scrub, shortcut recorder, keys, wheel. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    if (OUI.inputs) { return; }

    function byId(id) { return id ? document.getElementById(id) : null; }
    function field(rootId) { var r = byId(rootId); return r ? r.querySelector("input:not([type=hidden]), textarea, select") : null; }
    function keep(el, cleanup) { if (el._ouiCleanup) { el._ouiCleanup(); } el._ouiCleanup = cleanup; }

    // Sets the value the way a user does, so the OutSystems variable bound to the input updates.
    function setValue(el, value) {
        var proto = el.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : el.tagName === "SELECT" ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
        var setter = Object.getOwnPropertyDescriptor(proto, "value").set;
        setter.call(el, value);
        el.dispatchEvent(new Event("input", { bubbles: true }));
        el.dispatchEvent(new Event("change", { bubbles: true }));
    }

    OUI.inputs = {
        set: function (rootId, value) { var el = field(rootId); if (el) { setValue(el, value); } },
        clear: function (rootId) { var el = field(rootId); if (el) { setValue(el, ""); el.focus(); } },
        step: function (rootId, direction, step, min, max) {
            var el = field(rootId);
            if (!el) { return; }
            var v = parseFloat(el.value || "0"); if (isNaN(v)) { v = 0; }
            var s = step || 1, next = v + direction * s;
            if (min !== null && min !== undefined && next < min) { next = min; }
            if (max !== null && max !== undefined && max > (min || -Infinity) && next > max) { next = max; }
            var decimals = (String(s).split(".")[1] || "").length;
            setValue(el, next.toFixed(decimals));
        },
        reveal: function (rootId) {
            var el = field(rootId);
            if (!el) { return false; }
            el.type = el.type === "password" ? "text" : "password";
            return el.type === "text";
        },
        autogrow: function (rootId) {
            var root = byId(rootId), el = field(rootId);
            if (!root || !el) { return; }
            function fit() { el.style.height = "auto"; el.style.height = el.scrollHeight + 2 + "px"; }
            el.addEventListener("input", fit); fit();
            keep(root, function () { el.removeEventListener("input", fit); });
        },
        focusOpen: function (inputId, panelId) {
            var el = byId(inputId);
            if (!el) { return; }
            // Opens on typing and on click, not on focus: closing the panel returns focus to the input.
            function open() { if (OUI.floating) { try { OUI.floating.open(panelId, inputId); } catch (e) { } } }
            el.addEventListener("click", open); el.addEventListener("input", open);
            keep(el, function () { el.removeEventListener("click", open); el.removeEventListener("input", open); });
        }
    };

    // Key handlers on one element: keys is { Enter: fn, Escape: fn, Backspace: fn } (Backspace only when the field is empty).
    OUI.keys = {
        mount: function (id, keys) {
            var el = byId(id);
            if (!el) { return; }
            function onKey(e) {
                var fn = keys[e.key];
                if (!fn) { return; }
                if (e.key === "Backspace" && el.value !== "") { return; }
                if (e.key === "Enter" || e.key === "Escape") { e.preventDefault(); }
                fn(el.value, e);
            }
            el.addEventListener("keydown", onKey);
            keep(el, function () { el.removeEventListener("keydown", onKey); });
        },
        focus: function (id) { var el = byId(id); if (el) { el.focus(); if (el.select) { el.select(); } } }
    };

    OUI.otp = {
        mount: function (rootId, onChange, onComplete) {
            var root = byId(rootId);
            if (!root) { return; }
            var boxes = Array.prototype.slice.call(root.querySelectorAll("input"));
            function value() { return boxes.map(function (b) { return b.value; }).join(""); }
            function emit() { var v = value(); onChange(v); if (v.length === boxes.length) { onComplete(v); } }
            function onInput(e) {
                var i = boxes.indexOf(e.target), chars = e.target.value.replace(/\D/g, "");
                if (chars.length > 1) {
                    for (var k = 0; k < chars.length && i + k < boxes.length; k++) { boxes[i + k].value = chars[k]; }
                    boxes[Math.min(i + chars.length, boxes.length - 1)].focus();
                } else {
                    e.target.value = chars;
                    if (chars && i < boxes.length - 1) { boxes[i + 1].focus(); }
                }
                root.classList.toggle("is-complete", value().length === boxes.length);
                emit();
            }
            function onKey(e) {
                var i = boxes.indexOf(e.target);
                if (e.key === "Backspace" && !e.target.value && i > 0) { boxes[i - 1].focus(); boxes[i - 1].value = ""; e.preventDefault(); emit(); }
                if (e.key === "ArrowLeft" && i > 0) { boxes[i - 1].focus(); }
                if (e.key === "ArrowRight" && i < boxes.length - 1) { boxes[i + 1].focus(); }
            }
            function onFocus(e) { e.target.select(); }
            boxes.forEach(function (b) { b.addEventListener("input", onInput); b.addEventListener("keydown", onKey); b.addEventListener("focus", onFocus); });
            keep(root, function () { boxes.forEach(function (b) { b.removeEventListener("input", onInput); b.removeEventListener("keydown", onKey); b.removeEventListener("focus", onFocus); }); });
        }
    };

    OUI.scrub = {
        mount: function (rootId, getValue, step, min, max, decimals, onChange) {
            var root = byId(rootId);
            if (!root) { return; }
            var handle = root.querySelector("[data-oui-scrub]") || root;
            var out = root.querySelector("[data-oui-output]");
            var startX = 0, startValue = 0, current = 0, dragging = false;
            function clamp(v) { if (min !== null && v < min) { v = min; } if (max !== null && max > min && v > max) { v = max; } return v; }
            function down(e) { dragging = true; startX = e.clientX; startValue = current = getValue(); handle.setPointerCapture(e.pointerId); root.classList.add("is-scrubbing"); }
            function move(e) {
                if (!dragging) { return; }
                var factor = e.shiftKey ? 10 : e.altKey ? 0.1 : 1;
                current = clamp(startValue + Math.round((e.clientX - startX) / 4) * step * factor);
                if (out) { out.textContent = current.toFixed(decimals); }
            }
            function up() { if (!dragging) { return; } dragging = false; root.classList.remove("is-scrubbing"); if (current !== startValue) { onChange(current); } }
            function key(e) {
                if (e.key !== "ArrowUp" && e.key !== "ArrowDown") { return; }
                e.preventDefault();
                onChange(clamp(getValue() + (e.key === "ArrowUp" ? step : -step) * (e.shiftKey ? 10 : 1)));
            }
            handle.addEventListener("pointerdown", down); handle.addEventListener("pointermove", move);
            handle.addEventListener("pointerup", up); handle.addEventListener("pointercancel", up); handle.addEventListener("keydown", key);
            keep(root, function () {
                handle.removeEventListener("pointerdown", down); handle.removeEventListener("pointermove", move);
                handle.removeEventListener("pointerup", up); handle.removeEventListener("pointercancel", up); handle.removeEventListener("keydown", key);
            });
        }
    };

    OUI.shortcut = {
        mount: function (id, onChange) {
            var el = byId(id);
            if (!el) { return; }
            var names = { " ": "Space", ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right", Escape: "Esc" };
            function onKey(e) {
                if (["Control", "Shift", "Alt", "Meta"].indexOf(e.key) >= 0) { return; }
                if (e.key === "Tab") { return; }
                e.preventDefault();
                if ((e.key === "Backspace" || e.key === "Delete") && !e.ctrlKey && !e.altKey && !e.metaKey) { onChange(""); return; }
                var parts = [];
                if (e.ctrlKey) { parts.push("Ctrl"); } if (e.altKey) { parts.push("Alt"); } if (e.shiftKey) { parts.push("Shift"); } if (e.metaKey) { parts.push("Meta"); }
                parts.push(names[e.key] || (e.key.length === 1 ? e.key.toUpperCase() : e.key));
                onChange(parts.join("+"));
            }
            el.addEventListener("keydown", onKey);
            keep(el, function () { el.removeEventListener("keydown", onKey); });
        }
    };

    OUI.strength = function (password) {
        var p = password || "", score = 0;
        if (p.length >= 8) { score++; }
        if (p.length >= 12) { score++; }
        if (/[a-z]/.test(p) && /[A-Z]/.test(p)) { score++; }
        if (/\d/.test(p) && /[^A-Za-z0-9]/.test(p)) { score++; }
        if (/^(.)\1+$/.test(p) || /^(password|qwerty|123456)/i.test(p)) { score = Math.min(score, 1); }
        return p.length === 0 ? 0 : Math.max(1, score);
    };

    OUI.wheel = {
        mount: function (rootId, onChange) {
            var root = byId(rootId);
            if (!root) { return; }
            var list = root.querySelector("[data-oui-wheel]") || root;
            var timer = null, last = null;
            function center() {
                var items = list.querySelectorAll("[data-oui-item]"), mid = list.getBoundingClientRect().top + list.clientHeight / 2, best = null, dist = Infinity;
                for (var i = 0; i < items.length; i++) {
                    var r = items[i].getBoundingClientRect(), d = Math.abs(r.top + r.height / 2 - mid);
                    items[i].classList.remove("is-center");
                    if (d < dist) { dist = d; best = items[i]; }
                }
                if (best) { best.classList.add("is-center"); }
                return best;
            }
            function settle() { var best = center(); if (best && best.getAttribute("data-oui-item") !== last) { last = best.getAttribute("data-oui-item"); onChange(last); } }
            function onScroll() { center(); clearTimeout(timer); timer = setTimeout(settle, 140); }
            var selected = list.querySelector(".is-selected");
            if (selected) { list.scrollTop = selected.offsetTop - list.clientHeight / 2 + selected.offsetHeight / 2; last = selected.getAttribute("data-oui-item"); }
            center();
            list.addEventListener("scroll", onScroll, { passive: true });
            keep(root, function () { clearTimeout(timer); list.removeEventListener("scroll", onScroll); });
        }
    };

    OUI.color = {
        mount: function (id, onChange) {
            var el = byId(id);
            if (!el) { return; }
            function commit() { onChange(el.value); }
            el.addEventListener("change", commit);
            keep(el, function () { el.removeEventListener("change", commit); });
        }
    };
})();

/*! OUI motion, media, capture and tool panels: typewriter, word rotate, rolling number, spotlight, dock, carousel,
    image compare, card stack, dropzone, signature pad, curve editor, gradient editor, timeline editor, flame graph, split pane. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    if (OUI.motion) { return; }
    function byId(id) { return id ? document.getElementById(id) : null; }
    function keep(el, cleanup) { if (el._ouiCleanup) { el._ouiCleanup(); } el._ouiCleanup = cleanup; }
    function reduced() { return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches; }
    function h(tag, cls, text) { var e = document.createElement(tag); if (cls) { e.className = cls; } if (text !== undefined) { e.textContent = text; } return e; }
    var NS = "http://www.w3.org/2000/svg";
    function sv(tag, attrs, parent) { var e = document.createElementNS(NS, tag); for (var k in attrs) { e.setAttribute(k, attrs[k]); } if (parent) { parent.appendChild(e); } return e; }

    OUI.motion = {
        typewriter: function (id, text, speed, loop) {
            var el = byId(id); if (!el) { return; }
            var out = el.querySelector("[data-oui-out]") || el, i = 0, timer = null, back = false;
            if (reduced()) { out.textContent = text; return; }
            function step() {
                out.textContent = text.slice(0, i);
                if (!back && i < text.length) { i++; timer = setTimeout(step, speed); }
                else if (loop && !back) { back = true; timer = setTimeout(step, 1600); }
                else if (loop && back && i > 0) { i--; timer = setTimeout(step, speed / 2); }
                else if (loop) { back = false; timer = setTimeout(step, 400); }
            }
            step();
            keep(el, function () { clearTimeout(timer); });
        },
        rotate: function (id, words, ms) {
            var el = byId(id); if (!el) { return; }
            var list = words.split(",").map(function (w) { return w.trim(); }).filter(Boolean), i = 0, out = el.querySelector("[data-oui-out]") || el;
            out.textContent = list[0] || "";
            if (reduced() || list.length < 2) { return; }
            var timer = setInterval(function () {
                out.classList.add("is-out");
                setTimeout(function () { i = (i + 1) % list.length; out.textContent = list[i]; out.classList.remove("is-out"); out.classList.add("is-in"); setTimeout(function () { out.classList.remove("is-in"); }, 360); }, 240);
            }, ms || 2200);
            keep(el, function () { clearInterval(timer); });
        },
        roll: function (id, value) {
            var el = byId(id); if (!el) { return; }
            var text = String(value), cols = el.querySelectorAll(".oui-roll__col");
            if (cols.length !== text.length) {
                while (el.firstChild) { el.removeChild(el.firstChild); }
                for (var k = 0; k < text.length; k++) {
                    if (!/\d/.test(text[k])) { el.appendChild(h("span", "oui-roll__sep", text[k])); continue; }
                    var col = h("span", "oui-roll__col"), strip = h("span", "oui-roll__strip");
                    for (var d = 0; d <= 9; d++) { strip.appendChild(h("span", "oui-roll__digit", String(d))); }
                    col.appendChild(strip); el.appendChild(col);
                }
                cols = el.querySelectorAll(".oui-roll__col");
            }
            var digits = text.replace(/\D/g, "");
            cols.forEach(function (c, i) { var strip = c.firstChild; strip.style.transform = "translateY(" + (-parseInt(digits[i], 10) * 10) + "%)"; });
            el.setAttribute("aria-label", text);
        },
        spotlight: function (id) {
            var el = byId(id); if (!el) { return; }
            function move(e) { var r = el.getBoundingClientRect(); el.style.setProperty("--oui-x", (e.clientX - r.left) + "px"); el.style.setProperty("--oui-y", (e.clientY - r.top) + "px"); }
            el.addEventListener("pointermove", move);
            keep(el, function () { el.removeEventListener("pointermove", move); });
        },
        dock: function (id) {
            var el = byId(id); if (!el) { return; }
            var items = el.querySelectorAll("[data-oui-dock]");
            function move(e) { if (reduced()) { return; } items.forEach(function (it) { var r = it.getBoundingClientRect(), d = Math.abs(e.clientX - (r.left + r.width / 2)); it.style.setProperty("--oui-s", String(1 + Math.max(0, 1 - d / 140) * .55)); }); }
            function leave() { items.forEach(function (it) { it.style.setProperty("--oui-s", "1"); }); }
            el.addEventListener("pointermove", move); el.addEventListener("pointerleave", leave);
            keep(el, function () { el.removeEventListener("pointermove", move); el.removeEventListener("pointerleave", leave); });
        }
    };

    OUI.media = {
        carousel: function (id, onChange) {
            var el = byId(id); if (!el) { return; }
            var track = el.querySelector("[data-oui-track]"); if (!track) { return; }
            function index() { var slides = track.children, best = 0, dist = Infinity; for (var i = 0; i < slides.length; i++) { var d = Math.abs(slides[i].offsetLeft - track.scrollLeft); if (d < dist) { dist = d; best = i; } } return best; }
            var timer = null;
            function onScroll() { clearTimeout(timer); timer = setTimeout(function () { onChange(index()); }, 120); }
            track.addEventListener("scroll", onScroll, { passive: true });
            el._ouiGo = function (i) { var s = track.children[Math.max(0, Math.min(track.children.length - 1, i))]; if (s) { track.scrollTo({ left: s.offsetLeft, behavior: reduced() ? "auto" : "smooth" }); } };
            keep(el, function () { track.removeEventListener("scroll", onScroll); clearTimeout(timer); });
        },
        go: function (id, i) { var el = byId(id); if (el && el._ouiGo) { el._ouiGo(i); } },
        compare: function (id) {
            var el = byId(id); if (!el) { return; }
            var range = el.querySelector("input[type=range]");
            function set() { el.style.setProperty("--oui-pos", range.value + "%"); }
            range.addEventListener("input", set); set();
            keep(el, function () { range.removeEventListener("input", set); });
        },
        stack: function (id, onSwipe) {
            var el = byId(id); if (!el) { return; }
            var x0 = 0, dx = 0, card = null;
            function top() { var cards = el.querySelectorAll("[data-oui-card]:not(.is-gone)"); return cards[cards.length - 1]; }
            function down(e) { card = top(); if (!card || !card.contains(e.target)) { card = null; return; } x0 = e.clientX; card.style.transition = "none"; card.setPointerCapture(e.pointerId); }
            function move(e) { if (!card) { return; } dx = e.clientX - x0; card.style.transform = "translateX(" + dx + "px) rotate(" + dx / 18 + "deg)"; }
            function up() { if (!card) { return; } card.style.transition = ""; if (Math.abs(dx) > 100) { card.classList.add("is-gone"); card.style.transform = "translateX(" + (dx > 0 ? 600 : -600) + "px) rotate(" + (dx > 0 ? 30 : -30) + "deg)"; onSwipe(dx > 0 ? "right" : "left"); } else { card.style.transform = ""; } card = null; dx = 0; }
            el.addEventListener("pointerdown", down); el.addEventListener("pointermove", move); el.addEventListener("pointerup", up); el.addEventListener("pointercancel", up);
            keep(el, function () { el.removeEventListener("pointerdown", down); el.removeEventListener("pointermove", move); el.removeEventListener("pointerup", up); el.removeEventListener("pointercancel", up); });
        },
        reset: function (id) { var el = byId(id); if (el) { el.querySelectorAll("[data-oui-card]").forEach(function (c) { c.classList.remove("is-gone"); c.style.transform = ""; }); } }
    };

    OUI.capture = {
        dropzone: function (id) {
            var el = byId(id); if (!el) { return; }
            var depth = 0;
            function enter(e) { e.preventDefault(); depth++; el.classList.add("is-over"); }
            function leave() { depth = Math.max(0, depth - 1); if (!depth) { el.classList.remove("is-over"); } }
            function over(e) { e.preventDefault(); }
            function drop(e) {
                depth = 0; el.classList.remove("is-over");
                var input = el.querySelector("input[type=file]");
                if (input && e.dataTransfer && e.dataTransfer.files.length && e.target !== input) { e.preventDefault(); input.files = e.dataTransfer.files; input.dispatchEvent(new Event("change", { bubbles: true })); }
            }
            el.addEventListener("dragenter", enter); el.addEventListener("dragleave", leave); el.addEventListener("dragover", over); el.addEventListener("drop", drop);
            keep(el, function () { el.removeEventListener("dragenter", enter); el.removeEventListener("dragleave", leave); el.removeEventListener("dragover", over); el.removeEventListener("drop", drop); });
        },
        signature: function (id, onChange) {
            var el = byId(id); if (!el) { return; }
            var canvas = el.querySelector("canvas"), ctx = canvas.getContext("2d"), drawing = false, last = null, dirty = false;
            function size() { var r = canvas.getBoundingClientRect(), dpr = window.devicePixelRatio || 1, img = dirty ? canvas.toDataURL() : null; canvas.width = r.width * dpr; canvas.height = r.height * dpr; ctx.scale(dpr, dpr); ctx.lineCap = "round"; ctx.lineJoin = "round"; ctx.lineWidth = 2.2; ctx.strokeStyle = getComputedStyle(el).color; if (img) { var i = new Image(); i.onload = function () { ctx.drawImage(i, 0, 0, r.width, r.height); }; i.src = img; } }
            function pt(e) { var r = canvas.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top, p: e.pressure || .5 }; }
            function down(e) { drawing = true; last = pt(e); canvas.setPointerCapture(e.pointerId); el.classList.add("is-signed"); }
            function move(e) { if (!drawing) { return; } var p = pt(e); ctx.lineWidth = 1.4 + p.p * 2; ctx.beginPath(); ctx.moveTo(last.x, last.y); var mx = (last.x + p.x) / 2, my = (last.y + p.y) / 2; ctx.quadraticCurveTo(last.x, last.y, mx, my); ctx.lineTo(p.x, p.y); ctx.stroke(); last = p; dirty = true; }
            function up() { if (!drawing) { return; } drawing = false; onChange(canvas.toDataURL("image/png")); }
            canvas.addEventListener("pointerdown", down); canvas.addEventListener("pointermove", move); canvas.addEventListener("pointerup", up); canvas.addEventListener("pointercancel", up);
            window.addEventListener("resize", size); size();
            el._ouiClear = function () { ctx.clearRect(0, 0, canvas.width, canvas.height); dirty = false; el.classList.remove("is-signed"); onChange(""); };
            keep(el, function () { canvas.removeEventListener("pointerdown", down); canvas.removeEventListener("pointermove", move); canvas.removeEventListener("pointerup", up); canvas.removeEventListener("pointercancel", up); window.removeEventListener("resize", size); });
        },
        clear: function (id) { var el = byId(id); if (el && el._ouiClear) { el._ouiClear(); } }
    };

    OUI.tools = {
        curve: function (id, value, onChange) {
            var el = byId(id); if (!el) { return; }
            while (el.firstChild) { el.removeChild(el.firstChild); }
            var v = (value || ".25,.1,.25,1").split(",").map(parseFloat), S = 200, pad = 20;
            var svg = sv("svg", { viewBox: "0 0 " + (S + pad * 2) + " " + (S + pad * 2), class: "oui-curve__svg", role: "img", "aria-label": "Curve" }, el);
            sv("rect", { x: pad, y: pad, width: S, height: S, class: "oui-curve__box" }, svg);
            var path = sv("path", { class: "oui-curve__path" }, svg), l1 = sv("line", { class: "oui-curve__arm" }, svg), l2 = sv("line", { class: "oui-curve__arm" }, svg);
            var h1 = sv("circle", { r: 7, class: "oui-curve__handle", tabindex: 0 }, svg), h2 = sv("circle", { r: 7, class: "oui-curve__handle", tabindex: 0 }, svg), ball = sv("circle", { r: 5, class: "oui-curve__ball" }, svg);
            function X(t) { return pad + t * S; } function Y(t) { return pad + (1 - t) * S; }
            function draw() {
                path.setAttribute("d", "M" + X(0) + "," + Y(0) + " C" + X(v[0]) + "," + Y(v[1]) + " " + X(v[2]) + "," + Y(v[3]) + " " + X(1) + "," + Y(1));
                l1.setAttribute("x1", X(0)); l1.setAttribute("y1", Y(0)); l1.setAttribute("x2", X(v[0])); l1.setAttribute("y2", Y(v[1]));
                l2.setAttribute("x1", X(1)); l2.setAttribute("y1", Y(1)); l2.setAttribute("x2", X(v[2])); l2.setAttribute("y2", Y(v[3]));
                h1.setAttribute("cx", X(v[0])); h1.setAttribute("cy", Y(v[1])); h2.setAttribute("cx", X(v[2])); h2.setAttribute("cy", Y(v[3]));
                ball.style.offsetPath = "path('" + path.getAttribute("d") + "')"; ball.style.animationTimingFunction = "cubic-bezier(" + v.join(",") + ")";
            }
            function drag(handle, ix) {
                handle.addEventListener("pointerdown", function (e) {
                    handle.setPointerCapture(e.pointerId);
                    function mv(ev) { var r = svg.getBoundingClientRect(), k = (S + pad * 2) / r.width; v[ix] = Math.max(0, Math.min(1, ((ev.clientX - r.left) * k - pad) / S)); v[ix + 1] = Math.max(-.5, Math.min(1.5, 1 - ((ev.clientY - r.top) * k - pad) / S)); draw(); }
                    function upf() { handle.removeEventListener("pointermove", mv); handle.removeEventListener("pointerup", upf); onChange(v.map(function (n) { return Math.round(n * 100) / 100; }).join(",")); }
                    handle.addEventListener("pointermove", mv); handle.addEventListener("pointerup", upf);
                });
            }
            drag(h1, 0); drag(h2, 2); draw();
        },
        gradient: function (id, value, onChange) {
            var el = byId(id); if (!el) { return; }
            while (el.firstChild) { el.removeChild(el.firstChild); }
            var stops = (value || "#6a4cf5 0,#e04a84 100").split(",").map(function (s) { var p = s.trim().split(/\s+/); return { c: p[0], at: parseFloat(p[1]) || 0 }; });
            var bar = h("div", "oui-gradient__bar"), css = h("code", "oui-gradient__css");
            el.appendChild(bar); el.appendChild(css);
            function text() { return stops.slice().sort(function (a, b) { return a.at - b.at; }).map(function (s) { return s.c + " " + Math.round(s.at); }).join(","); }
            function paint() { var g = "linear-gradient(90deg, " + stops.slice().sort(function (a, b) { return a.at - b.at; }).map(function (s) { return s.c + " " + s.at + "%"; }).join(", ") + ")"; bar.style.background = g; css.textContent = "background: " + g + ";"; }
            stops.forEach(function (s) {
                var knob = h("button", "oui-gradient__stop"); knob.type = "button"; knob.style.left = s.at + "%"; knob.style.background = s.c; knob.setAttribute("aria-label", "Stop " + s.c + " at " + s.at + "%");
                var picker = h("input"); picker.type = "color"; picker.value = s.c; picker.className = "oui-gradient__picker"; knob.appendChild(picker);
                knob.addEventListener("pointerdown", function (e) {
                    if (e.target === picker) { return; }
                    knob.setPointerCapture(e.pointerId); var moved = false;
                    function mv(ev) { moved = true; var r = bar.getBoundingClientRect(); s.at = Math.max(0, Math.min(100, (ev.clientX - r.left) / r.width * 100)); knob.style.left = s.at + "%"; paint(); }
                    function upf() { knob.removeEventListener("pointermove", mv); knob.removeEventListener("pointerup", upf); if (moved) { onChange(text()); } else { picker.click(); } }
                    knob.addEventListener("pointermove", mv); knob.addEventListener("pointerup", upf);
                });
                picker.addEventListener("input", function () { s.c = picker.value; knob.style.background = s.c; paint(); });
                picker.addEventListener("change", function () { onChange(text()); });
                bar.appendChild(knob);
            });
            paint();
        },
        timeline: function (id, tracks, length, onChange) {
            var el = byId(id); if (!el) { return; }
            while (el.firstChild) { el.removeChild(el.firstChild); }
            var total = length || 30, data = (tracks || "").split(";").map(function (t) { var p = t.split("|"); return { name: p[0], clips: (p[1] || "").split(",").filter(Boolean).map(function (c) { var q = c.split(":"); return { label: q[0], s: parseFloat(q[1]) || 0, d: parseFloat(q[2]) || 1 }; }) }; }).filter(function (t) { return t.name; });
            var ruler = h("div", "oui-tl__ruler"); for (var i = 0; i <= total; i += 5) { var tick = h("span", "oui-tl__tick", i + "s"); tick.style.left = (i / total * 100) + "%"; ruler.appendChild(tick); }
            var lanes = h("div", "oui-tl__lanes"); el.appendChild(h("div", "oui-tl__corner")); el.appendChild(ruler);
            function out() { return data.map(function (t) { return t.name + "|" + t.clips.map(function (c) { return c.label + ":" + Math.round(c.s * 10) / 10 + ":" + c.d; }).join(","); }).join(";"); }
            data.forEach(function (t, ti) {
                el.appendChild(h("div", "oui-tl__name", t.name));
                var lane = h("div", "oui-tl__lane");
                t.clips.forEach(function (c) {
                    var clip = h("div", "oui-tl__clip oui-tl__clip--" + (ti % 4), c.label); clip.style.left = (c.s / total * 100) + "%"; clip.style.width = (c.d / total * 100) + "%"; clip.tabIndex = 0;
                    clip.addEventListener("pointerdown", function (e) {
                        clip.setPointerCapture(e.pointerId); var x0 = e.clientX, s0 = c.s, w = lane.getBoundingClientRect().width;
                        function mv(ev) { c.s = Math.max(0, Math.min(total - c.d, s0 + (ev.clientX - x0) / w * total)); clip.style.left = (c.s / total * 100) + "%"; }
                        function upf() { clip.removeEventListener("pointermove", mv); clip.removeEventListener("pointerup", upf); onChange(out()); }
                        clip.addEventListener("pointermove", mv); clip.addEventListener("pointerup", upf);
                    });
                    lane.appendChild(clip);
                });
                el.appendChild(lane);
            });
        },
        flame: function (id, stacks, onPick) {
            var el = byId(id); if (!el) { return; }
            while (el.firstChild) { el.removeChild(el.firstChild); }
            var root = { name: "all", value: 0, kids: {} };
            (stacks || "").split(";").forEach(function (line) { var p = line.trim().split(" "); var v = parseFloat(p.pop()) || 0, frames = p.join(" ").split(">").map(function (f) { return f.trim(); }).filter(Boolean); root.value += v; var n = root; frames.forEach(function (f) { n.kids[f] = n.kids[f] || { name: f, value: 0, kids: {} }; n.kids[f].value += v; n = n.kids[f]; }); });
            var rowH = 24, depthMax = 0;
            function walk(n, x, w, d) {
                depthMax = Math.max(depthMax, d);
                var bar = h("button", "oui-flame__bar", n.name + " (" + Math.round(n.value / root.value * 100) + "%)"); bar.type = "button";
                bar.style.left = x + "%"; bar.style.width = "calc(" + w + "% - 1px)"; bar.style.top = (d * rowH) + "px"; bar.style.setProperty("--oui-heat", String((n.name.length * 37) % 60));
                bar.title = n.name + ": " + n.value; bar.addEventListener("click", function () { onPick(n.name); });
                el.appendChild(bar);
                var cx = x; Object.keys(n.kids).sort().forEach(function (k) { var c = n.kids[k], cw = w * c.value / (n.value || 1); walk(c, cx, cw, d + 1); cx += cw; });
            }
            walk(root, 0, 100, 0);
            el.style.height = ((depthMax + 1) * rowH) + "px";
        },
        split: function (id, onChange) {
            var el = byId(id); if (!el) { return; }
            var handle = el.querySelector("[data-oui-split]"), vertical = el.classList.contains("is-vertical");
            function down(e) {
                handle.setPointerCapture(e.pointerId); el.classList.add("is-resizing");
                function mv(ev) { var r = el.getBoundingClientRect(), p = vertical ? (ev.clientY - r.top) / r.height : (ev.clientX - r.left) / r.width; el.style.setProperty("--oui-split", Math.max(10, Math.min(90, p * 100)) + "%"); }
                function upf() { handle.removeEventListener("pointermove", mv); handle.removeEventListener("pointerup", upf); el.classList.remove("is-resizing"); onChange(parseFloat(el.style.getPropertyValue("--oui-split")) || 50); }
                handle.addEventListener("pointermove", mv); handle.addEventListener("pointerup", upf);
            }
            function key(e) { var cur = parseFloat(el.style.getPropertyValue("--oui-split")) || 50, d = { ArrowLeft: -5, ArrowUp: -5, ArrowRight: 5, ArrowDown: 5 }[e.key]; if (d) { e.preventDefault(); cur = Math.max(10, Math.min(90, cur + d)); el.style.setProperty("--oui-split", cur + "%"); onChange(cur); } }
            handle.addEventListener("pointerdown", down); handle.addEventListener("keydown", key);
            keep(el, function () { handle.removeEventListener("pointerdown", down); handle.removeEventListener("keydown", key); });
        }
    };
})();

/*! OUI overlays: dialogs and sheets (controlled), toasts (a promise per toast), hover cards, command palette, scroll to. */
(function () {
    var OUI = window.OUI = window.OUI || {};
    if (OUI.dialog) { return; }
    function byId(id) { return id ? document.getElementById(id) : null; }
    function keep(el, cleanup) { if (el._ouiCleanup) { el._ouiCleanup(); } el._ouiCleanup = cleanup; }
    var focusable = "button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])";

    // A dialog or sheet: a [popover=manual] element shown while the parent's IsOpen is true. Esc and the backdrop ask
    // the parent to close (onClose); the parent sets IsOpen to False, and sync hides it.
    OUI.dialog = {
        mount: function (id, onClose) {
            var el = byId(id);
            if (!el) { return; }
            function onKey(e) {
                if (!el.matches(":popover-open")) { return; }
                if (e.key === "Escape") { e.preventDefault(); onClose(); }
                if (e.key === "Tab") {
                    var items = el.querySelectorAll(focusable);
                    if (!items.length) { return; }
                    var first = items[0], last = items[items.length - 1];
                    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
                    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
                }
            }
            function onClick(e) { if (e.target === el && el.hasAttribute("data-dismissable")) { onClose(); } }
            document.addEventListener("keydown", onKey);
            el.addEventListener("click", onClick);
            keep(el, function () { document.removeEventListener("keydown", onKey); el.removeEventListener("click", onClick); if (el.matches(":popover-open")) { el.hidePopover(); } });
        },
        sync: function (id, open) {
            var el = byId(id);
            if (!el || !el.showPopover) { return; }
            var isOpen = el.matches(":popover-open");
            if (open && !isOpen) {
                el._ouiReturn = document.activeElement;
                try { el.showPopover(); } catch (e) { }
                var target = el.querySelector("[autofocus]") || el.querySelector(focusable);
                if (target) { setTimeout(function () { target.focus(); }, 30); }
            } else if (!open && isOpen) {
                el.hidePopover();
                if (el._ouiReturn && el._ouiReturn.focus) { el._ouiReturn.focus(); }
            }
        }
    };

    // Toasts: OUI.toast.show({ title, text, tone, duration, action }) resolves true when the action was clicked.
    OUI.toast = {
        host: function () {
            var host = document.getElementById("oui-toasts");
            if (!host) {
                host = document.createElement("div");
                host.id = "oui-toasts";
                host.className = "oui-toasts";
                host.setAttribute("role", "region");
                host.setAttribute("aria-label", "Notifications");
                host.setAttribute("popover", "manual");
                document.body.appendChild(host);
            }
            if (host.showPopover && !host.matches(":popover-open")) { try { host.showPopover(); } catch (e) { } }
            return host;
        },
        show: function (o) {
            return new Promise(function (resolve) {
                var host = OUI.toast.host();
                var toast = document.createElement("div");
                var tone = o.tone || "neutral";
                toast.className = "oui-toast oui-toast--" + tone;
                toast.setAttribute("role", tone === "danger" ? "alert" : "status");
                var icons = { success: "check-circle", danger: "x-circle", warning: "alert", info: "info", neutral: "bell" };
                toast.innerHTML = "<span class='oui-i oui-i--" + (icons[tone] || "bell") + " oui-toast__icon' aria-hidden='true'></span>" +
                    "<div class='oui-toast__body'><div class='oui-toast__title'></div><div class='oui-toast__text'></div></div>" +
                    (o.action ? "<button type='button' class='oui-btn oui-btn--secondary oui-btn--sm oui-toast__action'></button>" : "") +
                    "<button type='button' class='oui-toast__close' aria-label='Dismiss'><span class='oui-i oui-i--close' aria-hidden='true'></span></button>" +
                    (o.duration > 0 ? "<div class='oui-toast__timer'></div>" : "");
                toast.querySelector(".oui-toast__title").textContent = o.title || "";
                toast.querySelector(".oui-toast__text").textContent = o.text || "";
                if (!o.text) { toast.querySelector(".oui-toast__text").remove(); }
                var timerBar = toast.querySelector(".oui-toast__timer");
                if (timerBar) { timerBar.style.animationDuration = o.duration + "ms"; }
                var done = false, timer = null, startX = 0, dx = 0;
                function close(clicked) {
                    if (done) { return; }
                    done = true;
                    clearTimeout(timer);
                    toast.classList.add("is-leaving");
                    setTimeout(function () { toast.remove(); if (!host.children.length && host.hidePopover) { try { host.hidePopover(); } catch (e) { } } }, 220);
                    resolve(!!clicked);
                }
                function arm() { if (o.duration > 0) { timer = setTimeout(function () { close(false); }, o.duration); } }
                if (o.action) { var a = toast.querySelector(".oui-toast__action"); a.textContent = o.action; a.addEventListener("click", function () { close(true); }); }
                toast.querySelector(".oui-toast__close").addEventListener("click", function () { close(false); });
                toast.addEventListener("mouseenter", function () { clearTimeout(timer); toast.classList.add("is-paused"); });
                toast.addEventListener("mouseleave", function () { if (!done) { toast.classList.remove("is-paused"); arm(); } });
                toast.addEventListener("pointerdown", function (e) { startX = e.clientX; dx = 0; toast.setPointerCapture(e.pointerId); });
                toast.addEventListener("pointermove", function (e) { if (!startX) { return; } dx = Math.max(0, e.clientX - startX); toast.style.transform = "translateX(" + dx + "px)"; toast.style.opacity = String(1 - dx / 300); });
                toast.addEventListener("pointerup", function () { if (dx > 90) { close(false); } else { toast.style.transform = ""; toast.style.opacity = ""; } startX = 0; });
                host.appendChild(toast);
                while (host.children.length > 4) { host.firstChild.remove(); }
                arm();
            });
        }
    };

    // Hover card: opens a popover after a delay while the pointer or focus is on the trigger or the card.
    OUI.hover = {
        mount: function (triggerId, panelId, delay) {
            var t = byId(triggerId), p = byId(panelId);
            if (!t || !p) { return; }
            var openTimer = null, closeTimer = null;
            function open() { clearTimeout(closeTimer); openTimer = setTimeout(function () { OUI.floating.open(panelId, triggerId); }, delay || 450); }
            function close() { clearTimeout(openTimer); closeTimer = setTimeout(function () { OUI.floating.close(panelId); }, 160); }
            var pairs = [[t, "mouseenter", open], [t, "mouseleave", close], [t, "focusin", open], [t, "focusout", close], [p, "mouseenter", open], [p, "mouseleave", close]];
            pairs.forEach(function (x) { x[0].addEventListener(x[1], x[2]); });
            keep(t, function () { clearTimeout(openTimer); clearTimeout(closeTimer); pairs.forEach(function (x) { x[0].removeEventListener(x[1], x[2]); }); });
        }
    };

    // Command palette: Ctrl+K (or Cmd+K) opens it; arrows move, Enter runs, Esc closes.
    OUI.palette = {
        mount: function (panelId, inputId, onOpenChange) {
            var p = byId(panelId), input = byId(inputId);
            if (!p || !input) { return; }
            var index = 0;
            function items() { return Array.prototype.filter.call(p.querySelectorAll("[data-oui-command]"), function (b) { return b.offsetParent !== null; }); }
            function mark() { var list = items(); index = Math.max(0, Math.min(index, list.length - 1)); list.forEach(function (b, i) { b.classList.toggle("is-active", i === index); if (i === index) { b.scrollIntoView({ block: "nearest" }); } }); }
            function onGlobal(e) {
                if ((e.ctrlKey || e.metaKey) && (e.key === "k" || e.key === "K")) { e.preventDefault(); onOpenChange(!p.matches(":popover-open")); }
            }
            function onKey(e) {
                if (e.key === "ArrowDown") { e.preventDefault(); index++; mark(); }
                else if (e.key === "ArrowUp") { e.preventDefault(); index--; mark(); }
                else if (e.key === "Enter") { e.preventDefault(); var list = items(); if (list[index]) { list[index].click(); } }
                else { setTimeout(function () { index = 0; mark(); }, 0); }
            }
            document.addEventListener("keydown", onGlobal);
            input.addEventListener("keydown", onKey);
            keep(p, function () { document.removeEventListener("keydown", onGlobal); input.removeEventListener("keydown", onKey); });
            OUI.palette._mark = mark;
        }
    };

    // Scrolls an element with this id into view, inside OutSystems' scrolling screen container.
    OUI.scrollTo = function (id) {
        var el = byId(id) || document.querySelector("[data-oui-anchor='" + id + "']");
        if (el) { el.scrollIntoView({ behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth", block: "start" }); }
    };
})();

/* data-oui-style: Reactive drops CSS custom properties (--oui-*) from a style attribute, so the generator writes such
   styles into data-oui-style and this observer applies them with style.setProperty, before paint, and follows changes. */
(function () {
  function apply(el) {
    var text = el.getAttribute("data-oui-style") || "";
    var before = el.__ouiStyle || [];
    var now = [];
    text.split(";").forEach(function (decl) {
      var i = decl.indexOf(":");
      if (i < 1) { return; }
      var name = decl.slice(0, i).trim();
      el.style.setProperty(name, decl.slice(i + 1).trim());
      now.push(name);
    });
    before.forEach(function (name) { if (now.indexOf(name) < 0) { el.style.removeProperty(name); } });
    el.__ouiStyle = now;
  }
  function scan(root) {
    if (!root || root.nodeType !== 1) { return; }
    if (root.hasAttribute("data-oui-style")) { apply(root); }
    root.querySelectorAll("[data-oui-style]").forEach(apply);
  }
  new MutationObserver(function (records) {
    records.forEach(function (r) {
      if (r.type === "attributes") { apply(r.target); } else { r.addedNodes.forEach(scan); }
    });
  }).observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ["data-oui-style"] });
  scan(document.body);
  OUI.applyStyles = scan;
})();

/*! OUI icons: one CSS mask rule per icon, added once. Use them as <element class="oui-i oui-i--name">. */
(function () {
    if (document.getElementById("oui-icons")) { return; }
    var icons = {"sun":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='4'/><path d='M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41'/></svg>","moon":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M20.5 13.2A8.5 8.5 0 1 1 10.8 3.5a6.6 6.6 0 0 0 9.7 9.7z'/></svg>","globe":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM3 12h18M12 3c2.5 2.6 3.8 5.6 3.8 9s-1.3 6.4-3.8 9c-2.5-2.6-3.8-5.6-3.8-9S9.500 5.600 12 3z'/></svg>","check":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M5 12.5l4.5 4.5L19 7.5'/></svg>","chevron-down":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M6 9l6 6 6-6'/></svg>","chevron-right":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M9 6l6 6-6 6'/></svg>","chevron-left":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M15 6l-6 6 6 6'/></svg>","close":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M6 6l12 12M18 6L6 18'/></svg>","search":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='11' cy='11' r='7'/><path d='M20 20l-3.5-3.5'/></svg>","plus":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 5v14M5 12h14'/></svg>","minus":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M5 12h14'/></svg>","copy":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect x='9' y='9' width='11' height='11' rx='2'/><path d='M5 15V5a2 2 0 0 1 2-2h8'/></svg>","trash":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M4 7h16M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V4h6v3'/></svg>","edit":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M4 20h4L19 9l-4-4L4 16v4zM14 6l4 4'/></svg>","more":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='5' cy='12' r='1'/><circle cx='12' cy='12' r='1'/><circle cx='19' cy='12' r='1'/></svg>","info":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='9'/><path d='M12 11v5M12 8h.01'/></svg>","alert":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 3l9.5 17h-19L12 3zM12 10v4M12 17h.01'/></svg>","x-circle":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='9'/><path d='M9 9l6 6M15 9l-6 6'/></svg>","check-circle":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='9'/><path d='M8 12.5l2.8 2.8L16 10'/></svg>","star":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.8l-5.2 2.8 1-5.8-4.3-4.1 5.9-.9L12 3.5z'/></svg>","heart":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 20s-7-4.4-7-10a4 4 0 0 1 7-2.6A4 4 0 0 1 19 10c0 5.6-7 10-7 10z'/></svg>","user":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='8' r='4'/><path d='M4 20a8 8 0 0 1 16 0'/></svg>","settings":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='3'/><path d='M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z'/></svg>","send":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M5 12l14-7-5 14-2.5-5.5L5 12z'/></svg>","paperclip":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M20 11.5l-8.3 8.3a5 5 0 0 1-7-7L13 4.5a3.4 3.4 0 0 1 4.8 4.8l-8.3 8.3a1.7 1.7 0 0 1-2.4-2.4L14.7 7.6'/></svg>","upload":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 16V4M7 9l5-5 5 5M4 20h16'/></svg>","download":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 4v12M7 11l5 5 5-5M4 20h16'/></svg>","calendar":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect x='3.5' y='5' width='17' height='15' rx='2'/><path d='M8 3v4M16 3v4M3.5 10h17'/></svg>","clock":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='9'/><path d='M12 7v5l3 2'/></svg>","filter":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M4 5h16l-6 7.5V19l-4 1.5v-8L4 5z'/></svg>","sort":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M8 4v16M4 8l4-4 4 4M16 20V4M12 16l4 4 4-4'/></svg>","grip":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><circle cx='9' cy='6' r='1'/><circle cx='15' cy='6' r='1'/><circle cx='9' cy='12' r='1'/><circle cx='15' cy='12' r='1'/><circle cx='9' cy='18' r='1'/><circle cx='15' cy='18' r='1'/></svg>","folder":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M3.5 7a2 2 0 0 1 2-2H10l2 2h6.5a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2V7z'/></svg>","file":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M6 3h8l5 5v11a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM14 3v5h5'/></svg>","inbox":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M3.5 13l2.5-7h12l2.5 7v5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-5zM3.5 13H9l1 2h4l1-2h5.5'/></svg>","bell":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15L6 16zM10 20a2 2 0 0 0 4 0'/></svg>","menu":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M4 7h16M4 12h16M4 17h16'/></svg>","sidebar":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect x='3' y='4' width='18' height='16' rx='2'/><path d='M9 4v16'/></svg>","home":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M4 11l8-7 8 7v8a1 1 0 0 1-1 1h-4v-6h-6v6H5a1 1 0 0 1-1-1v-8z'/></svg>","chart":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M4 20V10M10 20V4M16 20v-7M22 20H2'/></svg>","sparkle":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8L12 3zM19 16l.8 2.2L22 19l-2.2.8L19 22l-.8-2.2L16 19l2.2-.8L19 16z'/></svg>","link":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1'/></svg>","eye":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z'/><circle cx='12' cy='12' r='3'/></svg>","eye-off":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M3 3l18 18M10.6 5.1A10 10 0 0 1 12 5c6.5 0 10 7 10 7a17 17 0 0 1-3 3.9M6.6 6.6A17 17 0 0 0 2 12s3.5 7 10 7a9.6 9.6 0 0 0 4.2-1M9.9 9.9a3 3 0 0 0 4.2 4.2'/></svg>","lock":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect x='4.5' y='10.5' width='15' height='10' rx='2'/><path d='M8 10.5V7a4 4 0 0 1 8 0v3.5'/></svg>","arrow-up":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 19V5M6 11l6-6 6 6'/></svg>","arrow-down":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 5v14M6 13l6 6 6-6'/></svg>","arrow-right":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M5 12h14M13 6l6 6-6 6'/></svg>","refresh":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M20 11a8 8 0 0 0-14.7-4.3L4 8.5M4 4v4.5h4.5M4 13a8 8 0 0 0 14.7 4.3l1.3-1.8M20 20v-4.5h-4.5'/></svg>","mic":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect x='9' y='3' width='6' height='11' rx='3'/><path d='M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21'/></svg>","play":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M7 4.5v15l12.5-7.5L7 4.5z'/></svg>","pause":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M8 5v14M16 5v14'/></svg>","thumbs-up":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M7 10v10H4V10h3zM7 10l4-7a2 2 0 0 1 2.8 2.2L13 9h5.5a2 2 0 0 1 2 2.3l-1.2 7A2 2 0 0 1 17.3 20H7'/></svg>","command":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M9 6a3 3 0 1 0-3 3h12a3 3 0 1 0-3-3v12a3 3 0 1 0 3-3H6a3 3 0 1 0 3 3V6z'/></svg>","database":"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='black' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><ellipse cx='12' cy='5.5' rx='7.5' ry='2.5'/><path d='M4.5 5.5v13c0 1.4 3.4 2.5 7.5 2.5s7.5-1.1 7.5-2.5v-13M4.5 12c0 1.4 3.4 2.5 7.5 2.5s7.5-1.1 7.5-2.5'/></svg>"};
    var css = "";
    for (var name in icons) {
        if (Object.prototype.hasOwnProperty.call(icons, name)) {
            css += ".oui-i--" + name + "{--oui-i:url(\"data:image/svg+xml," + encodeURIComponent(icons[name]) + "\")}";
        }
    }
    var style = document.createElement("style");
    style.id = "oui-icons";
    style.textContent = css;
    document.head.appendChild(style);
})();
