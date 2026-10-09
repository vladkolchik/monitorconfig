// Monitor Config — tiny local server: static UI + DDC/CI bridge via a persistent PowerShell worker.
// Also owns the background features: brightness schedule, global hotkeys and Windows autostart.
const http = require('http');
const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');
const readline = require('readline');

const PORT = Number(process.env.MONITORCONFIG_PORT) || 47821;
const APP_URL = `http://127.0.0.1:${PORT}/`;
const PUBLIC = path.join(__dirname, 'public');
const NO_OPEN = process.argv.includes('--no-open') || process.argv.includes('--background');
const IDLE_EXIT_MS = 15000; // the window pings every 3s; quit once it is gone (unless background features are on)
const HOTKEY_STEP = 10;

const HOME = process.env.MONITORCONFIG_HOME || path.join(process.env.APPDATA || __dirname, 'MonitorConfig');
const SETTINGS_FILE = path.join(HOME, 'settings.json');
const STARTUP_FILE = process.env.MONITORCONFIG_STARTUP ||
  path.join(process.env.APPDATA || '', 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Startup', 'MonitorConfig.vbs');

// ---------- settings ----------
const DEFAULTS = {
  hotkeys: false,
  schedule: {
    enabled: false,
    items: [
      { time: '08:00', value: 65 },
      { time: '19:00', value: 35 },
      { time: '22:30', value: 10 },
    ],
  },
};

function loadSettings() {
  try {
    const s = JSON.parse(fs.readFileSync(SETTINGS_FILE, 'utf8'));
    return { ...DEFAULTS, ...s, schedule: { ...DEFAULTS.schedule, ...s.schedule } };
  } catch {
    return structuredClone(DEFAULTS);
  }
}

function saveSettings() {
  fs.mkdirSync(HOME, { recursive: true });
  fs.writeFileSync(SETTINGS_FILE, JSON.stringify(settings, null, 2));
}

let settings = loadSettings();
let hotkeyError = null;

// ---------- DDC worker ----------
const worker = spawn('powershell.exe',
  ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(__dirname, 'ddc.ps1')],
  { windowsHide: true });
worker.on('exit', code => { console.error(`ddc worker exited (${code})`); process.exit(1); });

const waiting = [];
let ready;
const readyP = new Promise(r => (ready = r));
readline.createInterface({ input: worker.stdout }).on('line', line => {
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  if (msg && msg.ready) return ready();
  if (msg && msg.hotkey) return onHotkey(msg.hotkey);
  const w = waiting.shift();
  if (w) w(msg);
});

// One command in flight at a time. Pending writes to the same control are
// coalesced so dragging a slider never builds a backlog on the slow DDC bus.
const queue = [];
let busy = false;

function send(line) {
  return new Promise(resolve => { waiting.push(resolve); worker.stdin.write(line + '\n'); });
}

async function pump() {
  if (busy) return;
  busy = true;
  await readyP;
  while (queue.length) {
    const job = queue.shift();
    const res = await send(job.line());
    job.resolvers.forEach(r => r(res));
  }
  busy = false;
}

function command(line) {
  return new Promise(r => { queue.push({ line: () => line, resolvers: [r] }); pump(); });
}

function ddcSet(id, vcp, value) {
  return new Promise(r => {
    const key = `${id}:${vcp}`;
    const pending = queue.find(j => j.key === key);
    if (pending) { pending.value = value; pending.resolvers.push(r); return; }
    const job = { key, value, resolvers: [r] };
    job.line = () => `set ${id} ${vcp} ${job.value}`;
    queue.push(job);
    pump();
  });
}

// ---------- monitor state ----------
const VCP = { brightness: 0x10, contrast: 0x12, volume: 0x62 };
let monitors = [];
let rev = 0; // bumped when something other than the UI changes a value

async function refresh() {
  const list = await command('list');
  monitors = Array.isArray(list) ? list : [];
  return monitors;
}

async function setControl(id, control, value) {
  const res = await ddcSet(id, VCP[control], value);
  const f = monitors[id] && monitors[id].features[control];
  if (res && res.ok && f) f.value = value;
  return res || { ok: false };
}

// Set brightness on every monitor, as a percentage of each monitor's own range.
async function setAllBrightness(percentFn) {
  const jobs = monitors
    .filter(m => m.features.brightness)
    .map(m => {
      const f = m.features.brightness;
      const p = Math.max(0, Math.min(100, percentFn(Math.round(f.value / f.max * 100))));
      return setControl(m.id, 'brightness', Math.round(p / 100 * f.max));
    });
  await Promise.all(jobs);
  rev++;
}

function onHotkey(id) {
  const s = HOTKEY_STEP;
  // snap to the step grid so 23 → 30 / 20 rather than 33 / 13
  setAllBrightness(p => id === 1 ? Math.floor(p / s) * s + s : Math.ceil(p / s) * s - s);
}

async function applyHotkeys() {
  hotkeyError = null;
  const res = await command(`hotkeys ${settings.hotkeys ? 'on' : 'off'}`);
  if (settings.hotkeys && !(res && res.ok)) hotkeyError = (res && res.error) || 'failed';
}

// ---------- schedule ----------
const minutes = t => { const [h, m] = t.split(':').map(Number); return h * 60 + m; };
let lastFired = null;

function currentItem(now = new Date()) {
  const items = [...settings.schedule.items].sort((a, b) => minutes(a.time) - minutes(b.time));
  if (!items.length) return null;
  const cur = now.getHours() * 60 + now.getMinutes();
  return [...items].reverse().find(i => minutes(i.time) <= cur) || items[items.length - 1];
}

function tickSchedule() {
  if (!settings.schedule.enabled) return;
  const now = new Date();
  const hhmm = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
  const item = settings.schedule.items.find(i => i.time === hhmm);
  const stamp = now.toDateString() + ' ' + hhmm;
  if (item && lastFired !== stamp) {
    lastFired = stamp;
    setAllBrightness(() => item.value);
  }
}

// When the schedule is switched on (or the app starts with it on), jump to the level that is in effect right now.
function applyCurrentScheduleItem() {
  const item = settings.schedule.enabled && currentItem();
  if (item) return setAllBrightness(() => item.value);
}

// ---------- autostart ----------
const isAutostart = () => fs.existsSync(STARTUP_FILE);

function setAutostart(on) {
  if (!on) { fs.rmSync(STARTUP_FILE, { force: true }); return; }
  fs.mkdirSync(path.dirname(STARTUP_FILE), { recursive: true });
  const q = s => s.replace(/"/g, '""');
  fs.writeFileSync(STARTUP_FILE, [
    "' Monitor Config autostart: runs the background service (schedule + hotkeys) without a window.",
    'Set sh = CreateObject("WScript.Shell")',
    `sh.CurrentDirectory = "${q(__dirname)}"`,
    `sh.Run """${q(process.execPath)}"" ""${q(path.join(__dirname, 'server.js'))}"" --background", 0, False`,
    '',
  ].join('\r\n'));
}

function publicSettings() {
  return {
    autostart: isAutostart(),
    hotkeys: settings.hotkeys,
    hotkeyError,
    schedule: settings.schedule,
  };
}

function validSchedule(s) {
  return s && typeof s.enabled === 'boolean' && Array.isArray(s.items) && s.items.length <= 12 &&
    s.items.every(i => /^([01]\d|2[0-3]):[0-5]\d$/.test(i.time) && Number.isInteger(i.value) && i.value >= 0 && i.value <= 100);
}

// ---------- HTTP ----------
const TYPES = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.svg': 'image/svg+xml' };
let lastPing = Date.now() + 45000; // grace period for the window to open

function json(res, code, body) {
  res.writeHead(code, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(body));
}

function readBody(req) {
  return new Promise(resolve => {
    let data = '';
    req.on('data', c => (data += c));
    req.on('end', () => { try { resolve(JSON.parse(data || '{}')); } catch { resolve({}); } });
  });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, APP_URL);
  const api = `${req.method} ${url.pathname}`;

  if (api === 'GET /api/monitors') {
    if (url.searchParams.has('refresh') || !monitors.length) await refresh();
    return json(res, 200, { rev, monitors });
  }
  if (api === 'POST /api/set') {
    const { id, control, value } = await readBody(req);
    const v = Math.round(Number(value));
    if (VCP[control] === undefined || !Number.isInteger(id) || !Number.isFinite(v) || v < 0 || v > 65535) {
      return json(res, 400, { ok: false, error: 'bad request' });
    }
    return json(res, 200, await setControl(id, control, v));
  }
  if (api === 'GET /api/settings') {
    return json(res, 200, publicSettings());
  }
  if (api === 'POST /api/settings') {
    const body = await readBody(req);
    if ('schedule' in body && !validSchedule(body.schedule)) return json(res, 400, { error: 'bad schedule' });
    if ('autostart' in body) setAutostart(!!body.autostart);
    if ('hotkeys' in body) { settings.hotkeys = !!body.hotkeys; await applyHotkeys(); }
    if ('schedule' in body) {
      const wasOn = settings.schedule.enabled;
      settings.schedule = { enabled: body.schedule.enabled, items: body.schedule.items.map(({ time, value }) => ({ time, value })) };
      if (settings.schedule.enabled && !wasOn) await applyCurrentScheduleItem();
    }
    saveSettings();
    return json(res, 200, publicSettings());
  }
  if (api === 'GET /api/ping') {
    lastPing = Date.now();
    return json(res, 200, { rev });
  }
  if (api === 'POST /api/quit') {
    json(res, 200, { ok: true });
    return setTimeout(shutdown, 100);
  }

  const file = path.join(PUBLIC, url.pathname === '/' ? 'index.html' : path.normalize(url.pathname));
  if (!file.startsWith(PUBLIC)) { res.writeHead(403); return res.end(); }
  fs.readFile(file, (err, buf) => {
    if (err) { res.writeHead(404); return res.end('not found'); }
    res.writeHead(200, { 'Content-Type': TYPES[path.extname(file)] || 'application/octet-stream' });
    res.end(buf);
  });
});

// ---------- window ----------
function openWindow() {
  const candidates = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  ];
  const browser = candidates.find(p => fs.existsSync(p));
  if (!browser) {
    spawn('cmd', ['/c', 'start', '', APP_URL], { detached: true, stdio: 'ignore' }).unref();
    return;
  }
  spawn(browser, [
    `--app=${APP_URL}`,
    `--user-data-dir=${path.join(HOME, 'browser')}`,
    '--window-size=360,560',
    '--no-first-run',
    '--no-default-browser-check',
  ], { detached: true, stdio: 'ignore' }).unref();
}

function shutdown() {
  worker.removeAllListeners('exit');
  worker.kill();
  process.exit(0);
}

const backgroundWanted = () => settings.hotkeys || settings.schedule.enabled;

server.on('error', err => {
  if (err.code === 'EADDRINUSE') { // already running — just bring up a window
    if (!NO_OPEN) openWindow();
    shutdown();
  }
  throw err;
});

server.listen(PORT, '127.0.0.1', async () => {
  console.log(`Monitor Config: ${APP_URL}`);
  if (!NO_OPEN) openWindow();

  await refresh();
  if (settings.hotkeys) await applyHotkeys();
  await applyCurrentScheduleItem();
  setInterval(tickSchedule, 10000);

  setInterval(() => {
    if (!NO_OPEN && !backgroundWanted() && Date.now() - lastPing > IDLE_EXIT_MS) shutdown();
  }, 2000);
});
