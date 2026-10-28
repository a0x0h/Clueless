// Relays caption batches from meeting tabs to the desktop app. Fetching from here
// (not the page) avoids the meeting site's CORS / private-network rules.
chrome.runtime.onMessage.addListener((msg) => {
  if (msg && msg.type === "captions") {
    fetch("http://localhost:47321/captions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(msg.payload),
    }).catch(() => {}); // desktop app not running: drop silently
  }
});
