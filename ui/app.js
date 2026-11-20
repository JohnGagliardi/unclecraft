'use strict';
const { pixelText, renderPixelLabels, avatarUrl, startLandscape, Sound, Nav } = window.PixelKit;
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];

/* ================= bridge to the C# host (or demo data for offscreen previews) ================= */
// Windows: WebView2 (window.chrome.webview, objects). macOS: Photino/WKWebView (window.external, JSON strings).
const host = window.chrome && window.chrome.webview
  ? { send: (o) => window.chrome.webview.postMessage(o), listen: (fn) => window.chrome.webview.addEventListener('message', (e) => fn(e.data)) }
  : window.external && typeof window.external.sendMessage === 'function'
    ? { send: (o) => window.external.sendMessage(JSON.stringify(o)), listen: (fn) => window.external.receiveMessage((s) => fn(typeof s === 'string' ? JSON.parse(s) : s)) }
    : null;
const demo = new URLSearchParams(location.search).get('demo');
let nextId = 1; const pending = new Map(); const listeners = [];
function call(type, data = {}) {
  if (!host) return Demo.call(type, data);
  const id = nextId++;
  return new Promise((resolve, reject) => { pending.set(id, { resolve, reject }); host.send({ id, type, ...data }); });
}
function onEvent(fn) { listeners.push(fn); }
if (host) host.listen((m) => {
  if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.ok ? p.resolve(m.data) : p.reject(m.error || { code: 'error', message: 'Something went wrong.' }); return; }
  if (m.event) listeners.forEach((fn) => fn(m));
});

/* ================= state ================= */
const S = { settings: {}, saved: [], mode: 1, slots: [null, null], status: null, busy: false, players: [], countdown: 0 };

/* ================= friendly errors ================= */
const ERRORS = {
  unreachable: ["CAN'T REACH THE HOST", "Make sure the host's PC is on and the server is running. At the host's house use their home IP; from elsewhere use the internet address the host gave you."],
  timeout: ['THE HOST IS SLOW', 'The server took too long to answer. Try again in a moment.'],
  bad_fingerprint: ['SECURITY CHECK FAILED', "The security code doesn't match this server. Open Settings and re-enter it (ask the host)."],
  bad_credentials: ['WRONG NAME OR PASSWORD', 'Check the spelling, or ask the host to reset your password.'],
  device_token_invalid: ['PLEASE LOG IN AGAIN', 'Your saved login expired or the host removed it. Enter your password once.'],
  no_free_account: ['ALL ACCOUNTS ARE BUSY', 'Everyone is playing! Someone needs to quit first. The server panel shows who is on.'],
  app_not_approved: ['HOST SETUP NOT FINISHED', "The host's Microsoft app isn't approved by Mojang yet. Nothing to fix on your PC."],
  test_mode_local_only: ['TEST MODE IS ON', "Right now only the host's own PC can play. Ask the host to turn test mode off."],
  test_mode_off: ['TEST ACCOUNT', 'Test mode is off on the host, so this test account cannot play.'],
  too_many_attempts: ['SLOW DOWN', 'Too many wrong passwords. Wait 30 seconds and try again.'],
  no_session: ['SESSION ENDED', 'Your session ended. Try again.'],
  token_failed: ["HOST COULDN'T SIGN IN", 'The server could not sign in to Minecraft for you. Tell the host.'],
  invite_invalid: ["INVITE CODE DIDN'T WORK", "It's wrong, already used or expired. Ask the host for a new one (node broker.js invite <your name>)."],
  password_too_short: ['PASSWORD TOO SHORT', 'Your new password needs at least 10 characters, or leave it empty. Your invite code still works.'],
  device_limit:['TOO MANY PCS', 'You already have 3 PCs remembered. Ask the host to remove an old one (node broker.js forget-device), then try the same code again.'],
  home_network_only: ['USE YOUR INVITE CODE', 'From outside the host\'s home, passwords are switched off. Ask the host for an invite code, or log in once at the host\'s home.'],
  wrong_pc:['LOG IN ON THIS PC', 'Your login was made on a different PC. Click your name again here to log in on this PC.'],
  java_failed:["GAME DIDN'T START", 'Java could not start Minecraft. Try again; if it keeps happening tell the host.'],
};
function friendly(err) {
  const [title, hint] = ERRORS[err?.code] || ['SOMETHING WENT WRONG', 'Try again. If it keeps happening, tell the host.'];
  return { title, hint, message: err?.message || '' };
}

/* ================= helpers ================= */
function toast(text, bad = false) {
  const t = $('#toast'); t.textContent = text; t.classList.toggle('bad', bad); t.classList.add('show');
  clearTimeout(toast.h); toast.h = setTimeout(() => t.classList.remove('show'), 3200);
}
function ago(iso) {
  if (!iso) return 'Not played yet';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 90) return 'Played just now';
  if (s < 3600) return `Played ${Math.round(s / 60)} min ago`;
  if (s < 86400) return `Played ${Math.round(s / 3600)} h ago`;
  const d = Math.round(s / 86400); return d === 1 ? 'Played yesterday' : `Played ${d} days ago`;
}
function mb(b) { return b >= 1e9 ? (b / 1e9).toFixed(2) + ' GB' : (b / 1e6).toFixed(b < 1e7 ? 1 : 0) + ' MB'; }
function el(tag, props = {}, ...kids) { const e = document.createElement(tag); Object.assign(e, props); kids.flat().forEach((k) => e.append(k)); return e; }
function openModal(m) { m.classList.add('open'); setTimeout(() => (m.querySelector('[autofocus], input, [data-nav]'))?.focus(), 30); }
function closeModal(m) { m.classList.remove('open'); }
function sameName(a, b) { return a && b && a.toLowerCase() === b.toLowerCase(); }

/* ================= icons (pixel art) ================= */
function iconSvg(rows, color) {
  const ns = 'http://www.w3.org/2000/svg'; const s = document.createElementNS(ns, 'svg');
  s.setAttribute('viewBox', `0 0 ${rows[0].length} ${rows.length}`); s.setAttribute('width', 24); s.setAttribute('height', 24); s.setAttribute('shape-rendering', 'crispEdges');
  let d = ''; rows.forEach((r, y) => [...r].forEach((c, x) => { if (c === '#') d += `M${x} ${y}h1v1h-1z`; }));
  const p = document.createElementNS(ns, 'path'); p.setAttribute('d', d); p.setAttribute('fill', color); s.append(p); return s;
}
const ICON_SOUND = ['........', '...#..#.', '..##...#', '####.#.#', '####.#.#', '..##...#', '...#..#.', '........'];
const ICON_MUTE = ['........', '...#....', '..##.#.#', '####..#.', '####..#.', '..##.#.#', '...#....', '........'];
const ICON_GEAR = ['...##...', '.#.##.#.', '..####..', '###..###', '###..###', '..####..', '.#.##.#.', '...##...'];

/* ================= hero ================= */
function setupHero() {
  $('#wordmark').append(pixelText('FAMILY SERVER', { scale: 7, outline: '#1b2a4a', shadow: '#16223f', gradient: ['#fff3a3', '#ffe066', '#ffd60a', '#ffb703', '#ff9f1c', '#fb8500', '#f77f00'] }));
  startLandscape($('#sky'), { px: 4, speed: 1 });
  $('#settingsBtn').append(iconSvg(ICON_GEAR, '#ffd60a'));
  $('#settingsBtn').onclick = () => { Sound.click(); openSetup(true); };
  $('#soundBtn').onclick = async () => { Sound.on = !Sound.on; renderSoundBtn(); Sound.click(); await call('setSound', { on: Sound.on }).catch(() => {}); };
}
function renderSoundBtn() { $('#soundBtn').replaceChildren(iconSvg(Sound.on ? ICON_SOUND : ICON_MUTE, Sound.on ? '#8ae234' : '#a9b6d8')); }

/* ================= player cards ================= */
function renderCards() {
  const box = $('#cards'); box.replaceChildren();
  for (const p of S.saved) {
    const slotIdx = S.slots.findIndex((c) => c && sameName(c.person, p.person));
    const card = el('button', { className: 'card' + (S.busy ? ' busy' : '') + (slotIdx >= 0 ? ' slotted' : '') });
    card.dataset.nav = ''; card.setAttribute('role', 'listitem');
    card.setAttribute('aria-label', `Play as ${p.person}`);
    if (slotIdx >= 0) card.dataset.slotname = slotIdx === 0 ? 'LEFT' : 'RIGHT';
    card.append(
      el('span', { className: 'play-chip', textContent: S.mode === 2 ? 'PICK' : 'PLAY' }),
      el('img', { className: 'avatar', src: avatarUrl(p.person), alt: '' }),
      el('span', { className: 'name', textContent: p.person }),
      el('span', { className: 'when', textContent: ago(p.lastPlayed) }),
    );
    if (p.error) card.append(el('span', { className: 'err', textContent: p.error }));
    const forget = el('span', { className: 'forget', textContent: '×', title: `Forget ${p.person} on this PC`, role: 'button', tabIndex: 0 });
    forget.dataset.nav = '';
    forget.onclick = (e) => { e.stopPropagation(); confirmForget(p.person); };
    forget.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); confirmForget(p.person); } };
    card.append(forget);
    card.onmouseenter = () => Sound.hover();
    card.onclick = () => choose({ kind: 'saved', person: p.person });
    box.append(card);
  }
  const add = el('button', { className: 'card new' + (S.busy ? ' busy' : '') });
  add.dataset.nav = ''; add.setAttribute('aria-label', 'New player: log in with name and password');
  add.append(el('span', { className: 'plus', textContent: '+' }), el('span', { className: 'name', textContent: 'New player' }), el('span', { className: 'when', textContent: 'Password or invite code' }));
  add.onclick = () => { Sound.click(); openLogin(); };
  box.append(add);
  if (!S.saved.length) box.prepend(el('div', { className: 'empty-hint', textContent: 'No saved players on this PC yet. Log in once and you\'ll get a one-click card.' }));
}

async function confirmForget(person) {
  Sound.click();
  const m = $('#confirmModal');
  $('#confirmTitle').textContent = `Forget ${person}?`;
  $('#confirmText').textContent = `Removes ${person}'s card from this PC and signs it out on the host. They can log in again with their password.`;
  openModal(m);
  $('#confirmNo').onclick = () => { Sound.back(); closeModal(m); };
  $('#confirmYes').onclick = async () => {
    closeModal(m);
    S.slots = S.slots.map((c) => (c && sameName(c.person, person) ? null : c));
    try { const r = await call('forget', { person }); S.saved = r.saved; } catch { /* ignore */ }
    renderCards(); renderSlots(); toast(`Forgot ${person} on this PC.`);
  };
}

/* ================= one click / split-screen slots ================= */
function setMode(mode) {
  S.mode = mode; S.slots = [null, null]; cancelCountdown();
  $$('.mode-toggle button[data-mode]').forEach((b) => b.setAttribute('aria-checked', String(Number(b.dataset.mode) === mode)));
  $('#splitPicker').hidden = mode !== 2;
  renderSlots(); renderCards();
  call('setMode', { split: mode === 2 }).catch(() => {});
}
function choose(choice) {
  if (S.busy) return;
  S.saved.forEach((p) => { p.error = null; });
  if (S.mode === 1) { Sound.click(); return play([choice]); }
  // split-screen: fill the next empty screen
  const i = S.slots.findIndex((c) => c && sameName(c.person, choice.person));
  if (i >= 0) { S.slots[i] = null; Sound.back(); cancelCountdown(); renderSlots(); renderCards(); return; }
  const free = S.slots.indexOf(null);
  if (free < 0) { toast('Both screens are full. Click a screen to empty it.'); return; }
  S.slots[free] = choice; Sound.pick();
  renderSlots(); renderCards();
}
function renderSlots() {
  $$('.slot').forEach((slot) => {
    const i = Number(slot.dataset.slot); const c = S.slots[i]; const body = slot.querySelector('.slot-body');
    slot.classList.toggle('filled', !!c);
    if (c) {
      const wrap = el('div', {}, el('img', { src: avatarUrl(c.person), alt: '' }), el('div', { className: 'who', textContent: c.person }));
      wrap.style.textAlign = 'center'; body.replaceChildren(wrap);
      slot.setAttribute('aria-label', `${i ? 'Right' : 'Left'} screen: ${c.person}. Click to empty.`);
    } else {
      body.replaceChildren(el('span', { textContent: 'Click a player' }));
      slot.setAttribute('aria-label', `${i ? 'Right' : 'Left'} screen: empty`);
    }
  });
  const full = S.slots.every(Boolean);
  const btn = $('#startSplit');
  btn.hidden = !full;
  $('#splitHint').textContent = full ? 'Starting in a moment. Click a screen to change it.' : 'Click a player for each screen. Click a filled screen to empty it.';
  if (full && !S.countdown) startCountdown(); else if (!full) cancelCountdown();
}
function startCountdown() {
  let n = 3; S.countdown = 1;
  const btn = $('#startSplit');
  const tick = () => {
    if (!S.countdown) return;
    btn.replaceChildren(pixelText(n > 0 ? `START SPLIT-SCREEN ${n}` : 'GO!', { scale: 3, color: '#0b1226' }));
    if (n > 0) { Sound.tick(); n--; S.countdown = setTimeout(tick, 900); } else { S.countdown = 0; launchSplit(); }
  };
  btn.onclick = () => { cancelCountdown(); launchSplit(); };
  tick(); btn.focus();
}
function cancelCountdown() { if (S.countdown) clearTimeout(S.countdown); S.countdown = 0; }
function launchSplit() { if (S.slots.every(Boolean)) { const players = [...S.slots]; play(players); } }

/* ================= new player login: name + password, or an invite code ================= */
let loginTab = 'password';
function inviteNormalize(s) { const c = (s.match(/[0-9a-z]/gi) || []).join('').toUpperCase(); return c.length === 16 ? c : null; }
function setLoginTab(tab, why = '') {
  loginTab = tab;
  $$('.login-tabs button').forEach((b) => b.setAttribute('aria-selected', String(b.dataset.tab === tab)));
  $('#pwFields').hidden = tab !== 'password';
  $('#inviteFields').hidden = tab !== 'invite';
  $('#loginHint').textContent = why || (tab === 'invite'
    ? 'Got a code from the host? Enter it once; after that you just click your card.'
    : 'Log in with the name and password the host gave you.');
  setTimeout(() => (tab === 'invite' ? $('#inviteCode') : $('#loginName').value ? $('#loginPass') : $('#loginName')).focus(), 40);
}
function openLogin(name = '', why = '', tab = 'password') {
  $('#loginName').value = name || ''; $('#loginPass').value = ''; $('#inviteCode').value = ''; $('#invitePass').value = ''; $('#inviteCode').oninput(); $('#invitePass').oninput();
  $('#loginGo').textContent = S.mode === 2 ? 'ADD TO SCREEN' : 'LOG IN & PLAY';
  openModal($('#loginModal'));
  setLoginTab(tab, why);
}
$$('.login-tabs button').forEach((b) => (b.onclick = () => { Sound.click(); setLoginTab(b.dataset.tab); }));
$('#inviteCode').oninput = () => {
  const el = $('#inviteCode');
  const raw = (el.value.match(/[0-9a-z]/gi) || []).join('').toUpperCase().slice(0, 16);
  const pretty = raw.match(/.{1,4}/g)?.join('-') || '';
  if (el.value !== pretty) el.value = pretty; // auto-format XXXX-XXXX-XXXX-XXXX as they type or paste
  const c = $('#inviteCheck');
  if (!raw) { c.className = 'small muted'; c.innerHTML = 'The host makes one with <code>node broker.js invite &lt;your name&gt;</code>. It works once and remembers this PC.'; return; }
  c.className = 'small ' + (raw.length === 16 ? 'fp-ok' : 'muted');
  c.textContent = raw.length === 16 ? '✔ Looks right.' : `${raw.length} of 16 characters`;
};
$('#invitePass').oninput = () => {
  const n = $('#invitePass').value.length; const c = $('#invitePassCheck');
  if (!n) { c.className = 'small muted'; c.textContent = 'Lets you log in on another computer later. Only you will know it; the host never sees it.'; return; }
  c.className = 'small ' + (n >= 10 ? 'fp-ok' : 'fp-bad');
  c.textContent = n >= 10 ? '✔ Long enough.' : `${n} of at least 10 characters`;
};
$('#loginForm').onsubmit = (e) => {
  e.preventDefault();
  if (loginTab === 'invite') {
    const code = inviteNormalize($('#inviteCode').value);
    if (!code) { toast('An invite code has 16 letters and numbers.', true); Sound.error(); return; }
    const newPassword = $('#invitePass').value;
    if (newPassword && newPassword.length < 10) { toast('Your new password needs at least 10 characters (or leave it empty).', true); Sound.error(); return; }
    closeModal($('#loginModal'));
    $('#invitePass').value = '';
    choose({ kind: 'invite', code, person: `Invite ${code.slice(0, 4)}`, ...(newPassword ? { newPassword } : {}) });
    return;
  }
  const person = $('#loginName').value.trim(); const password = $('#loginPass').value;
  if (!person || !password) { toast('Enter your name and password.', true); return; }
  closeModal($('#loginModal'));
  choose({ kind: 'password', person, password, remember: $('#loginRemember').checked });
  $('#loginPass').value = '';
};

/* ================= server panel ================= */
async function pollStatus() {
  try { S.status = await call('status'); } catch (e) { S.status = { ok: false, error: e }; }
  renderServer();
}
function renderServer() {
  const st = S.status; const light = $('#srvLight');
  $('#srvAddr').textContent = S.settings.server ? `Host: ${S.settings.server}` : '';
  if (!st || !st.ok) {
    light.className = 'light off';
    const f = friendly(st?.error);
    $('#srvState').textContent = S.settings.server ? f.title.charAt(0) + f.title.slice(1).toLowerCase() : 'Not set up yet';
    $('#accMeter').replaceChildren(); $('#accText').textContent = S.settings.server ? f.hint : 'Open settings to enter the host.';
    $('#playing').replaceChildren(el('li', { className: 'none', textContent: '-' }));
    $('#testBanner').hidden = true;
    $('#modeChip').hidden = true;
    return;
  }
  light.className = 'light on';
  $('#srvState').textContent = st.testMode ? 'Online (test mode)' : 'Online';
  $('#testBanner').hidden = !st.testMode;
  $('#modeChip').hidden = st.mode !== 'interim'; // small label, not the test-mode banner
  const meter = $('#accMeter'); meter.replaceChildren();
  for (let i = 0; i < st.accounts.total; i++) meter.append(el('i', { className: i < st.accounts.free ? 'free' : 'busy' }));
  $('#accText').textContent = st.accounts.total ? `${st.accounts.free} of ${st.accounts.total} free` : 'No accounts set up on the host yet.';
  const ul = $('#playing'); ul.replaceChildren();
  if (!st.playing.length) ul.append(el('li', { className: 'none', textContent: 'Nobody yet. Be the first!' }));
  for (const p of st.playing) {
    ul.append(el('li', {},
      el('img', { src: avatarUrl(p.person, 6), alt: '' }),
      el('div', {}, el('div', { className: 'p-name', textContent: p.person }),
        el('div', { className: 'p-acc', textContent: `as ${p.account} ` }, el('span', { className: 'p-state ' + p.state, textContent: p.state === 'online' ? '● IN GAME' : '● STARTING' })))));
  }
}

/* ================= first-run wizard ================= */
let wzStep = 0;
function openSetup(fromSettings = false) {
  $('#wzServer').value = S.settings.server || '';
  $('#wzFingerprint').value = S.settings.fingerprint || '';
  showStep(fromSettings ? 1 : 0);
  $('#setup').classList.add('open');
  if (!openSetup.sky) openSetup.sky = startLandscape($('#setup .overlay-sky'), { px: 5, speed: .6, palette: 'dusk' });
}
function showStep(n) {
  wzStep = n;
  $$('#setup .step').forEach((s) => s.classList.toggle('active', Number(s.dataset.step) === n));
  $$('#setup .steps i').forEach((d, i) => d.classList.toggle('done', i <= n));
  setTimeout(() => $('#setup .step.active input, #setup .step.active textarea, #setup .step.active .btn.go')?.focus(), 40);
}
function fpNormalize(s) { const hex = (s.match(/[0-9a-f]/gi) || []).join('').toUpperCase(); return hex.length === 64 ? hex.match(/../g).join(':') : null; }
$('#wzFingerprint').oninput = () => {
  const ok = fpNormalize($('#wzFingerprint').value);
  const c = $('#fpCheck');
  if (!$('#wzFingerprint').value.trim()) { c.className = 'small muted'; c.innerHTML = 'The host gets it with <code>node broker.js fingerprint</code>.'; return; }
  c.className = 'small ' + (ok ? 'fp-ok' : 'fp-bad');
  c.textContent = ok ? '✔ Looks right (32 pairs).' : `${(($('#wzFingerprint').value.match(/[0-9a-f]/gi) || []).length / 2) | 0} of 32 pairs so far.`;
};
$$('#setup [data-next]').forEach((b) => (b.onclick = async () => {
  Sound.click();
  if (wzStep === 1 && !$('#wzServer').value.trim()) { toast('Enter the host\'s address first.', true); return; }
  if (wzStep === 2) return wizardConnect();
  showStep(wzStep + 1);
}));
$$('#setup [data-back]').forEach((b) => (b.onclick = () => { Sound.back(); showStep(Math.max(wzStep === 3 ? 2 : 0, wzStep - 1)); }));
async function wizardConnect() {
  const server = $('#wzServer').value.trim(); const fp = fpNormalize($('#wzFingerprint').value);
  if (!fp) { toast('The security code should be 32 pairs like 12:34:...', true); Sound.error(); return; }
  $('#wzTest').disabled = true; $('#wzTest').textContent = 'CONNECTING…';
  try {
    const r = await call('setup', { server, fingerprint: fp });
    S.settings = r.settings; S.saved = r.saved;
    $('#wzResultTitle').replaceChildren(pixelText('CONNECTED!', { scale: 5, color: '#8ae234', shadow: '#0b1226' }));
    $('#wzResultText').textContent = r.testMode ? 'The host is in TEST MODE right now: only the host\'s own PC can play.' : `You're all set. ${r.free} of ${r.total} accounts are free right now.`;
    $('#wzRetry').hidden = true; $('#wzDone').hidden = false;
    Sound.success();
  } catch (e) {
    const f = friendly(e);
    $('#wzResultTitle').replaceChildren(pixelText(f.title, { scale: 3, color: '#ff9aa5', shadow: '#0b1226' }));
    $('#wzResultText').textContent = `${f.hint}${e.message ? `  (${e.message})` : ''}`;
    $('#wzRetry').hidden = false; $('#wzDone').hidden = true;
    Sound.error();
  } finally { $('#wzTest').disabled = false; $('#wzTest').textContent = 'CONNECT'; }
  showStep(3);
}
$('#wzDone').onclick = () => { Sound.click(); $('#setup').classList.remove('open'); renderCards(); pollStatus(); };

/* ================= launch screen ================= */
const STAGES = [['check', 'CHECK'], ['Java', 'JAVA'], ['Minecraft', 'GAME'], ['Fabric', 'FABRIC'], ['Mods', 'MODS'], ['login', 'SIGN IN'], ['start', 'START']];
let speed = { t: 0, b: 0, rate: 0, stage: '' };
let hopperAnim = null;
function openLaunch(players) {
  S.players = players.map((p) => p.person);
  const o = $('#launch'); o.classList.add('open');
  if (!openLaunch.sky) openLaunch.sky = startLandscape($('#launch .overlay-sky'), { px: 5, speed: 1.4 });
  $('#launchError').hidden = true;
  $('#stageList').replaceChildren(...STAGES.map(([k, label]) => { const li = el('li', { textContent: label }); li.dataset.k = k; return li; }));
  setTitle(players.length > 1 ? 'GET READY, BOTH!' : 'GETTING READY');
  $('#launchSub').textContent = players.length > 1 ? `${players[0].person} on the left, ${players[1].person} on the right` : `Playing as ${players[0].person}`;
  setStage('check'); setBar(null, '');
  hopperAnim?.stop(); hopperAnim = startHopper($('#hopper'), S.players);
}
function setTitle(t, color = '#ffffff') { $('#launchTitle').replaceChildren(pixelText(t, { scale: 5, color, shadow: '#0b1226' })); }
function setStage(k, state = 'active') {
  let passed = true;
  for (const li of $$('#stageList li')) {
    if (li.dataset.k === k) { li.className = state; passed = false; }
    else if (passed && state !== 'fail') li.className = li.className === 'fail' ? 'fail' : 'done';
  }
}
function setBar(frac, text) {
  const f = $('#barFill');
  if (frac == null) { f.classList.add('indeterminate'); } else { f.classList.remove('indeterminate'); f.style.width = `${Math.max(2, Math.min(100, frac * 100))}%`; }
  $('#barText').textContent = text;
}
function onProgress(p) {
  setStage(p.stage, p.done ? 'done' : 'active');
  if (p.done) { setBar(1, `${p.stage} ready`); return; }
  const now = performance.now();
  if (speed.stage !== p.stage) speed = { t: now, b: p.doneBytes, rate: 0, stage: p.stage };
  const dt = (now - speed.t) / 1000;
  if (dt > 0.5) { const r = (p.doneBytes - speed.b) / dt; speed.rate = speed.rate ? speed.rate * 0.7 + r * 0.3 : r; speed.t = now; speed.b = p.doneBytes; }
  const label = { Java: 'Java runtime', Minecraft: 'Minecraft', Fabric: 'Fabric loader', Mods: "Host's mods" }[p.stage] || p.stage;
  const total = Math.max(p.totalBytes, p.doneBytes);
  setBar(total ? p.doneBytes / total : p.doneFiles / Math.max(1, p.totalFiles),
    `${label}: ${mb(p.doneBytes)} of ${mb(total)}${speed.rate > 0 ? `  ·  ${mb(speed.rate)}/s` : ''}  ·  ${p.doneFiles}/${p.totalFiles} files`);
}
function launchError(err) {
  const f = friendly(err);
  hopperAnim?.sad();
  setTitle('UH-OH', '#ff9aa5');
  $('#launchSub').textContent = '';
  $$('#stageList li.active').forEach((li) => (li.className = 'fail'));
  setBar(0, '');
  $('#errTitle').replaceChildren(pixelText(f.title, { scale: 3, color: '#ffd1d6' }));
  $('#errText').textContent = f.message && f.message !== f.title ? f.message : '';
  $('#errHint').textContent = f.hint;
  const act = $('#errAction');
  act.hidden = true;
  if (err?.code === 'home_network_only' || err?.code === 'invite_invalid' || err?.code === 'device_limit' || err?.code === 'password_too_short') {
    act.hidden = false; act.textContent = err.code === 'invite_invalid' ? 'TRY ANOTHER CODE' : 'USE INVITE CODE';
    act.onclick = () => { closeLaunch(); openLogin('', f.hint, 'invite'); };
  } else if (err?.code === 'device_token_invalid' || err?.code === 'bad_credentials') {
    act.hidden = false; act.textContent = 'LOG IN AGAIN';
    act.onclick = () => { closeLaunch(); openLogin(err.person && !String(err.person).startsWith('Invite ') ? err.person : '', f.hint); };
  } else if (err?.code === 'unreachable' || err?.code === 'bad_fingerprint') {
    act.hidden = false; act.textContent = 'SETTINGS';
    act.onclick = () => { closeLaunch(); openSetup(true); };
  }
  $('#launchError').hidden = false;
  $('#errBack').onclick = () => { Sound.back(); closeLaunch(); };
  setTimeout(() => (act.hidden ? $('#errBack') : act).focus(), 50);
  Sound.error();
}
function closeLaunch() { $('#launch').classList.remove('open'); hopperAnim?.stop(); hopperAnim = null; }

/** Little scene: the players' avatars hop along scrolling blocks; booting = faster, done = jump for joy. */
function startHopper(canvas, names) {
  const ctx = canvas.getContext('2d'); let raf = 0; let mode = 'run'; const t0 = performance.now();
  const imgs = names.map((n) => { const i = new Image(); i.src = avatarUrl(n, 2); return i; });
  const colors = ['#8ae234', '#5cbf3a', '#8a5a32', '#ff9f1c', '#4cc9f0', '#9b5de5', '#f15bb5'];
  function frame(now) {
    const r = canvas.getBoundingClientRect(); const W = Math.ceil(r.width / 3), H = Math.ceil(r.height / 3);
    if (canvas.width !== W) { canvas.width = W; canvas.height = H; }
    const t = (now - t0) / 1000; const sp = mode === 'boot' ? 70 : mode === 'sad' ? 0 : 30;
    ctx.clearRect(0, 0, W, H);
    const ground = H - 10; const off = (t * sp) % 8;
    for (let x = -8; x < W + 8; x += 8) {
      const col = Math.floor((x + t * sp) / 8);
      ctx.fillStyle = colors[Math.abs(col * 7) % 3]; ctx.fillRect(Math.floor(x - off), ground, 8, 2);
      ctx.fillStyle = '#8a5a32'; ctx.fillRect(Math.floor(x - off), ground + 2, 8, 8);
      ctx.fillStyle = 'rgba(0,0,0,.15)'; ctx.fillRect(Math.floor(x - off) + 7, ground + 2, 1, 8);
      if (Math.abs(col * 13) % 11 === 0) { ctx.fillStyle = colors[3 + (Math.abs(col) % 4)]; ctx.fillRect(Math.floor(x - off), ground - 8, 8, 8); ctx.fillStyle = 'rgba(255,255,255,.25)'; ctx.fillRect(Math.floor(x - off), ground - 8, 8, 1); }
    }
    imgs.forEach((img, i) => {
      const x = Math.floor(W / 2 - (imgs.length - 1) * 16 + i * 32 - 12);
      const phase = t * (mode === 'boot' ? 9 : mode === 'yay' ? 7 : 6) + i * 1.3;
      const jump = mode === 'sad' ? 0 : Math.abs(Math.sin(phase)) * Math.min(mode === 'yay' ? 16 : 10, ground - 26);
      const y = ground - 24 - Math.floor(jump);
      if (img.complete) ctx.drawImage(img, x, y, 24, 24);
      ctx.fillStyle = 'rgba(0,0,0,.25)'; ctx.fillRect(x + 4, ground - 1, 16 - Math.floor(jump / 3), 1);
    });
    raf = requestAnimationFrame(frame);
  }
  raf = requestAnimationFrame(frame);
  return { stop() { cancelAnimationFrame(raf); }, boot() { mode = 'boot'; }, yay() { mode = 'yay'; }, sad() { mode = 'sad'; } };
}

/* ================= play ================= */
async function play(players) {
  if (S.busy) return;
  S.busy = true; renderCards();
  Sound.launch();
  openLaunch(players);
  try {
    await call('play', { players });
  } catch (err) {
    const person = err.person;
    if (person && err.code !== 'device_token_invalid') { const p = S.saved.find((x) => sameName(x.person, person)); if (p) p.error = friendly(err).title.toLowerCase().replace(/^./, (c) => c.toUpperCase()); }
    launchError(err);
  } finally {
    S.busy = false; S.slots = [null, null]; renderSlots(); renderCards(); pollStatus();
  }
}
onEvent((m) => {
  switch (m.event) {
    case 'progress': onProgress(m); break;
    case 'status': $('#launchSub').textContent = m.text; break;
    case 'phase':
      if (m.phase === 'login') { setStage('login'); setBar(null, `Signing in ${m.person}…`); }
      if (m.phase === 'starting') { setStage('start'); setTitle('STARTING!'); setBar(null, 'Starting Java…'); }
      if (m.phase === 'booting') { setStage('start'); setTitle('BOOTING MINECRAFT'); hopperAnim?.boot(); setBar(null, 'The game window will open in a moment…'); }
      if (m.phase === 'ingame') {
        setStage('done-all'); $$('#stageList li').forEach((li) => (li.className = 'done'));
        setTitle("YOU'RE IN!", '#8ae234'); hopperAnim?.yay(); setBar(1, 'Have fun! You can keep this window open or minimize it.');
        Sound.success(); setTimeout(closeLaunch, 3500);
      }
      break;
    case 'saved': S.saved = m.saved; renderCards(); break;
    case 'closed': toast(`${m.person}'s game closed. ${m.account} is free again.`); pollStatus(); break;
  }
});

/* ================= demo data (offscreen previews only) ================= */
const Demo = {
  saved: [
    { person: 'Test1', lastPlayed: new Date(Date.now() - 2 * 3600e3).toISOString() },
    { person: 'Alice', lastPlayed: new Date(Date.now() - 86400e3).toISOString() },
    { person: 'Ben', lastPlayed: new Date(Date.now() - 5 * 60e3).toISOString() },
    { person: 'Grandpa Joe', lastPlayed: null },
    { person: 'Maya', lastPlayed: new Date(Date.now() - 4 * 86400e3).toISOString() },
  ],
  async call(type) {
    if (type === 'init') return { configured: demo !== 'setup', settings: { server: '192.168.1.20', fingerprint: '12:34:56', soundOn: true, split: demo === 'split' }, saved: this.saved };
    if (type === 'status') return { ok: true, testMode: demo === 'test', mode: demo === 'test' ? 'test' : demo === 'interim' ? 'interim' : 'approved', accounts: { total: 6, free: 4 }, playing: [{ person: 'Ben', account: 'PoolAccount3', state: 'online' }, { person: 'Uncle', account: 'PoolAccount1', state: 'reserved' }] };
    return {};
  },
};

/* ================= boot ================= */
async function boot() {
  if (demo) document.body.classList.add('shot'); // offscreen previews: skip entry animations
  renderPixelLabels();
  setupHero();
  // only the 1/2-player toggle: the login tabs share the .mode-toggle look but have their own handler
  $$('.mode-toggle button[data-mode]').forEach((b) => (b.onclick = () => { Sound.click(); setMode(Number(b.dataset.mode)); }));
  $$('.slot').forEach((s) => (s.onclick = () => { const c = S.slots[Number(s.dataset.slot)]; if (c) choose(c); }));
  $$('[data-close]').forEach((b) => (b.onclick = () => { Sound.back(); closeModal(b.closest('.modal')); }));
  Nav.start(() => {
    const m = $('.modal.open'); if (m) { closeModal(m); Sound.back(); return; }
    if ($('#setup').classList.contains('open') && S.settings.server) { $('#setup').classList.remove('open'); return; }
    if (!$('#launchError').hidden) closeLaunch();
  });
  const r = await call('init');
  S.settings = r.settings; S.saved = r.saved; Sound.on = r.settings.soundOn !== false;
  renderSoundBtn();
  setMode(r.settings.split ? 2 : 1);
  if (!r.configured) openSetup(false);
  await pollStatus();
  setInterval(() => { if (!document.hidden && !$('#setup').classList.contains('open')) pollStatus(); }, 5000);
  setInterval(() => renderCards.pending || (!S.busy && renderCards()), 60000); // refresh "played x ago"
  // demo screens for offscreen screenshots
  if (demo === 'split') { S.slots = [{ kind: 'saved', person: 'Alice' }, null]; renderSlots(); renderCards(); }
  if (demo === 'split2') { setMode(2); S.slots = [{ kind: 'saved', person: 'Alice' }, { kind: 'saved', person: 'Ben' }]; S.countdown = 1; renderSlots(); renderCards(); $('#startSplit').hidden = false; $('#startSplit').replaceChildren(pixelText('START SPLIT-SCREEN 2', { scale: 3, color: '#0b1226' })); }
  if (demo === 'launch') { openLaunch([{ person: 'Alice' }, { person: 'Ben' }]); setStage('Minecraft'); setBar(0.43, 'Minecraft: 212 MB of 489 MB  ·  18.4 MB/s  ·  1873/4120 files'); }
  if (demo === 'error') { openLaunch([{ person: 'Test1' }]); setStage('login'); launchError({ code: 'no_free_account', message: 'All Minecraft accounts are in use right now.' }); }
  if (demo === 'setup2') { openSetup(true); $('#wzFingerprint').value = '12:34:56:78:9A:BC:DE:F0:12:34:56:78:9A:BC:DE:F0:12:34:56:78:9A:BC:DE:F0:12:34:56:78:9A:BC:DE:F0'; showStep(2); $('#wzFingerprint').oninput(); }
  if (demo === 'login') { openLogin(); }
  if (demo === 'invite') { openLogin('', '', 'invite'); $('#inviteCode').value = '7k2mq9xd4hrt8wza'; $('#inviteCode').oninput(); }
}
boot();
