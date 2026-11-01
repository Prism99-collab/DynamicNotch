// Dynamic Notch Media — the browser's side of the link. It gathers what every tab plays (from content.js) and
// hands the list to Dynamic Notch on this PC (ws://127.0.0.1, nothing goes online), and passes the notch's buttons
// back to the right tab. When the notch isn't running it simply waits and tries again now and then.
const api = globalThis.browser ?? globalThis.chrome;
const LINK = 'ws://127.0.0.1:47853/dynamic-notch';

const tabs = new Map();   // tabId -> Map(frameId -> { s, at })
let ws = null;
let open = false;
let retry = 0;
let retryTimer = 0;
let sendTimer = 0;
let pingTimer = 0;

function browserName() {
  const brands = (navigator.userAgentData?.brands || []).map(b => b.brand).join(' ');
  const ua = navigator.userAgent;
  if (/Firefox/i.test(ua)) return 'Firefox';
  if (/Brave/i.test(brands) || navigator.brave) return 'Brave';
  if (/Edge/i.test(brands) || /Edg\//.test(ua)) return 'MSEdge';
  if (/Opera/i.test(brands) || /OPR\//.test(ua)) return 'Opera';
  if (/Vivaldi/i.test(brands) || /Vivaldi/i.test(ua)) return 'Vivaldi';
  return 'Chrome';
}

// one entry per tab: the frame that plays, or the one heard from last
function list() {
  const out = [];
  for (const [id, frames] of tabs) {
    let best = null;
    for (const [frameId, f] of frames)
      if (!best || (f.s.playing && !best.s.playing) || (f.s.playing === best.s.playing && f.at > best.at)) best = { ...f, frameId };
    if (best) out.push({ id, frame: best.frameId, ...best.s, at: best.at });
  }
  return out.slice(0, 40);
}

function post(msg) {
  if (!open) return;
  try { ws.send(JSON.stringify(msg)); } catch { }
}

function sendSoon() {
  clearTimeout(sendTimer);
  sendTimer = setTimeout(() => post({ t: 'tabs', browser: browserName(), tabs: list() }), 150);
}

function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  clearTimeout(retryTimer);
  try { ws = new WebSocket(LINK); } catch { schedule(); return; }
  ws.onopen = () => {
    open = true;
    retry = 0;
    post({ t: 'hello', app: 'dynamic-notch-media', v: 1, browser: browserName() });
    sendSoon();
    // a message every 20 s keeps the link (and this worker) awake while the notch is there
    clearInterval(pingTimer);
    pingTimer = setInterval(() => post({ t: 'ping' }), 20000);
  };
  ws.onmessage = e => {
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    if (m.t !== 'cmd' || typeof m.tab !== 'number') return;
    if (m.cmd === 'focus') {
      api.tabs.update(m.tab, { active: true }).then(t => t && api.windows.update(t.windowId, { focused: true })).catch(() => { });
      return;
    }
    const frames = tabs.get(m.tab);
    let frameId = 0;
    if (frames) for (const [f, v] of frames) if (v.s.playing) { frameId = f; break; } else frameId = f;
    api.tabs.sendMessage(m.tab, { cmd: m.cmd, pos: m.pos }, { frameId }).catch(() => { });
  };
  ws.onclose = () => { open = false; clearInterval(pingTimer); schedule(); };
  ws.onerror = () => { };
}

function schedule() {
  clearTimeout(retryTimer);
  retryTimer = setTimeout(connect, Math.min(30000, 2000 * 2 ** retry++));
}

api.runtime.onMessage.addListener((msg, sender) => {
  if (!msg || msg.t !== 'state' || !sender.tab) return;
  const id = sender.tab.id, frameId = sender.frameId || 0;
  let frames = tabs.get(id);
  if (msg.s) {
    if (!frames) tabs.set(id, frames = new Map());
    frames.set(frameId, { s: msg.s, at: Date.now() });
  } else if (frames) {
    frames.delete(frameId);
    if (!frames.size) tabs.delete(id);
  }
  if (!open) connect();
  sendSoon();
});

api.tabs.onRemoved.addListener(id => { if (tabs.delete(id)) sendSoon(); });

// the worker may sleep; a light alarm wakes it to look for the notch again
api.alarms.create('dynamic-notch', { periodInMinutes: 1 });
api.alarms.onAlarm.addListener(() => { if (!open) connect(); });
api.runtime.onStartup?.addListener(connect);
api.runtime.onInstalled?.addListener(connect);
connect();
