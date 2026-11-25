// SwVault web file browser. Talks to browser.py (same origin, session cookie); builds every element
// with the DOM API (no innerHTML), so file names and notes can't inject markup.
"use strict";

(function () {
  const PAGE = 200;
  const REFRESH_MS = 60000;

  const state = {
    me: null,
    index: null,          // {commit, vaultUrl, states, initialState, files: [...]}
    files: [],            // index files, with .lower, .folder, .name, .ext, .fields
    byPath: new Map(),    // lower path -> file
    whereUsed: new Map(), // lower child path -> [parent files]
    accounts: [],         // the Accounts page (vault admins)
    accountQuery: "",
    locks: new Map(),     // lower path -> {owner, lockedAt}
    cars: [],
    tree: null,
    expanded: new Set([""]),
    folder: "",
    query: "",
    type: "",
    status: "",
    results: [],
    shown: PAGE,
    selected: null,       // lower path
    tab: "props",
    historyFor: null,
    history: null,
    versionIndex: 0,
    reviewsFor: null,
    reviews: null,
    leads: null,
  };

  const $ = id => document.getElementById(id);

  // ------------------------------------------------------------ helpers

  function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs || {})) {
      if (value === null || value === undefined || value === false) continue;
      if (key === "class") el.className = value;
      else if (key.startsWith("on")) el.addEventListener(key.slice(2), value);
      else el.setAttribute(key, value === true ? "" : value);
    }
    for (const child of children.flat(Infinity)) {
      if (child === null || child === undefined || child === false) continue;
      el.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return el;
  }

  async function api(path, options = {}) {
    const init = { credentials: "same-origin", headers: {} };
    if (options.body !== undefined) {
      init.method = "POST";
      init.headers["Content-Type"] = "application/json";
      init.headers["X-SwVault"] = "1";
      init.body = JSON.stringify(options.body);
    }
    const response = await fetch(path, init);
    if (response.status === 401 && !options.allow401) {
      showSignIn("Your sign-in has expired. Sign in again.");
      throw new Error("signed out");
    }
    const data = response.headers.get("Content-Type")?.includes("json") ? await response.json() : null;
    if (!response.ok) throw new Error((data && data.error) || `HTTP ${response.status}`);
    return data;
  }

  let toastTimer;
  function toast(message, ms = 5000) {
    const el = $("toast");
    el.textContent = message;
    el.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => (el.hidden = true), ms);
  }

  const fileName = path => path.slice(path.lastIndexOf("/") + 1);
  const folderOf = path => (path.lastIndexOf("/") > 0 ? path.slice(0, path.lastIndexOf("/")) : "");
  const extOf = path => { const n = fileName(path); const i = n.lastIndexOf("."); return i > 0 ? n.slice(i).toLowerCase() : ""; };

  function formatDate(iso) {
    if (!iso) return "";
    const d = new Date(iso);
    return isNaN(d) ? iso : d.toLocaleString(undefined, { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
  }

  function formatSize(bytes) {
    if (!bytes) return "";
    if (bytes < 1024) return bytes + " B";
    if (bytes < 1048576) return (bytes / 1024).toFixed(1).replace(/\.0$/, "") + " KB";
    return (bytes / 1048576).toFixed(1).replace(/\.0$/, "") + " MB";
  }

  const isReleased = s => (s || "").toLowerCase() === "released";

  // The vault's address as people (and SwVault on their PCs) know it: this site's own address plus
  // the vault's path. The server reports the path with whatever address it talks to itself on.
  function publicVaultUrl() {
    try { return location.origin + new URL(state.index.vaultUrl).pathname.replace(/\/+$/, ""); } catch (e) { return ""; }
  }
  function publicUrl(url) {
    try { const u = new URL(url); return location.origin + u.pathname + u.search + u.hash; } catch (e) { return url; }
  }

  function link(action, path) {
    const vault = publicVaultUrl() ? "vault=" + encodeURIComponent(publicVaultUrl()) + "&" : "";
    return `swvault://${action}?${vault}path=${encodeURIComponent(path)}`;
  }

  const thumbUrl = (file, ref) => `api/thumb?path=${encodeURIComponent(file.path)}` + (ref ? `&ref=${ref}` : `&v=${encodeURIComponent((file.oid || "").slice(0, 12) + (state.index.commit || "").slice(0, 8))}`);
  const downloadUrl = (path, ref, version) => `api/download?path=${encodeURIComponent(path)}` + (ref ? `&ref=${ref}&version=${version}` : "");

  // The formats SOLIDWORKS opens: kind -> "label:ext ext ...". Keep in step with CadFormats.cs
  // (src/SwVault.Protocol), which the SOLIDWORKS add-in uses for the same decisions.
  const CAD = new Map();
  for (const [kind, formats] of Object.entries({
    part: "PART:sldprt", asm: "ASM:sldasm", drw: "DRW:slddrw",
    neutral: "STEP:step stp stpz|IGES:igs iges|X_T:x_t xmt_txt|X_B:x_b xmt_bin|SAT:sat|SAB:sab|VDA:vda|IFC:ifc|JT:jt|3DXML:3dxml|IDF:emn brd bdf idb",
    foreign: "PRT:prt|ASM:asm|CREO:xpr xas|IPT:ipt|IAM:iam|PAR:par|PSM:psm|CATIA:catpart catproduct|CGR:cgr|3DM:3dm|CKD:ckd",
    mesh: "STL:stl|3MF:3mf|OBJ:obj|PLY:ply|OFF:off|AMF:amf|VRML:wrl",
    flat: "DXF:dxf|DWG:dwg",
    swother: "TMPL:prtdot asmdot drwdot|SHEET:slddrt|LIB:sldlfp|FORM:sldftp|BLOCK:sldblk",
  }))
    for (const entry of formats.split("|")) {
      const [label, extensions] = entry.split(":");
      for (const e of extensions.split(" ")) CAD.set("." + e, { kind, label });
    }
  const TYPE_GROUPS = [["Parts", ["part"]], ["Assemblies", ["asm"]], ["Drawings", ["drw"]], ["STEP, IGES, Parasolid...", ["neutral"]],
    ["Other CAD systems", ["foreign"]], ["Meshes (STL, 3MF, OBJ)", ["mesh"]], ["2D (DXF, DWG, PDF)", ["flat"], ".pdf"], ["Templates and library", ["swother"]]]
    .map(([label, kinds, extra]) => [label, [...CAD].filter(([, f]) => kinds.includes(f.kind)).map(([e]) => e).concat(extra || []).join(",")]);

  // Creo numbers its saves ("bracket.prt.12"): the type is the extension before the number.
  function cadExt(path) {
    const ext = extOf(path);
    if (!/^\.\d+$/.test(ext)) return ext;
    const inner = extOf(path.slice(0, -ext.length));
    return inner === ".prt" || inner === ".asm" ? inner : ext;
  }
  const cadOf = path => CAD.get(cadExt(path));
  const isNative = path => [".sldprt", ".sldasm", ".slddrw"].includes(extOf(path));

  function placeholder(path, size) {
    const ext = extOf(path), cad = cadOf(path);
    const [label, kind] = cad ? [cad.label, cad.kind] : [(ext.slice(1) || "FILE").toUpperCase().slice(0, 5), "other"];
    return h("div", { class: `thumb placeholder ${kind}` + (size ? " " + size : "") }, label);
  }

  // A padlock over the picture of a checked-out file: green when it's yours, red when someone else's.
  function lockMark(f) {
    const lock = state.locks.get(f.lower);
    if (!lock) return null;
    const mine = state.me && (lock.owner || "").toLowerCase() === state.me.login.toLowerCase();
    const ns = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(ns, "svg");
    svg.setAttribute("viewBox", "0 0 16 16");
    svg.setAttribute("aria-hidden", "true");
    const path = document.createElementNS(ns, "path");
    path.setAttribute("fill-rule", "evenodd");
    path.setAttribute("d", "M8 1.5a3.5 3.5 0 0 0-3.5 3.5v2H4a1.5 1.5 0 0 0-1.5 1.5v4.5A1.5 1.5 0 0 0 4 14.5h8a1.5 1.5 0 0 0 1.5-1.5V8.5A1.5 1.5 0 0 0 12 7h-.5V5A3.5 3.5 0 0 0 8 1.5zm-2 3.5a2 2 0 1 1 4 0v2H6V5z");
    svg.append(path);
    const label = mine ? "Checked out by you" : "Checked out by " + lock.owner;
    return h("span", { class: "lock-mark " + (mine ? "mine" : "locked"), role: "img", "aria-label": label, title: label + (lock.lockedAt ? ", since " + formatDate(lock.lockedAt) : "") }, svg);
  }

  const marked = (file, thumb) => h("div", { class: "thumb-wrap" }, thumb, lockMark(file));

  function thumbnail(file, ref, forceTry) {
    if (!file.thumb && !forceTry) return placeholder(file.path);
    const box = h("div", { class: "thumb" });
    const img = h("img", { src: thumbUrl(file, ref), alt: "", loading: "lazy", decoding: "async" });
    img.addEventListener("error", () => box.replaceWith(placeholder(file.path)));
    box.append(img);
    return box;
  }

  // ------------------------------------------------------------ data

  function searchFields(file) {
    const fields = [
      { label: Fuzzy.NAME_LABEL, value: file.name, weight: 1.0 },
      { label: "Folder", value: file.folder, weight: 0.6 },
      { label: "Type", value: file.ext.slice(1), weight: 0.5 },
    ];
    for (const [key, value] of Object.entries(file.properties || {})) if (value && String(value).trim()) fields.push({ label: key, value: String(value), weight: 0.85 });
    for (const c of file.configurations || []) fields.push({ label: "Configuration", value: c, weight: 0.6 });
    if (file.comment) fields.push({ label: "Comment", value: file.comment, weight: 0.5 });
    if (file.by) fields.push({ label: "By", value: file.by, weight: 0.5 });
    if (file.revision) fields.push({ label: "Revision", value: file.revision, weight: 0.5 });
    if (file.state) fields.push({ label: "State", value: file.state, weight: 0.5 });
    const lock = state.locks.get(file.lower);
    if (lock) fields.push({ label: "Checked out by", value: lock.owner || "", weight: 0.5 });
    return fields;
  }

  function useIndex(index) {
    state.index = index;
    state.files = index.files.map(f => Object.assign(f, {
      lower: f.path.toLowerCase(), name: fileName(f.path), folder: folderOf(f.path), ext: extOf(f.path),
    }));
    state.byPath = new Map(state.files.map(f => [f.lower, f]));
    state.whereUsed = new Map();
    for (const f of state.files)
      for (const r of f.references || []) {
        const key = (r.path || "").toLowerCase();
        if (!state.whereUsed.has(key)) state.whereUsed.set(key, []);
        state.whereUsed.get(key).push(f);
      }
    // How many assemblies deep each file sits (0: no assembly uses it), for the default order.
    const levels = new Map();
    const levelOf = (lower, depth) => {
      if (levels.has(lower)) return levels.get(lower);
      let level = 0;
      if (depth < 50)
        for (const parent of state.whereUsed.get(lower) || [])
          if (parent.ext === ".sldasm") level = Math.max(level, levelOf(parent.lower, depth + 1) + 1);
      if (depth === 0) levels.set(lower, level);
      return level;
    };
    for (const f of state.files) f.level = levelOf(f.lower, 0);
    state.tree = buildTree(state.files, state.cars);
    refreshFields();
    const name = index.vault || index.team || "SwVault";
    $("home").title = name;
    $("home").setAttribute("aria-label", name);
    document.title = name + " files";
    fillStatusFilter();
  }

  function refreshFields() {
    for (const f of state.files) f.fields = searchFields(f);
  }

  function buildTree(files, cars) {
    const root = { name: "", path: "", children: new Map(), count: 0 };
    // The node for a folder, adding it and its parents; each one on the way counts `files` more files.
    function ensure(folder, files) {
      let node = root;
      let path = "";
      for (const part of folder.split("/").filter(Boolean)) {
        path = path ? path + "/" + part : part;
        if (!node.children.has(part.toLowerCase())) node.children.set(part.toLowerCase(), { name: part, path, children: new Map(), count: 0 });
        node = node.children.get(part.toLowerCase());
        node.count += files;
      }
    }
    for (const f of files) {
      root.count++;
      if (f.folder) ensure(f.folder, 1);
    }
    // The vault only carries folders that hold files. Projects, subsystems and the folders inside
    // them are folders too, and show here while they are still empty.
    for (const car of cars || []) {
      if (car.folder) ensure(car.folder, 0);
      for (const s of car.subsystems || []) {
        if (!s.folder) continue;
        ensure(s.folder, 0);
        for (const sub of s.folders || []) ensure(s.folder + "/" + sub, 0);
      }
    }
    return root;
  }

  async function loadAll() {
    const [index, locks] = await Promise.all([api("api/index"), api("api/locks").catch(() => ({ locks: [] }))]);
    state.locks = new Map((locks.locks || []).map(l => [(l.path || "").toLowerCase(), l]));
    useIndex(index);
    api("api/cars").then(c => {
      state.cars = c.cars || [];
      state.tree = buildTree(state.files, state.cars);
      renderFolders();
      if (state.selected) renderDetails();
    }).catch(() => {});
  }

  async function refresh() {
    try {
      const commit = state.index && state.index.commit;
      await loadAll();
      if (state.index.commit !== commit) { state.historyFor = null; state.reviewsFor = null; }
      runSearch(false);
      if (state.selected) renderDetails();
    } catch (e) {
      if (e.message !== "signed out") console.warn(e);
    }
  }

  // ------------------------------------------------------------ search

  function fillStatusFilter() {
    const select = $("status");
    const current = state.status;
    select.replaceChildren(
      h("option", { value: "" }, "Any status"),
      h("option", { value: "mine" }, "My check-outs"),
      h("option", { value: "locked" }, "Checked out (anyone)"),
      ...(state.index.states || []).map(s => h("option", { value: "state:" + s }, s)),
    );
    select.value = current;
  }

  function passesFilters(f) {
    if (state.type && !state.type.split(",").includes(cadExt(f.path))) return false;
    if (state.folder && !(f.lower.startsWith(state.folder.toLowerCase() + "/"))) return false;
    if (state.status) {
      const lock = state.locks.get(f.lower);
      if (state.status === "mine" && !(lock && state.me && (lock.owner || "").toLowerCase() === state.me.login.toLowerCase())) return false;
      if (state.status === "locked" && !lock) return false;
      if (state.status.startsWith("state:") && (f.state || "").toLowerCase() !== state.status.slice(6).toLowerCase()) return false;
    }
    return true;
  }

  function runSearch(resetPaging = true) {
    if (!state.index) return;
    const query = Fuzzy.parseQuery(state.query);
    const results = [];
    for (const f of state.files) {
      if (!passesFilters(f)) continue;
      const hit = query.score(f.fields);
      if (hit) results.push({ file: f, hit });
    }
    if (query.isEmpty) {
      // Assemblies first with the top level at the top, then parts, drawings and everything else.
      const rank = f => (f.ext === ".sldasm" ? 0 : f.ext === ".sldprt" ? 1 : f.ext === ".slddrw" ? 2 : 3);
      results.sort((a, b) => rank(a.file) - rank(b.file)
        || (rank(a.file) === 0 ? a.file.level - b.file.level : 0)
        || a.file.name.localeCompare(b.file.name, undefined, { sensitivity: "base", numeric: true })
        || a.file.lower.localeCompare(b.file.lower));
    } else {
      results.sort((a, b) => b.hit.score - a.hit.score || a.file.path.length - b.file.path.length || a.file.lower.localeCompare(b.file.lower));
    }
    state.results = results;
    if (resetPaging) state.shown = PAGE;
    renderResults(query.isEmpty);
    renderFolders();
    saveHash();
  }

  function highlight(text, positions) {
    if (!positions || positions.length === 0) return [text];
    const set = new Set(positions);
    const parts = [];
    let run = "", marked = false;
    for (let i = 0; i < text.length; i++) {
      const m = set.has(i);
      if (m !== marked && run) { parts.push(marked ? h("mark", null, run) : run); run = ""; }
      marked = m;
      run += text[i];
    }
    if (run) parts.push(marked ? h("mark", null, run) : run);
    return parts;
  }

  function statusChips(f, compact) {
    const chips = [];
    if (f.version) chips.push(h("span", { class: "badge plain" }, "v" + f.version));
    if (f.revision) chips.push(h("span", { class: "badge plain" }, "Rev " + f.revision));
    if (f.state) chips.push(h("span", { class: "badge " + (isReleased(f.state) ? "released" : /review/i.test(f.state) ? "review" : "plain") }, f.state));
    const lock = state.locks.get(f.lower);
    if (lock) {
      const mine = state.me && (lock.owner || "").toLowerCase() === state.me.login.toLowerCase();
      chips.push(h("span", { class: "badge " + (mine ? "mine" : "locked"), title: lock.lockedAt ? "since " + formatDate(lock.lockedAt) : null }, (mine ? "Checked out by you" : "Checked out by " + lock.owner)));
    }
    if (!compact && f.approvedBy) chips.push(h("span", { class: "badge approved" }, "Release approved by " + f.approvedBy));
    return chips;
  }

  function renderResults(emptyQuery) {
    const grid = $("grid");
    const visible = state.results.slice(0, state.shown);
    grid.replaceChildren(...visible.map(({ file, hit }) => {
      const description = file.properties && (file.properties.Description || file.properties.description);
      const matchLine = hit.matchLabel ? `${hit.matchLabel}: ${hit.matchValue}` : description || file.comment || "";
      const card = h("button", {
        class: "card" + (file.lower === state.selected ? " selected" : ""), type: "button", title: file.path,
        onclick: () => select(file.lower, true),
      },
        marked(file, thumbnail(file)),
        h("div", { class: "body" },
          h("div", { class: "name" }, highlight(file.name, hit.namePositions)),
          h("div", { class: "line" }, file.folder || "(vault root)"),
          h("div", { class: "chips" }, statusChips(file, true)),
          matchLine ? h("div", { class: "line" }, String(matchLine).replace(/\s+/g, " ")) : null));
      return card;
    }));
    const total = state.results.length;
    const where = state.folder ? ` in ${state.folder}` : "";
    $("summary").textContent = total === 0 ? `No matches${where}.`
      : `${total} file${total === 1 ? "" : "s"}${where}` + (emptyQuery ? ", assemblies first" : "");
    $("clear-folder").hidden = !state.folder;
    $("clear-folder").textContent = state.folder ? "Whole vault ×" : "";
    $("more").hidden = total <= state.shown;
    $("more").textContent = `Show more (${total - state.shown} left)`;
  }

  function renderFolders() {
    const nav = $("folders");
    function node(n) {
      const children = [...n.children.values()].sort((a, b) => a.name.localeCompare(b.name));
      const open = state.expanded.has(n.path.toLowerCase());
      const row = h("div", {
        class: "folder" + (n.path.toLowerCase() === state.folder.toLowerCase() ? " selected" : ""),
        onclick: e => {
          if (e.target.classList.contains("twisty") && children.length) toggle(n.path);
          else selectFolder(n.path);
        },
      },
        h("span", { class: "twisty" }, children.length ? (open ? "▾" : "▸") : ""),
        h("span", null, n.path ? n.name : (state.index.vault || "Vault")),
        h("span", { class: "count" }, n.count));
      return h("li", null, row, open && children.length ? h("ul", null, children.map(node)) : null);
    }
    nav.replaceChildren(h("ul", null, node(state.tree)));
  }

  function toggle(path) {
    const key = path.toLowerCase();
    if (state.expanded.has(key)) state.expanded.delete(key); else state.expanded.add(key);
    renderFolders();
  }

  function selectFolder(path) {
    state.folder = path;
    let p = "";
    state.expanded.add("");
    for (const part of path.split("/").filter(Boolean)) { p = p ? p + "/" + part : part; state.expanded.add(p.toLowerCase()); }
    $("folders").classList.remove("open");
    runSearch();
  }

  // ------------------------------------------------------------ details

  function select(lower, userAction) {
    state.selected = lower;
    for (const card of document.querySelectorAll(".card.selected")) card.classList.remove("selected");
    const index = state.results.findIndex(r => r.file.lower === lower);
    if (index >= 0 && index < state.shown) $("grid").children[index].classList.add("selected");
    renderDetails();
    $("details").classList.add("open");
    if (userAction) $("details").scrollTop = 0;
    saveHash();
  }

  function subsystemOf(path) {
    let best = null;
    const lower = path.toLowerCase();
    for (const car of state.cars)
      for (const s of car.subsystems || []) {
        const folder = (s.folder || "").toLowerCase().replace(/\/+$/, "") + "/";
        if (lower.startsWith(folder) && (!best || folder.length > best.folder.length)) best = { car, s, folder };
      }
    if (!best) return null;
    const engineers = (best.s.engineers || []).filter(e => e.status === "approved").map(e => e.login);
    return `${best.s.name} (${best.car.name}) - RE: ${engineers.length ? engineers.join(", ") : "none yet"}`;
  }

  function renderDetails() {
    const panel = $("details");
    const f = state.selected && state.byPath.get(state.selected);
    if (!f) {
      panel.replaceChildren(h("div", { class: "empty muted" }, state.selected ? "That file isn't in the vault any more." : "Select a file to see everything about it."));
      return;
    }
    const lock = state.locks.get(f.lower);
    const mine = lock && state.me && (lock.owner || "").toLowerCase() === state.me.login.toLowerCase();
    const isSw = isNative(f.path);   // parts, assemblies, drawings: workflow states and revisions
    const opensInSw = !!cadOf(f.path); // also STEP, IGES, DXF...: SOLIDWORKS opens them
    const tabs = [["props", "Properties"], ["refs", "References"], ["used", "Where Used"], ["history", "History"], ["reviews", "Reviews"]];

    panel.replaceChildren(
      h("button", { class: "ghost back", type: "button", onclick: () => panel.classList.remove("open") }, "← Back to results"),
      h("div", { class: "head" },
        marked(f, thumbnail(f)),
        h("div", null,
          h("h2", null, f.name),
          h("div", { class: "path muted" }, f.path),
          h("div", { class: "chips" }, statusChips(f, false)))),
      h("div", { class: "actions" },
        opensInSw ? h("a", { class: "button primary", href: link("open", f.path), title: isSw ? "Opens it in SOLIDWORKS on this PC (needs SwVault), getting the latest files first"
          : "Imports it into SOLIDWORKS on this PC (needs SwVault), like File > Open" }, "Open in SOLIDWORKS") : null,
        h("a", { class: "button" + (opensInSw ? "" : " primary"), href: downloadUrl(f.path), title: "Download the latest version to this device" }, "Download"),
        opensInSw ? h("a", { class: "button", href: link("getlatest", f.path), title: "Get the latest version (and the files it uses) into your vault folder" }, "Get Latest") : null,
        lock ? null : h("button", { type: "button", title: "Lock it so only you can change it. To edit, open it in SOLIDWORKS and check it out there too; it's already yours.", onclick: () => checkOut(f) }, "Check Out"),
        mine ? h("button", { type: "button", title: "Let go of your check-out. Nothing is uploaded; changes on your PC stay there.", onclick: () => undoCheckOut(f) }, "Undo Check Out") : null,
        mine ? h("a", { class: "button", href: link("checkin", f.path), title: "Upload your changes, in SOLIDWORKS" }, "Check In") : null,
        isSw ? h("a", { class: "button", href: link("changestate", f.path), title: "Submit for review, approve, change request (in SOLIDWORKS)" }, "Change State") : null,
        h("button", { type: "button", onclick: () => openReviewDialog(f) }, "Request Review"),
        lock && !mine && state.me && state.me.admin ? h("button", { class: "danger", type: "button", title: "Admin: let go of this check-out when its owner can't, e.g. their PC is gone", onclick: () => releaseCheckout(f, lock) }, "Release Check-Out") : null,
        h("button", { type: "button", onclick: () => copyLink(f) }, "Copy Link")),
      h("p", { class: "muted small" }, "SOLIDWORKS buttons hand the file to SwVault on this PC (your browser asks the first time). Nothing happens? ",
        h("a", { href: "swvault://ping", title: "SwVault on this PC answers with a notification if the browser can reach it" }, "Test the link to this PC"), "."),
      h("div", { class: "tabs", role: "tablist" }, tabs.map(([key, label]) =>
        h("button", { type: "button", role: "tab", class: state.tab === key ? "active" : "", "aria-selected": state.tab === key ? "true" : "false", onclick: () => { state.tab = key; renderDetails(); } },
          key === "reviews" && state.reviewsFor === f.lower && state.reviews ? `${label} (${state.reviews.length})` : label))),
      h("div", { class: "tab" }, renderTab(f)));
  }

  async function refreshLocks() {
    try { const fresh = await api("api/locks"); state.locks = new Map((fresh.locks || []).map(l => [(l.path || "").toLowerCase(), l])); } catch (_) { /* keep what we have */ }
  }

  async function checkOut(f) {
    try {
      const lock = await api("api/checkout", { body: { path: f.path } });
      state.locks.set(f.lower, lock);
      toast(`${f.name} is checked out to you. In SOLIDWORKS, check it out on your PC to start editing.`);
    } catch (e) {
      if (e.message === "signed out") return;
      toast(e.message || "Couldn't check the file out.");
      await refreshLocks();
    }
    runSearch(false);
    renderDetails();
  }

  async function undoCheckOut(f) {
    if (!window.confirm(`Undo your check-out of ${f.name}?\n\nNothing is uploaded. If you changed the file on your PC, those changes stay there but the file is no longer reserved for you.`)) return;
    try {
      await api("api/undo-checkout", { body: { path: f.path } });
      state.locks.delete(f.lower);
      toast(`${f.name} is no longer checked out.`);
    } catch (e) {
      if (e.message === "signed out") return;
      toast(e.message || "Couldn't undo the check-out.");
      await refreshLocks();
    }
    runSearch(false);
    renderDetails();
  }

  async function releaseCheckout(f, lock) {
    if (!window.confirm(`Release ${lock.owner}'s check-out of ${f.name}?\n\nThe vault keeps the last checked-in version (v${f.version || 1}). Anything ${lock.owner} changed since checking it out is NOT uploaded; it stays only on their PC.`)) return;
    try {
      await api("api/admin/release", { body: { path: f.path } });
      state.locks.delete(f.lower);
      toast(`${f.name} is no longer checked out.`);
    } catch (e) {
      toast(e.message || "Couldn't release the check-out.");
      try { const fresh = await api("api/locks"); state.locks = new Map((fresh.locks || []).map(l => [(l.path || "").toLowerCase(), l])); } catch (_) { /* keep what we have */ }
    }
    runSearch(false);
    renderDetails();
  }

  function renderTab(f) {
    switch (state.tab) {
      case "refs": return renderReferences(f);
      case "used": return renderWhereUsed(f);
      case "history": return renderHistory(f);
      case "reviews": return renderReviews(f);
      default: return renderProperties(f);
    }
  }

  function renderProperties(f) {
    const rows = [];
    const group = title => rows.push(h("tr", { class: "group" }, h("th", { colspan: 2 }, title)));
    const add = (name, value) => { if (value !== undefined && value !== null && String(value).trim() !== "") rows.push(h("tr", null, h("th", null, name), h("td", null, String(value)))); };
    const lock = state.locks.get(f.lower);
    group("Vault");
    add("Name", f.name);
    add("Folder", f.folder || "(vault root)");
    add("Latest version", "v" + (f.version || 1));
    add("Workflow state", f.state);
    add("Revision", f.revision);
    add("Checked out by", lock ? lock.owner + (lock.lockedAt ? ", since " + formatDate(lock.lockedAt) : "") : "");
    add("Last checked in", f.by ? `${f.by}, ${formatDate(f.at)}` : "");
    add("Last version note", f.comment);
    add("Size", formatSize(f.size));
    add("Subsystem", subsystemOf(f.path));
    add("SOLIDWORKS version", f.swVersion);
    add("Exported from", f.derivedFrom);
    if (f.approvedBy) {
      add("Release approved by", `${f.approvedBy}, ${formatDate(f.approvedAt)}` + (f.approvedVersion ? ` (v${f.approvedVersion})` : "") + (f.approvedWith ? ` - ${f.approvedWith}` : ""));
      add("Release note", f.approvalNote);
    }
    add("Content id", f.oid);
    const props = Object.entries(f.properties || {}).sort((a, b) => a[0].localeCompare(b[0]));
    if (props.length) { group("Custom properties"); for (const [k, v] of props) add(k, v); }
    if ((f.configurations || []).length) { group("Configurations"); for (const c of f.configurations) rows.push(h("tr", null, h("td", { colspan: 2 }, c))); }
    if ((f.revisions || []).length) {
      group("Revisions");
      for (const r of [...f.revisions].reverse()) add("Rev " + (r.rev || r.Rev), `v${r.version || r.Version}` + ((r.by || r.By) ? ` by ${r.by || r.By}` : "") + ((r.at || r.At) ? `, ${formatDate(r.at || r.At)}` : ""));
    }
    if ((f.externalReferences || []).length) { group("References outside the vault"); for (const x of f.externalReferences) rows.push(h("tr", null, h("td", { colspan: 2 }, x))); }
    return h("table", { class: "props" }, h("tbody", null, rows));
  }

  function fileRow(path, version, extra) {
    const target = state.byPath.get((path || "").toLowerCase());
    const label = [fileName(path || "?")];
    if (version) label.push(" v" + version);
    if (target && version && target.version > version) label.push(` (latest v${target.version})`);
    return h("div", { class: "row", title: path, onclick: () => target ? goTo(target) : toast(fileName(path) + " isn't in the vault any more.") },
      target ? thumbnailSmall(target) : null,
      h("span", { class: "grow" }, label.join("")),
      target ? statusChips(target, true).slice(2) : h("span", { class: "badge plain" }, "missing"),
      extra || null);
  }

  function thumbnailSmall(file) {
    const el = thumbnail(file);
    el.style.width = el.style.height = "32px";
    el.style.fontSize = "9px";
    el.style.borderRadius = "5px";
    const wrap = marked(file, el);
    wrap.classList.add("small");
    return wrap;
  }

  function goTo(file) {
    state.query = "";
    $("search").value = "";
    state.folder = "";
    runSearch();
    select(file.lower, true);
  }

  function renderReferences(f) {
    if (!(f.references || []).length) return h("p", { class: "muted" }, (f.externalReferences || []).length ? "No references inside the vault." : "This file doesn't reference other files.");
    const seen = new Set([f.lower]);
    function list(refs, depth) {
      return h("ul", { class: depth ? "" : "tree" }, refs.map(r => {
        const target = state.byPath.get((r.path || "").toLowerCase());
        const key = (r.path || "").toLowerCase();
        const loop = seen.has(key);
        seen.add(key);
        const children = !loop && target && depth < 12 && (target.references || []).length ? list(target.references, depth + 1) : null;
        return h("li", null, fileRow(r.path, r.version), children);
      }));
    }
    return h("div", null, h("p", { class: "muted small" }, "Versions are the ones the author had when checking in (as built)."), list(f.references, 0));
  }

  function renderWhereUsed(f) {
    const parents = state.whereUsed.get(f.lower) || [];
    if (!parents.length) return h("p", { class: "muted" }, "Not used by any file in the vault.");
    return h("ul", { class: "list" }, parents.map(p => {
      const ref = (p.references || []).find(r => (r.path || "").toLowerCase() === f.lower);
      return h("li", null, fileRow(p.path, null, ref ? h("span", { class: "muted small" }, `uses v${ref.version}`) : null));
    }));
  }

  // ------------------------------------------------------------ history (version timeline)

  function renderHistory(f) {
    if (state.historyFor !== f.lower) {
      state.historyFor = f.lower;
      state.history = null;
      state.versionIndex = 0;
      api("api/history?path=" + encodeURIComponent(f.path)).then(r => {
        if (state.historyFor !== f.lower) return;
        state.history = r.versions || [];
        if (state.selected === f.lower && state.tab === "history") renderDetails();
      }).catch(e => { if (state.historyFor === f.lower) { state.history = []; toast("History: " + e.message); } });
    }
    if (!state.history) return h("p", { class: "muted" }, "Loading history...");
    if (!state.history.length) return h("p", { class: "muted" }, "No history yet. Versions appear here once the file is checked in.");

    const versions = state.history;
    const items = versions.map((v, i) => {
      const previousState = versions[i + 1] ? versions[i + 1].state : null;
      const classes = [v.isNewVersion ? "version" : "event", isReleased(v.state) ? "released" : "", i === state.versionIndex ? "selected" : ""].join(" ");
      const select = () => { state.versionIndex = i; renderDetails(); };
      if (v.isNewVersion) {
        return h("li", { class: classes, onclick: select, tabindex: 0, onkeydown: e => e.key === "Enter" && select() },
          h("span", { class: "dot" }),
          h("div", { class: "title" }, "Version " + v.version,
            v.revision ? h("span", { class: "badge plain" }, "Rev " + v.revision) : null,
            v.state ? h("span", { class: "badge " + (isReleased(v.state) ? "released" : "plain") }, (isReleased(v.state) ? "⚑ " : "") + v.state) : null,
            i === 0 ? h("span", { class: "badge latest" }, "Latest") : null),
          h("div", { class: "note" }, (v.comment || "").replace(/\s+/g, " ")),
          h("div", { class: "muted small" }, [v.by || "?", formatDate(v.at), formatSize(v.size), v.approvedBy ? "release approved by " + v.approvedBy : ""].filter(Boolean).join("  ·  ")));
      }
      const change = previousState && previousState !== v.state ? `${previousState} → ${v.state}` : v.state || "Updated";
      return h("li", { class: classes, onclick: select, tabindex: 0, onkeydown: e => e.key === "Enter" && select() },
        h("span", { class: "dot" }),
        [change + (v.revision ? ` (Rev ${v.revision})` : ""), v.approvedBy ? "approved by " + v.approvedBy : v.by, formatDate(v.at), v.comment].filter(Boolean).join("  ·  "));
    });
    return h("div", null, h("ul", { class: "timeline" }, items), renderVersion(f, versions, state.versionIndex));
  }

  function renderVersion(f, versions, i) {
    const v = versions[i];
    if (!v) return null;
    const previousVersion = versions.slice(i + 1).find(x => x.version !== v.version);
    const previousState = versions[i + 1] ? versions[i + 1].state : null;
    const changes = v.isNewVersion ? describeChanges(v, previousVersion)
      : ["Workflow change only; the content is the same as version " + v.version + ".", `State: ${previousState || "none"} → ${v.state || "none"}`];
    return h("div", { class: "version-detail" },
      h("div", { class: "top" },
        h("div", null,
          h("h3", null, "Version " + v.version + (v.revision ? "  ·  Rev " + v.revision : "") + (v.state ? "  ·  " + v.state : "")),
          h("div", { class: "muted small" }, (v.isNewVersion ? "Checked in by " : "Changed by ") + (v.by || "?") + " on " + formatDate(v.at)),
          v.approvedBy ? h("div", { class: "small" }, "Release approved by " + v.approvedBy + (v.approvalNote ? ": " + v.approvalNote : "")) : null,
          h("div", { class: "muted small" }, formatSize(v.size) + "  ·  commit " + (v.commit || "").slice(0, 8))),
        thumbnail(f, v.commit, true)),
      h("pre", null, v.comment || "(no note)"),
      h("strong", null, "Changes from the version before"),
      h("ul", null, changes.map(c => h("li", null, c))),
      h("div", { class: "actions" },
        h("a", { class: "button", href: downloadUrl(f.path, v.commit, v.version) }, "Download this version"),
        [".sldprt", ".sldasm", ".slddrw"].includes(f.ext) ? h("a", { class: "button", href: link("history", f.path), title: "Get or roll back to a version in SOLIDWORKS" }, "Open History in SOLIDWORKS") : null));
  }

  function describeChanges(current, previous) {
    if (!previous) return ["First version in the vault."];
    const changes = [];
    if (current.oid !== previous.oid) {
      const delta = (current.size || 0) - (previous.size || 0);
      changes.push(`Content changed (${formatSize(previous.size) || "0 B"} → ${formatSize(current.size) || "0 B"}` + (delta ? `, ${delta > 0 ? "+" : "-"}${formatSize(Math.abs(delta))}` : "") + ")");
    }
    if ((current.revision || "") !== (previous.revision || "")) changes.push(`Revision: ${previous.revision || "none"} → ${current.revision || "none"}`);
    if ((current.state || "") !== (previous.state || "")) changes.push(`State: ${previous.state || "none"} → ${current.state || "none"}`);
    const before = previous.properties || {}, after = current.properties || {};
    for (const key of [...new Set([...Object.keys(before), ...Object.keys(after)])].sort((a, b) => a.localeCompare(b))) {
      if (key in before && !(key in after)) changes.push(`Property removed: ${key} (was "${before[key]}")`);
      else if (!(key in before)) changes.push(`Property added: ${key} = "${after[key]}"`);
      else if (before[key] !== after[key]) changes.push(`Property ${key}: "${before[key]}" → "${after[key]}"`);
    }
    const oldRefs = new Map((previous.references || []).map(r => [(r.path || "").toLowerCase(), r]));
    const newRefs = new Map((current.references || []).map(r => [(r.path || "").toLowerCase(), r]));
    for (const [k, r] of newRefs) if (!oldRefs.has(k)) changes.push(`Reference added: ${fileName(r.path)} v${r.version}`);
    for (const [k, r] of oldRefs) if (!newRefs.has(k)) changes.push(`Reference removed: ${fileName(r.path)}`);
    for (const [k, r] of newRefs) if (oldRefs.has(k) && oldRefs.get(k).version !== r.version) changes.push(`Reference updated: ${fileName(r.path)} v${oldRefs.get(k).version} → v${r.version}`);
    const oldConfigs = previous.configurations || [], newConfigs = current.configurations || [];
    for (const c of newConfigs) if (!oldConfigs.includes(c)) changes.push("Configuration added: " + c);
    for (const c of oldConfigs) if (!newConfigs.includes(c)) changes.push("Configuration removed: " + c);
    return changes.length ? changes : ["No recorded changes besides the note."];
  }

  // ------------------------------------------------------------ reviews

  // justSent: a request made a moment ago, which the server's issue search may not list yet.
  function loadReviews(f, force, justSent) {
    if (!force && state.reviewsFor === f.lower) return;
    state.reviewsFor = f.lower;
    state.reviews = null;
    api("api/reviews?path=" + encodeURIComponent(f.path)).then(r => {
      if (state.reviewsFor !== f.lower) return;
      state.reviews = r.reviews || [];
      if (justSent && !state.reviews.some(x => x.number === justSent.number)) state.reviews.unshift(Object.assign({ comments: [] }, justSent));
      if (state.selected === f.lower) renderDetails();
    }).catch(e => { if (state.reviewsFor === f.lower) { state.reviews = []; toast("Reviews: " + e.message); renderDetails(); } });
  }

  function renderReviews(f) {
    loadReviews(f, false);
    const top = h("div", { class: "actions" }, h("button", { type: "button", class: "primary", onclick: () => openReviewDialog(f) }, "Request Review"));
    if (!state.reviews) return h("div", null, top, h("p", { class: "muted" }, "Loading reviews..."));
    if (!state.reviews.length) return h("div", null, top, h("p", { class: "muted" }, "No reviews of this file yet."));
    const me = (state.me && state.me.login || "").toLowerCase();
    return h("div", null, top, state.reviews.map(r => {
      const open = r.status === "Waiting for review" || r.status === "Changes requested";
      const isLead = r.lead.toLowerCase() === me, isRequester = r.requester.toLowerCase() === me;
      const statusClass = r.status === "Approved" ? "approved" : r.status === "Changes requested" ? "changes" : r.status === "Cancelled" ? "plain" : "review";
      let form = null;
      if (open) {
        const text = h("textarea", { rows: 3, placeholder: isLead ? "Feedback (needed to ask for changes)" : "Comment" });
        let buttons;
        const send = decision => async () => {
          if (buttons.some(b => b.disabled)) return;
          buttons.forEach(b => { b.disabled = true; });
          try {
            await api(`api/reviews/${r.number}/respond`, { body: { decision, comment: text.value } });
            toast(decision === "approve" ? "Approved." : decision === "changes" ? "Changes requested." : decision === "cancel" ? "Request cancelled." : "Comment posted.");
            loadReviews(f, true);
          } catch (e) { toast(e.message); }
          buttons.forEach(b => { b.disabled = false; });
        };
        buttons = [
          isLead ? h("button", { type: "button", class: "primary", onclick: send("approve") }, "Approve") : null,
          isLead ? h("button", { type: "button", onclick: send("changes") }, "Request Changes") : null,
          h("button", { type: "button", onclick: send("comment") }, "Comment"),
          isRequester ? h("button", { type: "button", class: "ghost", onclick: send("cancel") }, "Cancel Request") : null].filter(Boolean);
        form = h("form", { onsubmit: e => e.preventDefault() }, text, h("div", { class: "buttons" }, buttons));
      }
      return h("article", { class: "review" },
        h("header", null, r.kind + " of v" + r.version, h("span", { class: "badge " + statusClass }, r.status)),
        h("div", { class: "muted small" }, `Asked by ${r.requester} on ${formatDate(r.created)}; reviewer ${r.lead || "(none)"}` + (r.cc && r.cc.length ? `; cc ${r.cc.join(", ")}` : "")),
        f.version > r.version ? h("div", { class: "small" }, `The file has changed since (now v${f.version}).`) : null,
        r.message ? h("pre", null, r.message) : null,
        (r.comments || []).map(c => h("div", { class: "comment" }, h("div", { class: "who" }, `${c.author}, ${formatDate(c.at)}`), h("pre", null, bold(c.text)))),
        form,
        r.webUrl ? h("div", { class: "small" }, h("a", { href: publicUrl(r.webUrl), target: "_blank", rel: "noopener" }, "Open on the server")) : null);
    }));
  }

  // Review comments are Markdown on the server; show **bold** as bold and the rest as plain text.
  function bold(text) {
    return String(text || "").split(/\*\*(.+?)\*\*/s).map((part, i) => (i % 2 ? h("strong", null, part) : part));
  }

  async function openReviewDialog(f) {
    const dialog = $("review-dialog"), form = $("review-form");
    form.reset();
    $("review-error").hidden = true;
    $("review-file").textContent = `${f.path} (version ${f.version || 1})`;
    if (f.ext === ".slddrw") form.kind.value = "drawing";
    const select = form.lead;
    select.replaceChildren(h("option", { value: "" }, "Loading leads..."));
    dialog.showModal();
    try {
      if (!state.leads) state.leads = (await api("api/leads")).leads || [];
      select.replaceChildren(h("option", { value: "" }, state.leads.length ? "Choose a lead" : "No subteam leads yet"),
        ...state.leads.map(l => h("option", { value: l.login }, (l.fullName || l.login) + (l.subteam ? ` (${l.subteam})` : ""))));
    } catch (e) {
      select.replaceChildren(h("option", { value: "" }, "Couldn't load leads"));
    }
    form.dataset.path = f.path;
  }

  async function sendReviewRequest(e) {
    e.preventDefault();
    const form = $("review-form");
    const error = $("review-error");
    const submit = form.querySelector("button[type=submit]");
    if (submit.disabled) return;  // already sending; a second click would open a duplicate review
    submit.disabled = true;
    submit.textContent = "Sending...";
    try {
      const review = await api("api/reviews", { body: { path: form.dataset.path, kind: form.kind.value, lead: form.lead.value, message: form.message.value } });
      $("review-dialog").close();
      toast(`Asked ${review.lead} for a ${review.kind.toLowerCase()}.`);
      const f = state.byPath.get(form.dataset.path.toLowerCase());
      if (f) { state.tab = "reviews"; loadReviews(f, true, review); renderDetails(); }
    } catch (err) {
      error.textContent = err.message;
      error.hidden = false;
    } finally {
      submit.disabled = false;
      submit.textContent = "Send request";
    }
  }

  async function copyLink(f) {
    const url = new URL(location.href);
    url.hash = "file=" + encodeURIComponent(f.path);
    try {
      await navigator.clipboard.writeText(url.toString());
      toast("Link copied. Anyone on the team can open it.");
    } catch {
      prompt("Copy this link:", url.toString());
    }
  }

  // ------------------------------------------------------------ hash (shareable state)

  function saveHash() {
    const params = new URLSearchParams();
    if (state.query) params.set("q", state.query);
    if (state.folder) params.set("folder", state.folder);
    if (state.type) params.set("type", state.type);
    if (state.status) params.set("status", state.status);
    const f = state.selected && state.byPath.get(state.selected);
    if (f) params.set("file", f.path);
    const hash = params.toString();
    if (location.hash.slice(1) !== hash) history.replaceState(null, "", hash ? "#" + hash : location.pathname + location.search);
  }

  function readHash() {
    const params = new URLSearchParams(location.hash.slice(1));
    state.query = params.get("q") || "";
    state.folder = params.get("folder") || "";
    state.type = params.get("type") || "";
    state.status = params.get("status") || "";
    $("search").value = state.query;
    $("type").value = state.type;
    $("status").value = state.status;
    const file = params.get("file");
    if (state.folder) selectFolderSilently(state.folder);
    return file ? file.toLowerCase() : null;
  }

  function selectFolderSilently(path) {
    let p = "";
    for (const part of path.split("/").filter(Boolean)) { p = p ? p + "/" + part : part; state.expanded.add(p.toLowerCase()); }
  }

  // ------------------------------------------------------------ sign in, start

  function showSignIn(message) {
    $("app").hidden = true;
    $("signin").hidden = false;
    const error = $("signin-error");
    error.textContent = message || "";
    error.hidden = !message;
  }

  async function start() {
    try {
      state.me = await api("api/me", { allow401: true });
    } catch (e) {
      showSignIn(e.message === "HTTP 401" ? "" : null);
      return;
    }
    if (!state.me || !state.me.login) { showSignIn(); return; }
    $("signin").hidden = true;
    $("app").hidden = false;
    $("user-name").textContent = state.me.fullName || state.me.login;
    $("accounts-toggle").hidden = !state.me.admin;
    $("summary").textContent = "Loading the vault...";
    try {
      await loadAll();
    } catch (e) {
      if (e.message !== "signed out") $("summary").textContent = "Couldn't load the vault: " + e.message;
      return;
    }
    const file = readHash();
    runSearch();
    if (file && state.byPath.has(file)) select(file, false);
    setInterval(() => { if (!document.hidden) refresh(); }, REFRESH_MS);
    document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
  }

  // ------------------------------------------------------------ accounts (vault admins)

  const FILES_PLACEHOLDER = "Search names, part numbers, materials, notes... (typos and initials work)";

  function showAccounts(show) {
    $("accounts").hidden = !show;
    $("main").hidden = show;
    $("search-hint").hidden = show;
    $("accounts-toggle").textContent = show ? "Files" : "Accounts";
    // The search box searches whatever is on screen: accounts here, files otherwise.
    const search = $("search");
    search.value = show ? state.accountQuery : state.query;
    search.placeholder = show ? "Search accounts: user name, name, email, access, team role..." : FILES_PLACEHOLDER;
    search.setAttribute("aria-label", show ? "Search accounts" : "Search the vault");
    $("type").hidden = $("status").hidden = show;
    if (show) { loadAccounts(); loadUpdates(); } else clearTimeout(updatesTimer);
  }

  const accountsShown = () => !$("accounts").hidden;

  function accountMatches(a, query) {
    const words = query.toLowerCase().split(/\s+/).filter(Boolean);
    if (!words.length) return true;
    const text = [a.login, a.fullName, a.email, a.role === "none" ? "not on the team" : a.role, a.memberType, a.subteam, a.disabled ? "disabled" : "active"].join(" ").toLowerCase();
    return words.every(w => text.includes(w));
  }

  // Automatic updates: the server builds the newest code on GitHub and publishes it (nightly, or now).
  let updatesTimer;

  function renderUpdates(data) {
    const s = data.status || {};
    const running = s.state === "running";
    $("updates").hidden = false;
    $("updates-published").textContent = data.published && data.published.version
      ? `Published: ${data.published.version}` + (data.published.published ? ` (${formatDate(data.published.published)})` : "") : "Nothing published yet";
    const status = $("updates-status");
    status.className = "small " + (s.state === "failed" ? "failed" : "muted");
    status.textContent = !data.enabled ? "Automatic updates are off. To turn them on, on the server: sudo ./swvault-admin.sh auto-update on <owner>/<repository>"
      : running ? `${s.message}...`
      : s.finished ? `${s.state === "failed" ? "Last update failed" : "Last check"} ${formatDate(s.finished)}: ${s.message}`
      : s.message || "";
    const button = $("updates-run");
    button.hidden = !data.enabled;
    button.disabled = running;
    button.textContent = running ? "Updating..." : "Check for updates now";
    clearTimeout(updatesTimer);
    if (running && accountsShown()) updatesTimer = setTimeout(loadUpdates, 5000);
  }

  async function loadUpdates() {
    try {
      renderUpdates(await api("api/admin/update"));
    } catch (e) {
      if (e.message !== "signed out") $("updates").hidden = true; // an older server without this
    }
  }

  async function runUpdate() {
    if (!window.confirm("Build the newest SwVault code and publish it to everyone if anything changed?\n\nThis takes about 10 minutes. If the tests fail, nothing is published.")) return;
    const error = $("accounts-error");
    error.hidden = true;
    try {
      renderUpdates(await api("api/admin/update", { body: {} }));
    } catch (e) {
      if (e.message === "signed out") return;
      error.textContent = e.message;
      error.hidden = false;
      loadUpdates();
    }
  }

  async function loadAccounts() {
    const error = $("accounts-error");
    error.hidden = true;
    try {
      const data = await api("api/admin/accounts");
      state.accounts = data.accounts || [];
      renderAccounts();
    } catch (e) {
      if (e.message === "signed out") return;
      error.textContent = e.message;
      error.hidden = false;
    }
  }

  async function manageAccount(account, body, done) {
    const error = $("accounts-error");
    error.hidden = true;
    try {
      const result = await api("api/admin/accounts/" + encodeURIComponent(account.login), { body });
      if (result.password) window.prompt(`New password for ${account.login}. Copy it now and send it to them privately; it isn't shown again.`, result.password);
      else toast(done);
    } catch (e) {
      if (e.message === "signed out") return;
      error.textContent = e.message;
      error.hidden = false;
    }
    await loadAccounts();
  }

  function changeAccess(a, select) {
    const role = select.value;
    const question = role === "admin" ? `Make ${a.login} a vault admin?\n\nAdmins can manage every account, release anyone's check-out, and change the vault's settings.`
      : a.role === "admin" ? `Take away ${a.login}'s admin access? They become a ${role}.` : null;
    if (question && !window.confirm(question)) { select.value = a.role === "none" ? "" : a.role; return; }
    manageAccount(a, { action: "role", role }, `${a.login} is now ${role === "admin" ? "an admin" : "a " + role}.`);
  }

  function renderAccounts() {
    const all = state.accounts || [];
    const accounts = all.filter(a => accountMatches(a, state.accountQuery));
    const active = all.filter(a => !a.disabled).length;
    $("accounts-summary").textContent = `${all.length} account${all.length === 1 ? "" : "s"}, ${active} active`
      + (accounts.length !== all.length ? `; showing ${accounts.length} matching "${state.accountQuery}"` : "");
    const roleCell = a => {
      if (a.role === "admin" && a.you) return "Admin";
      const select = h("select", { "aria-label": "Access for " + a.login, onchange: e => changeAccess(a, e.target) },
        a.role === "none" ? h("option", { value: "", selected: true, disabled: true }, "Not on the team") : null,
        h("option", { value: "admin", selected: a.role === "admin" }, "Admin (manage the vault)"),
        h("option", { value: "designer", selected: a.role === "designer" }, "Designer (check out and in)"),
        h("option", { value: "viewer", selected: a.role === "viewer" }, "Viewer (read only)"));
      return select;
    };
    const teamRole = a => (a.memberType === "lead" ? `Lead${a.subteam ? " of " + a.subteam : ""}` : a.memberType === "member" ? "Member" : "")
      + (a.pendingLead ? ` (asked to lead ${a.pendingLead})` : "");
    const rows = accounts.map(a => h("tr", { class: a.disabled ? "disabled" : null },
      h("td", null, h("strong", null, a.login), a.you ? " (you)" : ""),
      h("td", null, a.fullName),
      h("td", null, a.email || h("span", { class: "muted" }, "none")),
      h("td", null, roleCell(a)),
      h("td", null, teamRole(a)),
      h("td", null, a.lastSignIn ? formatDate(a.lastSignIn) : h("span", { class: "muted" }, "never")),
      h("td", null, a.disabled ? "Disabled" : "Active"),
      h("td", null, h("div", { class: "row-actions" },
        h("button", { class: "ghost", type: "button", onclick: () => {
          const email = window.prompt(`Email address for ${a.login} (review and approval emails go there):`, a.email || "");
          if (email && email.trim() !== a.email) manageAccount(a, { action: "email", email: email.trim() }, `Email for ${a.login} saved.`);
        } }, "Email..."),
        h("button", { class: "ghost", type: "button", onclick: () => {
          if (window.confirm(`Give ${a.login} a new password? Their current one stops working.`)) manageAccount(a, { action: "reset-password" });
        } }, "Reset password"),
        a.you ? null : h("button", { class: "ghost", type: "button", onclick: () => {
          if (a.disabled || window.confirm(`Disable ${a.login}? They won't be able to sign in. Their history and check-outs stay.`))
            manageAccount(a, { action: a.disabled ? "enable" : "disable" }, a.disabled ? `${a.login} can sign in again.` : `${a.login} is disabled.`);
        } }, a.disabled ? "Enable" : "Disable")))));
    $("accounts-table").replaceChildren(
      h("thead", null, h("tr", null, ["Account", "Name", "Email", "Access", "Team role", "Last sign-in", "Status", ""].map(t => h("th", null, t)))),
      h("tbody", null, rows.length ? rows : h("tr", null, h("td", { colspan: 8, class: "muted" }, "No accounts match."))));
  }

  // A click on a swvault:// button leaves the page with no way to know whether SwVault took it.
  // Say what should happen and what to check, instead of a click that may silently do nothing.
  function explainLink(e) {
    const a = e.target.closest && e.target.closest('a[href^="swvault:"]');
    if (!a) return;
    const test = a.getAttribute("href").startsWith("swvault://ping");
    let left = false;
    const leave = () => { left = true; };
    window.addEventListener("blur", leave, { once: true });
    document.addEventListener("visibilitychange", leave, { once: true });
    toast(test ? "Sent. SwVault on this PC should show a notification near the clock within a few seconds."
      : "Sent to SwVault on this PC. If your browser asks, allow it to open the SwVault link.");
    setTimeout(() => {
      window.removeEventListener("blur", leave);
      document.removeEventListener("visibilitychange", leave);
      if (left) return; // a prompt, SOLIDWORKS or a SwVault window took over: it arrived
      toast(test
        ? "No sign of SwVault yet. If no notification appeared: is SwVault installed and running on this PC (its icon is near the clock; version 1.1.0 or newer)? Did the browser ask to open the link, and was it allowed?"
        : "If nothing happened: check SwVault is running on this PC (its icon is near the clock) and that your browser is allowed to open SwVault links. \"Test the link to this PC\" below the buttons checks both.", 12000);
    }, 3000);
  }

  function wire() {
    document.addEventListener("click", explainLink);
    $("type").append(...TYPE_GROUPS.map(([label, value]) => h("option", { value }, label)));
    $("accounts-toggle").addEventListener("click", () => showAccounts($("accounts").hidden));
    $("updates-run").addEventListener("click", runUpdate);
    $("signin-form").addEventListener("submit", async e => {
      e.preventDefault();
      const form = e.target;
      try {
        await api("api/login", { body: { username: form.username.value, password: form.password.value }, allow401: true });
        form.password.value = "";
        await start();
      } catch (err) {
        showSignIn(err.message === "HTTP 401" ? "Wrong user name or password." : err.message);
      }
    });
    $("signout").addEventListener("click", async () => {
      await api("api/logout", { body: {} }).catch(() => {});
      location.hash = "";
      location.reload();
    });
    let timer;
    $("search").addEventListener("input", e => {
      clearTimeout(timer);
      if (accountsShown()) { state.accountQuery = e.target.value.trim(); renderAccounts(); return; }
      timer = setTimeout(() => { state.query = e.target.value.trim(); runSearch(); }, 120);
    });
    $("search").addEventListener("keydown", e => {
      if (accountsShown()) {
        if (e.key === "Escape") { e.target.value = ""; state.accountQuery = ""; renderAccounts(); }
        return;
      }
      if (e.key === "Enter" && state.results.length) select(state.results[0].file.lower, true);
      if (e.key === "Escape") { e.target.value = ""; state.query = ""; runSearch(); }
    });
    $("type").addEventListener("change", e => { state.type = e.target.value; runSearch(); });
    $("status").addEventListener("change", e => { state.status = e.target.value; runSearch(); });
    $("clear-folder").addEventListener("click", () => selectFolder(""));
    $("more").addEventListener("click", () => { state.shown += PAGE; renderResults(Fuzzy.parseQuery(state.query).isEmpty); });
    $("folders-toggle").addEventListener("click", () => $("folders").classList.toggle("open"));
    $("home").addEventListener("click", e => { e.preventDefault(); showAccounts(false); state.query = ""; $("search").value = ""; state.folder = ""; state.status = ""; state.type = ""; $("type").value = ""; $("status").value = ""; runSearch(); });
    $("review-form").addEventListener("submit", sendReviewRequest);
    $("review-cancel").addEventListener("click", () => $("review-dialog").close());
    document.addEventListener("keydown", e => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); $("search").focus(); $("search").select(); }
    });
    window.addEventListener("hashchange", () => {
      if (!state.index) return;
      const file = readHash();
      runSearch();
      if (file && state.byPath.has(file) && file !== state.selected) select(file, true);
    });
  }

  wire();
  start();
})();
