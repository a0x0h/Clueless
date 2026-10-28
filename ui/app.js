// Clueless UI: a floating pill (session controls) and a panel (Claude's answers,
// transcript, quick actions). Captions come from the host; questions go through the host
// into the Claude desktop app, with a screenshot of the user's screen when enabled.

const host = window.chrome && window.chrome.webview;
const post = (msg) => (host ? host.postMessage(msg) : console.log("→ host", msg));

const DEFAULTS = {
  meName: "",
  profile: "", // CV / background, sent once per session in the warm-up
  includeScreen: true,
  returnFocus: true,
  newChat: false,
  autoSend: false,
  hideLiveCaptions: true,
  micAccurate: false,
  stealth: true,
  onTop: true,
  sessionsFolder: "",
  intro:
    "You're my real-time copilot in a live conversation (often a job interview or a meeting). I'll send you what's being said as \"Name: text\" lines " +
    "from automatic captions: expect recognition errors, and a question may be split over several lines or still unfinished, so infer what's really being asked. " +
    "\"Others\" is the other side (for example the interviewer); lines under my name or \"You\" are mine. Often there's a screenshot of my screen too.\n" +
    "When I ask for help, reply with the answer itself, ready to read at a glance or say out loud in my voice: lead with the direct answer, then a few short bullets. " +
    "Use **bold** for the key phrases. No preamble, no restating the question.",
};
let settings = { ...DEFAULTS };

const PROMPTS = {
  assist: { label: "Assist", screen: true,
    text: "Help me right now. If the other side just asked me something (it may span several lines above, or still be in progress), answer it as I'd say it. " +
      "Otherwise tell me the most useful thing to know or say at this moment, using my screen if it's relevant." },
  say: { label: "What should I say?", screen: true,
    text: "What should I say next? Give me one or two short options I can say out loud, in my own voice." },
  followup: { label: "Follow-up questions", screen: true,
    text: "Suggest three sharp follow-up questions I could ask right now." },
  recap: { label: "Recap", screen: false, full: true,
    text: "Recap the whole conversation so far: key points, decisions, open questions and action items." },
};

let rows = []; // {k, s, t, ts, f, sent} from the host, in order
const answers = []; // {id, ts, label, screen, state, text, error}
let session = false, sessionStarted = 0;
let tab = "assist";
let nextId = 1;
const picked = new Set(); // transcript lines selected to ask about together
let pickAnchor = null;

const $ = (id) => document.getElementById(id);

// ---------------------------------------------------------------- helpers

const PALETTE = ["#fb923c", "#34d399", "#facc15", "#f472b6", "#38bdf8", "#a78bfa", "#2dd4bf", "#f87171"];
const colorOf = (s) => {
  if (s === "Me") return "#60a5fa";
  if (s === "Others") return "#a1a1aa";
  let h = 0;
  for (const c of s) h = (h * 31 + c.charCodeAt(0)) | 0;
  return PALETTE[Math.abs(h) % PALETTE.length];
};
const nameOf = (s) => (s === "Me" ? settings.meName || "You" : s);
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const timeOf = (ts) => new Date(ts).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });

// "Alex: a b\nAbi: c" — consecutive lines by the same person are joined
function format(list) {
  const out = [];
  let last = null;
  for (const r of list) {
    if (r.s === last) out[out.length - 1] += " " + r.t;
    else out.push(nameOf(r.s) + ": " + r.t);
    last = r.s;
  }
  return out.join("\n");
}

const ICON = {
  copy: '<svg viewBox="0 0 24 24"><rect x="9" y="9" width="11" height="11" rx="2.5"/><path d="M5 15V6a1 1 0 0 1 1-1h9"/></svg>',
  check: '<svg viewBox="0 0 24 24"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg>',
  screen: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg>',
  arrow: '<svg viewBox="0 0 24 24"><path d="M5 12h13M13 6l6 6-6 6"/></svg>',
};

// ---------------------------------------------------------------- markdown (answers)

// Claude's replies as it formats them: headings, bold/italic, lists (nested), tables,
// quotes, code. Links are shown as text: following one would navigate the panel away.
function inline(s) {
  const codes = [];
  return esc(s)
    .replace(/`([^`]+)`/g, (_, c) => "\u0000" + (codes.push(c) - 1) + "\u0000")
    .replace(/\[([^\]]+)\]\([^)]+\)/g, '<span class="lnk">$1</span>')
    .replace(/(\*\*|__)(?=\S)(.+?\S)\1/g, "<strong>$2</strong>")
    .replace(/(^|[^*\w])\*(?=\S)([^*]*?\S)\*(?!\w)/g, "$1<em>$2</em>")
    .replace(/(^|[^_\w])_(?=\S)([^_]*?\S)_(?!\w)/g, "$1<em>$2</em>")
    .replace(/~~(.+?)~~/g, "<del>$1</del>")
    .replace(/\u0000(\d+)\u0000/g, (_, i) => "<code>" + codes[+i] + "</code>");
}

const LIST_ITEM = /^(\s*)(?:([-*+•])|(\d+)[.)])\s+(.*)$/;
const RULE = /^\s*([-*_])(\s*\1){2,}\s*$/;
const cells = (l) => l.trim().replace(/^\|/, "").replace(/\|$/, "").split("|").map((c) => c.trim());

function md(src) {
  const lines = String(src).replace(/\r/g, "").split("\n");
  const out = [];
  const special = (l) => /^\s*```/.test(l) || /^#{1,6}\s/.test(l) || /^\s*>/.test(l) || LIST_ITEM.test(l) || RULE.test(l);
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    let m;
    if (!line.trim()) { i++; continue; }
    if (/^\s*```/.test(line)) {
      const code = [];
      for (i++; i < lines.length && !/^\s*```/.test(lines[i]); i++) code.push(lines[i]);
      i++;
      out.push("<pre><code>" + esc(code.join("\n")) + "</code></pre>");
    } else if ((m = line.match(/^(#{1,6})\s+(.*)$/))) {
      const tag = "h" + Math.min(5, m[1].length + 2);
      out.push(`<${tag}>${inline(m[2].replace(/\s#+\s*$/, ""))}</${tag}>`);
      i++;
    } else if (RULE.test(line)) {
      out.push("<hr>");
      i++;
    } else if (/^\s*>/.test(line)) {
      const q = [];
      for (; i < lines.length && /^\s*>/.test(lines[i]); i++) q.push(lines[i].replace(/^\s*>\s?/, ""));
      out.push("<blockquote>" + md(q.join("\n")) + "</blockquote>");
    } else if (line.includes("|") && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}/.test(lines[i + 1])) {
      let html = "<table><thead><tr>" + cells(line).map((c) => "<th>" + inline(c) + "</th>").join("") + "</tr></thead><tbody>";
      for (i += 2; i < lines.length && lines[i].includes("|") && lines[i].trim(); i++)
        html += "<tr>" + cells(lines[i]).map((c) => "<td>" + inline(c) + "</td>").join("") + "</tr>";
      out.push('<div class="tbl">' + html + "</tbody></table></div>");
    } else if (LIST_ITEM.test(line)) {
      const items = [];
      for (; i < lines.length; i++) {
        const l = lines[i], li = l.match(LIST_ITEM);
        if (li) items.push({ ind: li[1].length, tag: li[2] ? "ul" : "ol", text: li[4] });
        else if (l.trim() && /^\s+/.test(l)) items[items.length - 1].text += " " + l.trim();
        else if (!l.trim() && i + 1 < lines.length && LIST_ITEM.test(lines[i + 1])) continue;
        else break;
      }
      out.push(listHtml(items));
    } else {
      const para = [];
      for (; i < lines.length && lines[i].trim() && !(para.length && special(lines[i])); i++) para.push(lines[i]);
      out.push("<p>" + para.map(inline).join("<br>") + "</p>");
    }
  }
  return out.join("");
}

// Items carry their indent; a deeper one opens a list inside the item before it.
function listHtml(items) {
  let html = "";
  const stack = [];
  for (const it of items) {
    while (stack.length > 1 && it.ind < stack[stack.length - 1].ind) html += `</li></${stack.pop().tag}>`;
    const top = stack[stack.length - 1];
    if (!top || it.ind > top.ind) { html += `<${it.tag}>`; stack.push({ ind: it.ind, tag: it.tag }); }
    else {
      html += "</li>";
      if (top.tag !== it.tag) { html += `</${top.tag}><${it.tag}>`; top.tag = it.tag; }
    }
    html += "<li>" + inline(it.text);
  }
  while (stack.length) html += `</li></${stack.pop().tag}>`;
  return html;
}

// ---------------------------------------------------------------- window shape

// The window is transparent; tell the host where the visible parts are so only they take clicks.
let shapeQueued = false;
function reportShapes() {
  if (shapeQueued) return;
  shapeQueued = true;
  requestAnimationFrame(() => {
    shapeQueued = false;
    const rects = [];
    const add = (el, r) => {
      if (!el || el.offsetParent === null) return;
      const b = el.getBoundingClientRect();
      if (b.width && b.height) rects.push({ x: b.left, y: b.top, w: b.width, h: b.height, r });
    };
    add($("pill"), 23);
    add($("panel"), 18);
    if ($("menu").classList.contains("show")) add($("menu"), 12);
    post({ type: "shapes", rects, dpr: window.devicePixelRatio || 1 });
  });
}
new ResizeObserver(reportShapes).observe(document.body);
new ResizeObserver(reportShapes).observe($("pill"));
window.addEventListener("resize", reportShapes);

// Drag the window from anywhere on the pill or the panel's top bar, buttons included:
// a press that moves more than a few pixels is a drag, otherwise it's a normal click.
let press = null, dragged = false;
// (Windows' move loop swallows the mouse-up of a drag, so the flag is reset on the next press.)
window.addEventListener("mousedown", () => { dragged = false; }, true);
for (const el of [$("pill"), document.querySelector("#panel .head")]) {
  el.addEventListener("mousedown", (e) => {
    if (e.button === 0) press = { x: e.screenX, y: e.screenY };
  });
}
window.addEventListener("mousemove", (e) => {
  if (!press || !(e.buttons & 1)) { press = null; return; }
  if (Math.abs(e.screenX - press.x) + Math.abs(e.screenY - press.y) > 4) {
    press = null;
    dragged = true;
    post({ type: "drag" });
  }
});
window.addEventListener("mouseup", () => { press = null; });
// swallow the click that ends a drag
window.addEventListener("click", (e) => { if (dragged) { dragged = false; e.stopPropagation(); e.preventDefault(); } }, true);

// ---------------------------------------------------------------- session controls

$("btnStart").onclick = () => {
  answers.length = 0;
  rows = [];
  clearPicked();
  post({ type: "start" });
  warmUp(false);
  setCollapsed(false);
  render();
};

// ---- warm-up: Claude gets your profile once, when the session starts, so questions
// during the session carry only the conversation

function warmupText() {
  const parts = [settings.intro.trim()];
  const me = [];
  if (settings.meName.trim()) me.push("My name: " + settings.meName.trim());
  if (settings.profile.trim()) me.push(settings.profile.trim());
  if (me.length) parts.push("About me (use this to answer as me):\n" + me.join("\n\n"));
  parts.push("Nothing to answer yet. Reply only with \"Ready.\"");
  return parts.filter(Boolean).join("\n\n");
}

const hashOf = (s) => { let h = 5381; for (let i = 0; i < s.length; i++) h = (h * 33 + s.charCodeAt(i)) | 0; return (h >>> 0).toString(36); };

function warmUp(force) {
  const text = warmupText();
  const id = nextId++;
  post({ type: "warmup", id, text, hash: hashOf(text), force: !!force });
  answers.push({ id, ts: Date.now(), kind: "warmup", label: "Warm-up", screen: false, state: "sending", text: "" });
  busyId = id;
  render();
}
$("btnStop").onclick = () => post({ type: "stop" });

function setCollapsed(on) {
  document.body.classList.toggle("collapsed", on);
  $("toggleText").textContent = on ? "Show" : "Hide";
  reportShapes();
}
$("btnToggle").onclick = () => setCollapsed(!document.body.classList.contains("collapsed"));

setInterval(() => {
  if (!session) return;
  const s = Math.max(0, Math.floor((Date.now() - sessionStarted) / 1000));
  const hh = Math.floor(s / 3600), mm = Math.floor((s % 3600) / 60), ss = s % 60;
  $("timer").textContent = (hh ? hh + ":" + String(mm).padStart(2, "0") : String(mm).padStart(2, "0")) + ":" + String(ss).padStart(2, "0");
}, 500);

// ---------------------------------------------------------------- tabs + rendering

document.querySelectorAll(".tab").forEach((b) => (b.onclick = () => setTab(b.dataset.tab)));
function setTab(t) {
  tab = t;
  document.querySelectorAll(".tab").forEach((b) => b.classList.toggle("on", b.dataset.tab === t));
  $("assistView").classList.toggle("hidden", t !== "assist");
  $("transcriptView").classList.toggle("hidden", t !== "transcript");
  render();
  stickBottom(t === "assist" ? $("assistView") : $("transcriptView"), true);
}

const nearBottom = (el) => el.scrollHeight - el.scrollTop - el.clientHeight < 60;
function stickBottom(el, force) { if (force || el.dataset.stick !== "0") el.scrollTop = el.scrollHeight; }
for (const id of ["assistView", "transcriptView"]) $(id).addEventListener("scroll", (e) => (e.target.dataset.stick = nearBottom(e.target) ? "1" : "0"));

let renderQueued = false;
function scheduleRender() {
  if (renderQueued) return;
  renderQueued = true;
  requestAnimationFrame(() => { renderQueued = false; render(); });
}

const selectingText = (el) => { const s = window.getSelection(); return s && !s.isCollapsed && el.contains(s.anchorNode); };

function render() {
  // emptiness + counters
  $("assistEmpty").classList.toggle("hidden", answers.length > 0);
  $("emptyTitle").textContent = session ? "Listening…" : "Start a session to begin";
  $("emptyText").textContent = session
    ? "Ask anything, or use Assist when you need help. Claude sees the conversation and your screen."
    : "Claude will hear the conversation and see your screen when you ask for help. Everything is saved for you.";
  $("feedEmpty").classList.toggle("hidden", rows.length > 0);
  const unsent = rows.filter((r) => !r.sent && r.f).length;
  $("lineCount").textContent = rows.length;
  $("lineCount").classList.toggle("new", unsent > 0 && tab !== "transcript");

  if (tab === "assist") renderAnswers();
  else renderFeed();
}

function renderAnswers() {
  const box = $("answers");
  if (selectingText(box)) return;
  box.replaceChildren(...answers.map(answerEl));
  stickBottom($("assistView"));
}

function answerEl(a) {
  const d = document.createElement("div");
  d.className = "answer";
  d.id = "a-" + a.id;
  fillAnswer(d, a);
  return d;
}

const WARM_STATE = {
  sending: "Giving Claude your profile…",
  waiting: "Claude is reading your profile…",
  streaming: "Claude is reading your profile…",
  done: "Claude knows your profile. Ready when you are.",
  skipped: "This Claude chat already knows your profile. Ready.",
};

function fillAnswer(d, a) {
  if (a.kind === "warmup") {
    const ok = a.state === "done" || a.state === "skipped";
    d.className = "answer warm" + (ok ? " ok" : "") + (a.state === "error" ? " bad" : "");
    d.innerHTML = (ok ? ICON.check : a.state === "error" ? "" : '<span class="spin"></span>') +
      `<span>${esc(a.state === "error" ? a.error || "Warm-up failed." : WARM_STATE[a.state] || "")}</span>`;
    return;
  }
  const working = a.state === "sending" || a.state === "waiting";
  const seen = a.screen && a.state !== "sending" ? `<span class="seen">${ICON.screen}Viewed screen</span><span>·</span>` : "";
  const status = a.state === "sending" ? "Sending to Claude…" : a.state === "waiting" || (a.state === "streaming" && !a.text) ? "Claude is thinking…" : "";
  const body =
    a.state === "error" ? `<div class="body err">${esc(a.error || "Something went wrong.")}</div>`
    : a.text ? `<div class="body md">${md(a.text)}</div>`
    : `<div class="typing"><i></i><i></i><i></i></div>`;
  d.innerHTML =
    `<div class="meta">${seen}<span>${esc(a.label)}</span>${status ? `<span>· ${status}</span>` : ""}` +
    (a.text && !working ? `<button class="copy" data-copy="${a.id}">${ICON.copy}</button>` : "") +
    `</div>` + body;
}

function updateAnswer(a) {
  const d = document.getElementById("a-" + a.id);
  if (!d || tab !== "assist") return scheduleRender();
  fillAnswer(d, a);
  stickBottom($("assistView"));
}

function renderFeed() {
  const feed = $("feed");
  if (selectingText(feed)) return;
  const frag = document.createDocumentFragment();
  let turn = null, prev = null;
  for (const r of rows) {
    if (!turn || prev.s !== r.s || r.ts - prev.ts > 3 * 60e3) {
      turn = document.createElement("div");
      turn.className = "turn";
      turn.innerHTML = `<div class="who"><span class="name" data-speaker="${esc(r.s)}" style="color:${colorOf(r.s)}">${esc(nameOf(r.s))}</span><span class="time">${timeOf(r.ts)}</span></div>`;
      frag.append(turn);
    }
    const l = document.createElement("div");
    l.className = "line" + (r.f ? "" : " live") + (r.sent ? " sent" : "") + (picked.has(r.k) ? " picked" : "");
    l.dataset.k = r.k;
    l.innerHTML = `<span class="txt">${esc(r.t)}</span>` + (r.sent ? `<span class="check">${ICON.check}</span>` : "") +
      `<button class="send" data-send>${ICON.arrow}Ask</button>`;
    turn.append(l);
    prev = r;
  }
  feed.replaceChildren(frag);
  stickBottom($("transcriptView"));
}

// ---------------------------------------------------------------- asking Claude

let busyId = 0;
const RECENT_MS = 60e3, RECENT_LINES = 4;

// What Claude gets with a question: every line it hasn't seen yet, plus the last minute
// (at least the last few lines) again, including lines still being spoken. A question
// split over several caption lines, or still in progress, so reaches Claude whole.
function contextRows() {
  let start = rows.findIndex((r) => !r.sent);
  if (start < 0) start = rows.length;
  const newest = rows.length ? rows[rows.length - 1].ts : 0;
  let recent = rows.length;
  while (recent > 0 && (rows.length - recent < RECENT_LINES || newest - rows[recent - 1].ts < RECENT_MS)) recent--;
  return rows.slice(Math.min(start, recent));
}

// opts: lines (send exactly these), focus (selected lines to answer about), full (whole transcript)
function ask(request, label, screen, opts = {}) {
  const lines = opts.lines || (opts.full ? rows.slice() : contextRows());
  const parts = [];
  if (lines.length && !opts.lines) {
    const live = lines.some((r) => !r.f) ? " (the last line may still be in progress)" : "";
    parts.push((opts.full ? "The whole conversation so far" : "Latest conversation, newest last" + live) + ":\n" + format(lines));
  } else if (lines.length) parts.push(format(lines));
  if (opts.focus && opts.focus.length) parts.push("About these lines:\n" + format(opts.focus).split("\n").map((l) => "> " + l).join("\n"));
  if (request) parts.push(request);
  if (!parts.length) return toast("Nothing new to send");
  const id = nextId++;
  const withScreen = !!(screen && settings.includeScreen);
  const seen = lines.concat(opts.focus || []).filter((r) => r.f);
  post({ type: "send", id, text: parts.join("\n\n"), intro: warmupText(), keys: seen.map((r) => r.k), screen: withScreen, label });
  seen.forEach((r) => (r.sent = true)); // host confirms on the next rows update
  answers.push({ id, ts: Date.now(), label, screen: withScreen, state: "sending", text: "" });
  busyId = id;
  if (tab !== "assist") setTab("assist"); else render();
  stickBottom($("assistView"), true);
}

const run = (key) => { const p = PROMPTS[key]; ask(p.text, p.label, p.screen, { full: p.full }); };

document.querySelectorAll(".actions [data-act]").forEach((b) => (b.onclick = () => run(b.dataset.act)));

function onClaude(m) {
  const a = answers.find((x) => x.id === m.id);
  if (!a) return;
  a.state = m.state;
  if (m.text != null) a.text = m.text;
  if (m.error) a.error = m.error;
  if ((m.state === "done" || m.state === "error" || m.state === "skipped") && busyId === m.id) { busyId = 0; flushAuto(); }
  updateAnswer(a);
}

// Auto-send: finished sentences are batched for a moment and sent once Claude is free.
let autoSince = Date.now();
const autoQueued = new Set();
let autoTimer = null;
function collectAuto() {
  if (!settings.autoSend || !session) return;
  let added = false;
  for (const r of rows) {
    if (!r.f || r.sent || r.ts < autoSince || autoQueued.has(r.k)) continue;
    autoQueued.add(r.k);
    added = true;
  }
  if (added) { clearTimeout(autoTimer); autoTimer = setTimeout(flushAuto, 1200); }
}
function flushAuto() {
  if (!settings.autoSend || busyId) return;
  const list = rows.filter((r) => autoQueued.has(r.k) && !r.sent);
  if (list.length) ask("", list.length === 1 ? "Sent a line" : `Sent ${list.length} lines`, false, { lines: list });
}

// ---- input

const input = $("input");
function syncInput() {
  $("input").closest(".ask").classList.toggle("typing-on", !!input.value);
}
input.addEventListener("input", syncInput);
input.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && e.ctrlKey) { e.preventDefault(); run("assist"); return; }
  if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); submit(); }
});
document.addEventListener("keydown", (e) => { if (e.key === "Enter" && e.ctrlKey && document.activeElement !== input) { e.preventDefault(); run("assist"); } });
$("go").onclick = () => (input.value.trim() ? submit() : run("assist"));
function submit() {
  const q = input.value.trim();
  if (!q) return;
  input.value = "";
  syncInput();
  const focus = pickedRows();
  clearPicked();
  ask(q, q, true, { focus });
}

$("btnScreen").onclick = () => toggleSetting("includeScreen");

// ---- transcript interactions

// Click lines to select them (Shift+click selects a range), then ask about all of them at
// once from the bar that appears, or type a question. A line's own Ask button sends just it.
const rowByKey = (k) => rows.find((r) => r.k === k);
const pickedRows = () => rows.filter((r) => picked.has(r.k));
const quote = (list) => { const t = list.map((r) => r.t).join(" "); return "“" + (t.length > 70 ? t.slice(0, 68) + "…" : t) + "”"; };

function clearPicked() { picked.clear(); pickAnchor = null; syncPicked(); }
function syncPicked() {
  const n = picked.size;
  document.body.classList.toggle("picking", n > 0);
  $("pickCount").textContent = n === 1 ? "1 line selected" : `${n} lines selected`;
  document.querySelectorAll("#feed .line").forEach((l) => l.classList.toggle("picked", picked.has(l.dataset.k)));
  $("hintText").textContent = n ? "Ask about the selected lines" : "Ask about your screen or conversation, or";
  $("hintKeys").style.display = n ? "none" : "";
}

function askPicked(request, label) {
  const focus = pickedRows();
  if (!focus.length) return;
  clearPicked();
  ask(request, label || quote(focus), false, { focus });
}

$("feed").addEventListener("click", (e) => {
  const name = e.target.closest(".name[data-speaker]");
  if (name) return renameSpeaker(name.dataset.speaker);
  const line = e.target.closest(".line");
  if (!line) return;
  const r = rowByKey(line.dataset.k);
  if (!r || window.getSelection().toString()) return;
  if (e.target.closest("[data-send]")) {
    clearPicked();
    return ask("How should I respond to this?", quote([r]), false, { focus: [r] });
  }
  if (e.shiftKey && pickAnchor && rowByKey(pickAnchor)) {
    const a = rows.findIndex((x) => x.k === pickAnchor), b = rows.indexOf(r);
    for (let i = Math.min(a, b); i <= Math.max(a, b); i++) picked.add(rows[i].k);
  } else if (picked.has(r.k)) picked.delete(r.k);
  else picked.add(r.k);
  pickAnchor = r.k;
  syncPicked();
});
// Shift+click picks a range of lines, not a range of text
$("feed").addEventListener("mousedown", (e) => {
  if (e.shiftKey && e.target.closest(".line")) { e.preventDefault(); window.getSelection().removeAllRanges(); }
});
$("pickRespond").onclick = () => askPicked("How should I respond to this?");
$("pickExplain").onclick = () => askPicked("Explain this briefly: what's being said or asked, and anything I should know.");
$("pickClear").onclick = clearPicked;
$("answers").addEventListener("click", (e) => {
  const c = e.target.closest("[data-copy]");
  if (!c) return;
  const a = answers.find((x) => x.id === +c.dataset.copy);
  if (a) post({ type: "copy", text: a.text });
});

function renameSpeaker(s) {
  if (s === "Me") return promptText("Your name", settings.meName || "You", (v) => { settings.meName = v; saveSettings(); syncSettingsUI(); scheduleRender(); });
  promptText(`Rename ${s}`, s, (v) => post({ type: "rename", from: s, to: v }));
}

// ---------------------------------------------------------------- menu (logo)

const menu = $("menu");
function showMenu(anchor, items) {
  menu.innerHTML = "";
  for (const it of items) {
    if (it === "-") { menu.append(document.createElement("hr")); continue; }
    const b = document.createElement("button");
    b.innerHTML = esc(it[0]) + (it[2] ? `<small>${esc(it[2])}</small>` : "");
    if (it[3]) b.className = it[3];
    b.onclick = () => { hideMenu(); it[1](); };
    menu.append(b);
  }
  const a = anchor.getBoundingClientRect();
  menu.style.left = a.left + "px";
  menu.style.top = a.bottom + 8 + "px";
  menu.classList.add("show");
  reportShapes();
}
function hideMenu() { if (menu.classList.contains("show")) { menu.classList.remove("show"); reportShapes(); } }
document.addEventListener("mousedown", (e) => { if (!menu.contains(e.target) && !e.target.closest("#logo")) hideMenu(); });
window.addEventListener("blur", hideMenu);

$("logo").onclick = () => {
  if (menu.classList.contains("show")) return hideMenu();
  showMenu($("logo"), [
    ["Settings", openSettings],
    ["Saved sessions", () => post({ type: "openSessions" })],
    [settings.stealth ? "Hidden from screen share" : "Visible in screen share", () => toggleSetting("stealth"), settings.stealth ? "on" : "off"],
    ["Copy transcript", () => post({ type: "copy", text: format(rows) })],
    ["Give Claude my profile again", () => warmUp(true)],
    "-",
    ["Quit", () => post({ type: "close" }), "", "danger"],
  ]);
};
document.addEventListener("contextmenu", (e) => { if (!e.target.closest("input, textarea")) e.preventDefault(); });

// ---- prompt dialog

let promptCb = null;
function promptText(title, value, cb) {
  $("promptTitle").textContent = title;
  $("promptInput").value = value;
  promptCb = cb;
  $("prompt").classList.add("show");
  setTimeout(() => { $("promptInput").focus(); $("promptInput").select(); });
}
const closePrompt = () => { $("prompt").classList.remove("show"); promptCb = null; };
$("promptCancel").onclick = closePrompt;
$("promptOk").onclick = () => { const v = $("promptInput").value.trim(); const cb = promptCb; closePrompt(); if (v && cb) cb(v); };
$("promptInput").addEventListener("keydown", (e) => { if (e.key === "Enter") $("promptOk").click(); if (e.key === "Escape") closePrompt(); });

// ---- toast

let toastTimer;
function toast(text, action) {
  const t = $("toast");
  t.innerHTML = `<span>${esc(text)}</span>`;
  t.classList.toggle("has-action", !!action);
  if (action) {
    const b = document.createElement("button");
    b.textContent = action[0];
    b.onclick = () => { t.classList.remove("show"); action[1](); };
    t.append(b);
  }
  t.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove("show"), action ? 6000 : 2400);
}

// ---------------------------------------------------------------- settings

const openSettings = () => { syncSettingsUI(); setCollapsed(false); $("settings").classList.add("show"); };
const closeSettings = () => $("settings").classList.remove("show");
$("closeSettings").onclick = closeSettings;
$("openSessions").onclick = () => post({ type: "openSessions" });
document.addEventListener("keydown", (e) => { if (e.key === "Escape") { closeSettings(); hideMenu(); clearPicked(); } });
$("loadProfile").onclick = () => post({ type: "pickFile", target: "profile" });

let saveTimer;
function saveSettings() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => post({ type: "settings", settings }), 250);
}

function toggleSetting(k) {
  settings[k] = !settings[k];
  if (k === "autoSend" && settings[k]) { autoSince = Date.now(); autoQueued.clear(); }
  saveSettings();
  syncSettingsUI();
  if (k === "stealth") toast(settings.stealth ? "Hidden from screen share" : "Visible in screen share");
  if (k === "includeScreen") toast(settings.includeScreen ? "Claude will see your screen" : "Screen sharing with Claude off");
}

function syncSettingsUI() {
  document.querySelectorAll(".opt[data-setting]").forEach((o) => o.classList.toggle("on", !!settings[o.dataset.setting]));
  for (const id of ["meName", "profile", "intro"]) if (document.activeElement !== $(id)) $(id).value = settings[id] || "";
  $("sessionsFolder").textContent = settings.sessionsFolder || "";
  $("btnScreen").classList.toggle("on", !!settings.includeScreen);
  $("btnScreen").dataset.tip = settings.includeScreen ? "Claude sees your screen" : "Screen not shared";
}

document.querySelectorAll(".opt[data-setting]").forEach((o) => o.addEventListener("click", () => toggleSetting(o.dataset.setting)));
for (const id of ["meName", "profile", "intro"])
  $(id).addEventListener("input", () => { settings[id] = $(id).value; saveSettings(); if (id === "meName") scheduleRender(); });

// ---------------------------------------------------------------- messages from the host

function onHost(m) {
  switch (m.type) {
    case "settings":
      // the old built-in instructions give way to the new default
      if (/^I'm in a live conversation and you're my assistant\./.test(m.settings.intro || "")) delete m.settings.intro;
      settings = { ...DEFAULTS, ...m.settings };
      syncSettingsUI();
      scheduleRender();
      break;
    case "rows":
      rows.length = Math.min(rows.length, m.from);
      rows.push(...m.rows);
      scheduleRender();
      collectAuto();
      break;
    case "status": {
      if (m.session && !session) { autoSince = Date.now(); }
      session = !!m.session;
      if (session) sessionStarted = m.started;
      document.body.classList.toggle("in-session", session);
      const who = m.hearing ? ` · <b>${esc(m.hearing === "Me" ? nameOf("Me") : m.hearing)}</b>` : "";
      $("state").innerHTML = session ? esc(m.text) + who : m.note ? esc(m.note) : "Not listening";
      // your voice comes from its own mic channel; tell the user if Windows blocks it
      $("micBanner").classList.toggle("hidden", m.mic !== "error");
      if (m.mic === "error") $("micText").textContent = "Can't hear your microphone: " + m.micError;
      $("micFix").style.display = "none";
      if (session && m.mic === "starting") $("state").innerHTML = "Loading speech engine…";
      scheduleRender();
      break;
    }
    case "saved":
      toast("Session saved", m.path ? ["Open", () => post({ type: "openPath", path: m.path })] : null);
      break;
    case "claude":
      onClaude(m);
      break;
    case "toast":
      toast(m.text);
      break;
    case "file":
      if (m.target === "profile") {
        settings.profile = m.text;
        saveSettings();
        syncSettingsUI();
        toast("Loaded " + m.name);
      }
      break;
    case "hotkey":
      run(m.name === "say" ? "say" : "assist");
      break;
  }
}

if (host) {
  host.addEventListener("message", (e) => onHost(e.data));
  post({ type: "ready" });
} else {
  // Opened in a normal browser: sample data so the design can be checked.
  document.documentElement.classList.add("demo");
  const t0 = Date.now() - 4 * 60e3;
  onHost({ type: "settings", settings: { sessionsFolder: "C:\\Users\\you\\Documents\\Clueless Sessions" } });
  onHost({ type: "status", session: true, started: t0, mode: "live", text: "Listening", hearing: "Others" });
  onHost({ type: "rows", from: 0, rows: [
    { k: "1", s: "Alex", t: "Okay, let's get started with the terminal model review.", ts: t0, f: true, sent: true },
    { k: "2", s: "Alex", t: "The gate layout is done and the baggage area still needs work.", ts: t0 + 4e3, f: true, sent: true },
    { k: "3", s: "Abi", t: "When is the review with the client?", ts: t0 + 24e3, f: true, sent: false },
    { k: "4", s: "Me", t: "I think it's on Thursday, but let me double check.", ts: t0 + 40e3, f: true, sent: false },
    { k: "5", s: "Others", t: "Can you explain how the passenger flow simulation handles peak hours?", ts: t0 + 70e3, f: false, sent: false },
  ] });
  answers.push({ id: nextId++, ts: t0, kind: "warmup", label: "Warm-up", state: "skipped" });
  answers.push({ id: nextId++, ts: t0 + 80e3, label: "What should I say?", screen: true, state: "done",
    text: "**Short version:** it replays the hourly arrival curve from the flight schedule, so peaks come from real banks of departures.\n\nYou could say:\n- \"Each flight becomes a passenger arrival curve, so queues build up naturally during banks.\"\n- \"For peaks we stress-test at 120% of the busiest hour.\"" });
  render();
  if (location.hash === "#transcript") { setTab("transcript"); picked.add("3"); picked.add("4"); syncPicked(); }
}
