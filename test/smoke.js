// End-to-end smoke test against real hardware: starts the server on a spare port with a throwaway
// settings dir, drives the API, presses the global hotkeys and restores the original brightness.
//   npm test            — everything except the minute-boundary schedule check
//   npm test -- --full  — also waits (up to ~70s) for a scheduled slot to fire on its own
const { spawn, execFileSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const PORT = 47899;
const BASE = `http://127.0.0.1:${PORT}`;
const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'monitorconfig-test-'));
const STARTUP = path.join(TMP, 'Startup', 'MonitorConfig.vbs');
const FULL = process.argv.includes('--full');

const sleep = ms => new Promise(r => setTimeout(r, ms));
let passed = 0;
function check(cond, name, extra = '') {
  if (!cond) throw new Error(`FAIL: ${name} ${extra}`);
  passed++;
  console.log(`  ok  ${name}`);
}

async function api(method, p, body) {
  const r = await fetch(BASE + p, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  return { status: r.status, body: await r.json() };
}

const brightness = async (rescan = true) => {
  const { body } = await api('GET', '/api/monitors' + (rescan ? '?refresh=1' : ''));
  return body.monitors[0].features.brightness.value;
};

function press(vk) { // Ctrl+Alt+<vk>; PgUp/PgDn are extended keys
  execFileSync('powershell.exe', ['-NoProfile', '-Command', `
    Add-Type -Name K -Namespace W -MemberDefinition '[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);'
    [W.K]::keybd_event(0x11,0,0,[UIntPtr]::Zero); [W.K]::keybd_event(0x12,0,0,[UIntPtr]::Zero)
    [W.K]::keybd_event(${vk},0,1,[UIntPtr]::Zero); [W.K]::keybd_event(${vk},0,3,[UIntPtr]::Zero)
    [W.K]::keybd_event(0x12,0,2,[UIntPtr]::Zero); [W.K]::keybd_event(0x11,0,2,[UIntPtr]::Zero)`]);
}

const hhmm = d => `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;

(async () => {
  const server = spawn(process.execPath, [path.join(__dirname, '..', 'server.js'), '--no-open'], {
    env: { ...process.env, MONITORCONFIG_PORT: PORT, MONITORCONFIG_HOME: TMP, MONITORCONFIG_STARTUP: STARTUP },
    stdio: 'ignore',
  });
  let original;
  try {
    for (let i = 0; i < 40; i++) { try { await api('GET', '/api/ping'); break; } catch { await sleep(250); } }

    console.log('monitors');
    const { body: list } = await api('GET', '/api/monitors');
    check(list.monitors.length >= 1, 'at least one monitor found');
    check(list.monitors[0].features.brightness, 'brightness readable over DDC/CI');
    original = list.monitors[0].features.brightness.value;

    console.log('writes');
    check((await api('POST', '/api/set', { id: 0, control: 'brightness', value: 30 })).body.ok, 'set brightness 30');
    check(await brightness() === 30, 'hardware reads back 30');
    await Promise.all([31, 33, 35, 37, 39, 40].map(v => api('POST', '/api/set', { id: 0, control: 'brightness', value: v })));
    check(await brightness() === 40, 'burst of writes settles on the last value (40)');
    check((await api('POST', '/api/set', { id: 0, control: 'gamma', value: 1 })).status === 400, 'unknown control rejected');
    check((await api('POST', '/api/set', { id: 0, control: 'brightness', value: -5 })).status === 400, 'negative value rejected');

    console.log('hotkeys');
    const hk = (await api('POST', '/api/settings', { hotkeys: true })).body;
    check(hk.hotkeys && !hk.hotkeyError, 'Ctrl+Alt+PgUp/PgDn registered', hk.hotkeyError || '');
    const rev0 = (await api('GET', '/api/ping')).body.rev;
    press(0x22); await sleep(700);
    check(await brightness() === 30, 'Ctrl+Alt+PgDn: 40 → 30');
    press(0x21); await sleep(400);
    check((await api('GET', '/api/osd')).body.visible === true, 'OSD plate shown after the hotkey');
    await sleep(300);
    check(await brightness() === 40, 'Ctrl+Alt+PgUp: 30 → 40');
    await sleep(1800);
    check((await api('GET', '/api/osd')).body.visible === false, 'OSD plate hides by itself');
    check((await api('GET', '/api/ping')).body.rev > rev0, 'rev bumped so open windows refresh');
    await api('POST', '/api/settings', { hotkeys: false });
    press(0x22); await sleep(700);
    check(await brightness() === 40, 'hotkeys off: key press ignored');

    console.log('schedule');
    const now = new Date();
    const earlier = new Date(now - 60 * 60000);
    check((await api('POST', '/api/settings', { schedule: { enabled: true, items: [{ time: '25:00', value: 5 }] } })).status === 400, 'invalid time rejected');
    const s = (await api('POST', '/api/settings', {
      schedule: { enabled: true, items: [{ time: hhmm(earlier), value: 25 }, { time: hhmm(new Date(+now + 3 * 3600000)), value: 90 }] },
    })).body;
    check(s.schedule.enabled && s.schedule.items.length === 2, 'schedule saved');
    check(await brightness() === 25, 'enabling applies the slot in effect now (25)');
    const saved = JSON.parse(fs.readFileSync(path.join(TMP, 'settings.json'), 'utf8'));
    check(saved.schedule.enabled && saved.schedule.items[0].value === 25, 'settings persisted to disk');

    if (FULL) {
      const next = new Date(+new Date() + 60000);
      await api('POST', '/api/settings', { schedule: { enabled: false, items: [] } });
      await api('POST', '/api/settings', { schedule: { enabled: true, items: [{ time: hhmm(next), value: 45 }] } });
      await api('POST', '/api/set', { id: 0, control: 'brightness', value: 20 });
      console.log(`  ..  waiting for ${hhmm(next)}`);
      let hit = false;
      for (let i = 0; i < 80 && !hit; i++) { await sleep(1000); hit = (await brightness(false)) === 45; }
      check(hit, 'slot fires on its own at the minute boundary');
    }
    await api('POST', '/api/settings', { schedule: { enabled: false, items: [] } });

    console.log('autostart');
    await api('POST', '/api/settings', { autostart: true });
    check(fs.existsSync(STARTUP), 'startup script created');
    const vbs = fs.readFileSync(STARTUP, 'utf8');
    check(vbs.includes(process.execPath) && vbs.includes('--background'), 'startup script runs node server.js --background');
    check((await api('GET', '/api/settings')).body.autostart === true, 'autostart reported on');
    await api('POST', '/api/settings', { autostart: false });
    check(!fs.existsSync(STARTUP), 'startup script removed');
  } finally {
    if (original !== undefined) await api('POST', '/api/set', { id: 0, control: 'brightness', value: original }).catch(() => {});
    await api('POST', '/api/quit').catch(() => {});
    await sleep(500);
    console.log('shutdown');
    check(server.exitCode !== null, '/api/quit stops the server');
    fs.rmSync(TMP, { recursive: true, force: true });
  }
  console.log(`\n${passed} checks passed`);
})().catch(e => { console.error(e.message); process.exit(1); });
