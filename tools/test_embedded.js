// Шигтгэсэн WarKey-ийн локал API-г бодит exe-ээр турших (платформын token + presence файл идэвхтэй байх ёстой)
'use strict';
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const http = require('http');
const os = require('os');

const EXE = process.argv[2];
const PORT = 47899, SECRET = 'test-secret-' + Date.now();
const tokenFile = path.join(process.env.APPDATA, 'garena-mn-client', 'token.json');
const token = JSON.parse(fs.readFileSync(tokenFile, 'utf8')).token;
if (!token) { console.error('token алга'); process.exit(2); }
const profile = path.join(process.env.LOCALAPPDATA, 'LexusWarKey', 'profile.json');
const backup = profile + '.test-backup';
if (fs.existsSync(profile)) fs.copyFileSync(profile, backup);

let pass = 0, fail = 0;
const chk = (n, c, extra = '') => { if (c) { pass++; console.log('PASS ' + n + (extra ? ' — ' + extra : '')); } else { fail++; console.log('FAIL ' + n + (extra ? ' — ' + extra : '')); } };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
function api(method, route, body, secret = SECRET) {
  return new Promise((resolve) => {
    const data = body ? Buffer.from(JSON.stringify(body)) : null;
    const req = http.request({ host: '127.0.0.1', port: PORT, path: route, method, timeout: 4000,
      headers: { 'x-warkey-secret': secret, 'content-type': 'application/json', 'content-length': data ? data.length : 0 } }, (res) => {
      let buf = ''; res.on('data', (d) => (buf += d)); res.on('end', () => { try { resolve({ ...JSON.parse(buf || '{}'), httpStatus: res.statusCode }); } catch { resolve({ httpStatus: res.statusCode, raw: buf }); } });
    });
    req.on('error', (e) => resolve({ httpStatus: 0, error: e.message })); req.on('timeout', () => req.destroy(new Error('timeout')));
    if (data) req.write(data); req.end();
  });
}

(async () => {
  const proc = spawn(EXE, ['--embedded'], { stdio: 'ignore', windowsHide: true, env: { ...process.env, GARENA_WARKEY_TOKEN: token, GARENA_WARKEY_PORT: String(PORT), GARENA_WARKEY_SECRET: SECRET } });
  let exitCode = null; proc.on('exit', (c) => (exitCode = c));
  await sleep(3500);
  chk('процесс асаж амьд байна (цонхгүй)', exitCode === null, exitCode === null ? 'pid ' + proc.pid : 'exit=' + exitCode);
  const noSecret = await api('GET', '/state', null, 'wrong');
  chk('нууц толгойгүй → 401', noSecret.httpStatus === 401, 'status=' + noSecret.httpStatus);
  const st = await api('GET', '/state');
  chk('GET /state ok', st.httpStatus === 200 && st.ok === true, JSON.stringify({ version: st.version, account: st.account, entitled: st.entitled, locked: st.locked, hook: st.hookInstalled, gameRunning: st.gameRunning, inv: (st.inventory || []).length, chat: (st.chat || []).length }));
  chk('inventory 6 слот, skills массив', (st.inventory || []).length === 6 && Array.isArray(st.skills));
  chk('locked=false (платформ идэвхтэй тул)', st.locked === false);
  // inventory slot 5 (6 дахь) → товч "J" (0x4A), дараа нь арилгана
  const s1 = await api('POST', '/inventory', { slot: 5, vk: 0x4A });
  chk('POST /inventory slot6=J', s1.httpStatus === 200 && s1.inventory[5].from === 'J', JSON.stringify(s1.inventory && s1.inventory[5]));
  const s2 = await api('POST', '/inventory', { slot: 5, vk: 0 });
  chk('POST /inventory slot6 арилгах', s2.httpStatus === 200 && s2.inventory[5].from === '', JSON.stringify(s2.inventory && s2.inventory[5]));
  const bad = await api('POST', '/inventory', { slot: 9, vk: 65 });
  chk('буруу slot → 400', bad.httpStatus === 400, bad.error);
  // chat нэмэх/солих/устгах
  const before = (st.chat || []).length;
  const c1 = await api('POST', '/chat/add', { vk: 0x78, message: 'test gg' });   // F9
  chk('POST /chat/add F9 "test gg"', c1.httpStatus === 200 && c1.chat.length === before + 1 && c1.chat[c1.chat.length - 1].key === 'F9', JSON.stringify(c1.chat && c1.chat[c1.chat.length - 1]));
  const idx = c1.chat.length - 1;
  const c2 = await api('POST', '/chat/setmessage', { index: idx, message: 'test gg 2' });
  chk('POST /chat/setmessage', c2.httpStatus === 200 && c2.chat[idx].message === 'test gg 2');
  const c3 = await api('POST', '/chat/remove', { index: idx });
  chk('POST /chat/remove', c3.httpStatus === 200 && c3.chat.length === before);
  const sk = await api('POST', '/skill', { id: 'A000', letter: 'Q' });
  chk('POST /skill үл мэдэх id → 404', sk.httpStatus === 404, sk.error);
  const nf = await api('GET', '/nothing');
  chk('үл мэдэх route → 404', nf.httpStatus === 404);
  // overlay toggle (тоглоомгүй ч overlay цонх гарна) → буцааж хаана
  const o1 = await api('POST', '/overlay');
  chk('POST /overlay нээв', o1.httpStatus === 200 && o1.overlayOpen === true, 'overlayOpen=' + o1.overlayOpen + (o1.error ? ' ' + o1.error : ''));
  await sleep(500);
  const o2 = await api('POST', '/overlay');
  chk('POST /overlay хаав', o2.httpStatus === 200 && o2.overlayOpen === false);
  const sd = await api('POST', '/shutdown');
  chk('POST /shutdown ok', sd.ok === true);
  await sleep(2500);
  chk('процесс гарсан (code 0)', exitCode !== null, 'exit=' + exitCode);
  if (exitCode === null) { try { proc.kill(); } catch {} }
  if (fs.existsSync(backup)) { fs.copyFileSync(backup, profile); fs.unlinkSync(backup); console.log('profile.json сэргээв'); }
  console.log(`\n=== embedded API: ${pass} PASS, ${fail} FAIL ===`);
  process.exit(fail ? 1 : 0);
})();
