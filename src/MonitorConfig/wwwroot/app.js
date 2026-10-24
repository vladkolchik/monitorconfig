const PRESETS = [
  { v: 10, t: 'ночь' },
  { v: 35, t: 'вечер' },
  { v: 65, t: 'работа' },
  { v: 100, t: 'день' },
];
const ROWS = [
  { key: 'contrast', label: 'Контраст' },
  { key: 'volume', label: 'Громкость' },
];

const $ = id => document.getElementById(id);
const state = { monitors: [], cur: 0 };
const mon = () => state.monitors[state.cur];

// ---------- status ----------
let inflight = 0;
function status(kind, text) {
  const el = $('status');
  el.className = 'status ' + kind;
  el.lastElementChild.textContent = text;
}

// ---------- writes (throttled per control, last value always wins) ----------
const senders = {};
function write(control, value) {
  const m = mon();
  if (!m) return;
  m.features[control].value = value;
  const key = m.id + ':' + control;
  const s = senders[key] || (senders[key] = { last: 0, timer: null });
  s.value = value;
  clearTimeout(s.timer);
  s.timer = setTimeout(() => { s.last = Date.now(); post(m.id, control, s.value); },
    Math.max(0, 90 - (Date.now() - s.last)));
}

async function post(id, control, value) {
  inflight++;
  status('busy', 'запись…');
  let ok = false;
  try {
    const r = await fetch('/api/set', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ id, control, value }),
    });
    ok = (await r.json()).ok;
  } catch {}
  inflight--;
  if (!ok) status('err', 'монитор не принял значение');
  else if (!inflight) status('ok', 'DDC/CI · готово');
}

// ---------- rendering ----------
const pct = f => (f.max ? Math.round(f.value / f.max * 100) : 0);

function paintRange(input) {
  input.style.setProperty('--p', (input.value / input.max * 100) + '%');
}

function renderBrightness() {
  const f = mon().features.brightness;
  const input = $('bright');
  input.max = f.max;
  input.value = f.value;
  paintRange(input);
  const p = pct(f);
  $('bval').textContent = p;
  document.querySelectorAll('.preset').forEach(b => b.classList.toggle('on', +b.dataset.v === p));
}

function renderRows() {
  const box = $('rows');
  box.innerHTML = '';
  const f = mon().features;
  for (const r of ROWS) {
    if (!f[r.key]) continue;
    const row = document.createElement('label');
    row.className = 'row';
    row.innerHTML = `<span class="k">${r.label}</span><input type="range" min="0" max="${f[r.key].max}" value="${f[r.key].value}"><span class="v">${pct(f[r.key])}</span>`;
    const input = row.querySelector('input');
    const out = row.querySelector('.v');
    paintRange(input);
    input.addEventListener('input', () => {
      paintRange(input);
      write(r.key, +input.value);
      out.textContent = pct(f[r.key]);
    });
    box.appendChild(row);
  }
  box.hidden = !box.children.length;
}

function renderTabs() {
  const tabs = $('tabs');
  tabs.hidden = state.monitors.length < 2;
  tabs.innerHTML = '';
  state.monitors.forEach((m, i) => {
    const b = document.createElement('button');
    b.textContent = (i + 1) + (m.primary ? ' · основной' : '');
    b.setAttribute('aria-selected', i === state.cur);
    b.onclick = () => { state.cur = i; render(); };
    tabs.appendChild(b);
  });
}

function render() {
  renderTabs();
  const m = mon();
  const usable = m && m.features.brightness;
  $('panel').hidden = !usable;
  $('empty').hidden = !!usable;
  if (!m) {
    $('name').textContent = 'Монитор не найден';
    $('meta').textContent = '';
    return;
  }
  $('name').textContent = m.name || 'Монитор';
  $('meta').textContent = `${m.width}×${m.height}` + (m.primary ? ' · основной' : '');
  if (usable) { renderBrightness(); renderRows(); }
}

function setBrightness(v) {
  const f = mon() && mon().features.brightness;
  if (!f) return;
  v = Math.max(0, Math.min(f.max, Math.round(v)));
  if (v === f.value) return;
  write('brightness', v);
  renderBrightness();
}

// ---------- load ----------
let rev = null;

async function load(rescan) {
  if (rescan) $('refresh').classList.add('spin');
  if (rescan || rev === null) status('busy', 'чтение мониторов…');
  try {
    const r = await fetch('/api/monitors' + (rescan ? '?refresh=1' : ''));
    const data = await r.json();
    if (!Array.isArray(data.monitors)) throw new Error();
    state.monitors = data.monitors;
    rev = data.rev;
    state.cur = Math.min(state.cur, Math.max(0, state.monitors.length - 1));
    render();
    if (!inflight) status(state.monitors.length ? 'ok' : 'err', state.monitors.length ? 'DDC/CI · готово' : 'мониторы не найдены');
  } catch {
    state.monitors = [];
    render();
    status('err', 'сервер недоступен');
  }
  $('refresh').classList.remove('spin');
}

// ---------- settings view ----------
let cfg = null;

function nowMinutes() { const d = new Date(); return d.getHours() * 60 + d.getMinutes(); }
const toMin = t => +t.slice(0, 2) * 60 + +t.slice(3);

function activeSlot(items) {
  if (!items.length) return -1;
  const order = items.map((it, i) => [toMin(it.time), i]).sort((a, b) => a[0] - b[0]);
  const past = order.filter(([m]) => m <= nowMinutes());
  return (past.length ? past[past.length - 1] : order[order.length - 1])[1];
}

function renderSettings() {
  $('opt-autostart').checked = cfg.autostart;
  $('opt-hotkeys').checked = cfg.hotkeys;
  $('opt-tray').checked = cfg.trayOnly;
  $('opt-schedule').checked = cfg.schedule.enabled;
  const hk = $('hk-desc');
  hk.classList.toggle('err', !!cfg.hotkeyError);
  hk.innerHTML = cfg.hotkeyError
    ? 'сочетание занято другой программой'
    : '<kbd>Ctrl</kbd><kbd>Alt</kbd><kbd>PgUp</kbd> / <kbd>PgDn</kbd> — яркость ±10';

  const box = $('sched');
  box.classList.toggle('off', !cfg.schedule.enabled);
  box.innerHTML = '';
  const items = cfg.schedule.items;
  const active = cfg.schedule.enabled ? activeSlot(items) : -1;
  items.forEach((it, i) => {
    const row = document.createElement('div');
    row.className = 'slot' + (i === active ? ' now' : '');
    row.innerHTML = `<input type="time" value="${it.time}" aria-label="Время"><input type="range" min="0" max="100" value="${it.value}" aria-label="Яркость"><span class="v">${it.value}</span><button class="del" title="Удалить" aria-label="Удалить">×</button>`;
    const [time, range] = row.querySelectorAll('input');
    paintRange(range);
    time.addEventListener('change', () => { if (time.value) { it.time = time.value; saveSchedule(); } });
    range.addEventListener('input', () => { it.value = +range.value; paintRange(range); row.querySelector('.v').textContent = it.value; saveSchedule(); });
    row.querySelector('.del').addEventListener('click', () => { items.splice(i, 1); saveSchedule(true); });
    box.appendChild(row);
  });
  $('sched-add').disabled = items.length >= 12;

  const background = cfg.hotkeys || cfg.schedule.enabled;
  $('bg-note').hidden = !(background || cfg.trayOnly);
  $('auto-badge').hidden = !background;
}

async function saveSettings(patch) {
  try {
    const r = await fetch('/api/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(patch),
    });
    if (!r.ok) throw new Error();
    cfg = await r.json();
    status('ok', 'сохранено');
  } catch {
    status('err', 'не удалось сохранить');
  }
  renderSettings();
}

let schedTimer;
function saveSchedule(immediate) {
  clearTimeout(schedTimer);
  if (immediate) renderSettings();
  schedTimer = setTimeout(() => saveSettings({ schedule: cfg.schedule }), immediate ? 0 : 400);
}

async function loadSettings() {
  try { cfg = await (await fetch('/api/settings')).json(); renderSettings(); } catch {}
}

function showSettings(on) {
  $('view-main').hidden = on;
  $('view-settings').hidden = !on;
  $('hint').hidden = on;
  if (on) loadSettings();
}

// ---------- wiring ----------
$('presets').innerHTML = PRESETS.map(p =>
  `<button class="preset" data-v="${p.v}"><span class="lvl"><i style="width:${p.v}%"></i></span><span class="n">${p.v}</span><span class="t">${p.t}</span></button>`
).join('');
$('presets').addEventListener('click', e => {
  const b = e.target.closest('.preset');
  if (b) setBrightness(+b.dataset.v / 100 * mon().features.brightness.max);
});

$('bright').addEventListener('input', e => {
  write('brightness', +e.target.value);
  renderBrightness();
});

$('refresh').addEventListener('click', () => load(true));
$('open-settings').addEventListener('click', () => showSettings(true));
$('close-settings').addEventListener('click', () => showSettings(false));
$('opt-autostart').addEventListener('change', e => saveSettings({ autostart: e.target.checked }));
$('opt-hotkeys').addEventListener('change', e => saveSettings({ hotkeys: e.target.checked }));
$('opt-tray').addEventListener('change', e => saveSettings({ trayOnly: e.target.checked }));
$('opt-schedule').addEventListener('change', e => {
  cfg.schedule.enabled = e.target.checked;
  saveSchedule(true);
  setTimeout(() => load(), 600); // switching on applies the current slot right away
});
$('sched-add').addEventListener('click', () => {
  const last = cfg.schedule.items[cfg.schedule.items.length - 1];
  const t = last ? Math.min(toMin(last.time) + 60, 23 * 60 + 59) : 12 * 60;
  cfg.schedule.items.push({ time: `${String(Math.floor(t / 60)).padStart(2, '0')}:${String(t % 60).padStart(2, '0')}`, value: 50 });
  saveSchedule(true);
});
$('quit').addEventListener('click', async () => {
  try { await fetch('/api/quit', { method: 'POST' }); } catch {}
  window.close();
});

window.addEventListener('wheel', e => {
  const f = mon() && mon().features.brightness;
  if (!f || !$('view-settings').hidden || e.target.closest('.row')) return;
  setBrightness(f.value + (e.deltaY < 0 ? 1 : -1) * f.max * 0.02);
}, { passive: true });

window.addEventListener('keydown', e => {
  const f = mon() && mon().features.brightness;
  if (!f || !$('view-settings').hidden || e.target.matches('input')) return;
  if (e.key === 'ArrowUp' || e.key === 'ArrowRight') setBrightness(f.value + f.max * 0.05);
  else if (e.key === 'ArrowDown' || e.key === 'ArrowLeft') setBrightness(f.value - f.max * 0.05);
  else if (/^[1-4]$/.test(e.key)) setBrightness(PRESETS[+e.key - 1].v / 100 * f.max);
  else return;
  e.preventDefault();
});

// pick up changes made by hotkeys / schedule / the tray menu
async function ping() {
  try {
    const r = await (await fetch('/api/ping')).json();
    if (rev !== null && r.rev !== rev && !inflight) load();
  } catch {}
}
setInterval(ping, 3000);

load().then(ping);
loadSettings();
