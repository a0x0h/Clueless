// Reads the meeting's own on-screen captions (which carry speaker names) and sends
// changed caption blocks to the desktop app. Turn captions on in the meeting first.
(() => {
  const host = location.hostname;
  const PLATFORM = host.includes("meet.google") ? "meet" : host.includes("zoom") ? "zoom" : "teams";
  const session = Date.now().toString(36);
  const ids = new WeakMap();
  let nextId = 1;
  const lastSent = new Map(); // key -> "speaker\u0000text"
  let lastBeat = 0;

  const NOISE = /^(arrow_downward|jump to bottom|keyboard_arrow_down|more_vert|close|\d{1,2}:\d{2}(:\d{2})?( ?[ap]m)?)$/i;

  const keyOf = (el) => {
    if (!ids.has(el)) ids.set(el, nextId++);
    return `${PLATFORM}:${session}:${ids.get(el)}`;
  };
  const lines = (el) =>
    (el.innerText || "").split("\n").map((s) => s.trim()).filter((s) => s && !NOISE.test(s));
  const textOf = (el) => (el ? (el.innerText || "").trim() : "");

  // Generic fallback: a caption block is the innermost element holding an avatar image
  // plus at least two lines of text (name, then what they said).
  function blocksByAvatar(region) {
    const cands = [...region.querySelectorAll("div")].filter(
      (d) => d.querySelector("img") && lines(d).length >= 2
    );
    return cands
      .filter((d) => !cands.some((o) => o !== d && d.contains(o)))
      .map((b) => {
        const ls = lines(b);
        return { el: b, speaker: ls[0], text: ls.slice(1).join(" ") };
      });
  }

  function meet() {
    const region =
      document.querySelector('div[role="region"][aria-label*="aption" i]') ||
      document.querySelector('div[jsname="dsyhDe"]') ||
      document.querySelector(".a4cQT");
    if (!region) return null;
    const named = [...region.querySelectorAll(".nMcdL")].map((b) => ({
      el: b,
      speaker: textOf(b.querySelector(".NWpY1d, .KcIKyf")),
      text: textOf(b.querySelector(".ygicle, .bh44bd")),
    }));
    return named.length && named.every((b) => b.speaker && b.text) ? named : blocksByAvatar(region);
  }

  function teams() {
    const texts = document.querySelectorAll('[data-tid="closed-caption-text"]');
    if (!texts.length) {
      const region = document.querySelector('[data-tid="closed-caption-v2-window-wrapper"], [data-tid="closed-captions-renderer"]');
      return region ? blocksByAvatar(region) : null;
    }
    return [...texts].map((t) => {
      const block = t.closest(".fui-ChatMessageCompact") || t.parentElement?.parentElement || t;
      return { el: block, speaker: textOf(block.querySelector('[data-tid="author"]')) || "Speaker", text: textOf(t) };
    });
  }

  function zoom() {
    // Full transcript panel has names; fall back to the subtitle overlay.
    const items = document.querySelectorAll(".lt-full-transcript__item");
    if (items.length) {
      return [...items].map((it) => ({
        el: it,
        speaker: textOf(it.querySelector(".lt-full-transcript__display-name")) || "Speaker",
        text: textOf(it.querySelector(".lt-full-transcript__message")),
      }));
    }
    const subs = document.querySelectorAll(".live-transcription-subtitle__item");
    if (subs.length) return [...subs].map((s) => ({ el: s, speaker: "Zoom", text: textOf(s) }));
    const region = document.querySelector("#live-transcription-subtitle, .live-transcription-subtitle__box");
    return region ? blocksByAvatar(region) : null;
  }

  function tick() {
    let blocks;
    try {
      blocks = PLATFORM === "meet" ? meet() : PLATFORM === "zoom" ? zoom() : teams();
    } catch (e) {
      return;
    }
    if (!blocks) return; // captions not on in this frame

    const items = [];
    for (const b of blocks) {
      if (!b.text) continue;
      const key = keyOf(b.el);
      const sig = b.speaker + "\u0000" + b.text;
      if (lastSent.get(key) === sig) continue;
      lastSent.set(key, sig);
      items.push({ key, speaker: b.speaker, text: b.text });
    }
    // Heartbeat while captions are visible, so the app knows a meeting is supplying names.
    const now = Date.now();
    if (items.length || now - lastBeat > 2000) {
      lastBeat = now;
      try {
        chrome.runtime.sendMessage({ type: "captions", payload: { platform: PLATFORM, items } });
      } catch (e) {
        /* extension reloaded; this page needs a refresh */
      }
    }
  }

  setInterval(tick, 400);
})();
