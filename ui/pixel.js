// UncleCraft pixel kit: original 5x7 font, name-seeded avatars, parallax landscape, synthesized sounds, pad/keyboard nav.
// Everything here is drawn in code; no external art, fonts or sounds.
(() => {
'use strict';

/* ---------------- 5x7 pixel font (hand-drawn) ---------------- */
const GLYPHS = {
  A: '01110 10001 10001 11111 10001 10001 10001', B: '11110 10001 10001 11110 10001 10001 11110',
  C: '01110 10001 10000 10000 10000 10001 01110', D: '11110 10001 10001 10001 10001 10001 11110',
  E: '11111 10000 10000 11110 10000 10000 11111', F: '11111 10000 10000 11110 10000 10000 10000',
  G: '01110 10001 10000 10111 10001 10001 01111', H: '10001 10001 10001 11111 10001 10001 10001',
  I: '01110 00100 00100 00100 00100 00100 01110', J: '00111 00010 00010 00010 00010 10010 01100',
  K: '10001 10010 10100 11000 10100 10010 10001', L: '10000 10000 10000 10000 10000 10000 11111',
  M: '10001 11011 10101 10101 10001 10001 10001', N: '10001 10001 11001 10101 10011 10001 10001',
  O: '01110 10001 10001 10001 10001 10001 01110', P: '11110 10001 10001 11110 10000 10000 10000',
  Q: '01110 10001 10001 10001 10101 10010 01101', R: '11110 10001 10001 11110 10100 10010 10001',
  S: '01111 10000 10000 01110 00001 00001 11110', T: '11111 00100 00100 00100 00100 00100 00100',
  U: '10001 10001 10001 10001 10001 10001 01110', V: '10001 10001 10001 10001 10001 01010 00100',
  W: '10001 10001 10001 10101 10101 10101 01010', X: '10001 10001 01010 00100 01010 10001 10001',
  Y: '10001 10001 01010 00100 00100 00100 00100', Z: '11111 00001 00010 00100 01000 10000 11111',
  0: '01110 10001 10011 10101 11001 10001 01110', 1: '00100 01100 00100 00100 00100 00100 01110',
  2: '01110 10001 00001 00010 00100 01000 11111', 3: '11110 00001 00001 01110 00001 00001 11110',
  4: '00010 00110 01010 10010 11111 00010 00010', 5: '11111 10000 11110 00001 00001 10001 01110',
  6: '00110 01000 10000 11110 10001 10001 01110', 7: '11111 00001 00010 00100 01000 01000 01000',
  8: '01110 10001 10001 01110 10001 10001 01110', 9: '01110 10001 10001 01111 00001 00010 01100',
  ' ': '00000 00000 00000 00000 00000 00000 00000', '-': '00000 00000 00000 11111 00000 00000 00000',
  '.': '00000 00000 00000 00000 00000 01100 01100', '!': '00100 00100 00100 00100 00100 00000 00100',
  '?': '01110 10001 00001 00010 00100 00000 00100', ':': '00000 01100 01100 00000 01100 01100 00000',
  '/': '00001 00010 00010 00100 01000 01000 10000', "'": '00100 00100 01000 00000 00000 00000 00000',
  '(': '00010 00100 01000 01000 01000 00100 00010', ')': '01000 00100 00010 00010 00010 00100 01000',
  '+': '00000 00100 00100 11111 00100 00100 00000', '%': '11001 11010 00010 00100 01000 01011 10011',
  '_': '00000 00000 00000 00000 00000 00000 11111', '&': '01100 10010 10100 01000 10101 10010 01101',
  ',': '00000 00000 00000 00000 01100 00100 01000', '#': '01010 11111 01010 01010 01010 11111 01010',
};
const GLYPH_ROWS = Object.fromEntries(Object.entries(GLYPHS).map(([k, v]) => [k, v.split(' ')]));

/** Returns an <svg> with `text` in the pixel font. scale = CSS px per font pixel. shadow = color of a 1px drop shadow. */
let gradId = 0;
function pixelText(text, { scale = 3, color = 'currentColor', shadow = null, spacing = 1, outline = null, gradient = null } = {}) {
  const chars = [...String(text).toUpperCase()].map((c) => (GLYPH_ROWS[c] ? c : '?'));
  const w = Math.max(1, chars.length * (5 + spacing) - spacing);
  const h = 7;
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  const o = outline ? 1 : 0;              // outline thickness (font pixels)
  const s = shadow ? (outline ? 2 : 1) : 0; // drop shadow offset
  svg.setAttribute('viewBox', `${-o} ${-o} ${w + 2 * o + s} ${h + 2 * o + s}`);
  svg.setAttribute('width', (w + 2 * o + s) * scale);
  svg.setAttribute('height', (h + 2 * o + s) * scale);
  svg.setAttribute('shape-rendering', 'crispEdges');
  svg.setAttribute('aria-label', text);
  svg.setAttribute('role', 'img');
  svg.classList.add('px-text');
  let d = '';
  chars.forEach((c, i) => {
    const ox = i * (5 + spacing);
    GLYPH_ROWS[c].forEach((row, y) => {
      let x = 0;
      while (x < 5) {
        if (row[x] === '1') { let e = x; while (e < 5 && row[e] === '1') e++; d += `M${ox + x} ${y}h${e - x}v1h${x - e}z`; x = e; } else x++;
      }
    });
  });
  const layer = (fill, dx, dy) => { const p = document.createElementNS(ns, 'path'); p.setAttribute('d', d); p.setAttribute('fill', fill); if (dx || dy) p.setAttribute('transform', `translate(${dx} ${dy})`); svg.appendChild(p); };
  const ring = [[-1, 0], [1, 0], [0, -1], [0, 1], [-1, -1], [1, 1], [-1, 1], [1, -1]];
  if (shadow) { if (outline) ring.forEach(([x, y]) => layer(shadow, x + s, y + s)); layer(shadow, s, s); }
  if (outline) ring.forEach(([x, y]) => layer(outline, x, y));
  let fill = color;
  if (gradient) {
    const id = `pxg${++gradId}`;
    const defs = document.createElementNS(ns, 'defs');
    const g = document.createElementNS(ns, 'linearGradient'); g.setAttribute('id', id); g.setAttribute('x1', 0); g.setAttribute('y1', 0); g.setAttribute('x2', 0); g.setAttribute('y2', 1);
    gradient.forEach((c, i) => { // hard stops = pixel bands, no smooth blend
      const a = document.createElementNS(ns, 'stop'); a.setAttribute('offset', i / gradient.length); a.setAttribute('stop-color', c);
      const b = document.createElementNS(ns, 'stop'); b.setAttribute('offset', (i + 1) / gradient.length); b.setAttribute('stop-color', c);
      g.append(a, b);
    });
    defs.append(g); svg.prepend(defs); fill = `url(#${id})`;
  }
  layer(fill, 0, 0);
  return svg;
}
/** Replaces the content of every [data-px] element with pixel text from its attribute. */
function renderPixelLabels(root = document) {
  root.querySelectorAll('[data-px]').forEach((el) => {
    const scale = Number(el.dataset.pxScale || 2);
    el.replaceChildren(pixelText(el.dataset.px, { scale, shadow: el.dataset.pxShadow || null }));
  });
}

/* ---------------- deterministic randomness ---------------- */
function hashString(s) { let h = 2166136261; for (const c of String(s).toLowerCase()) { h ^= c.codePointAt(0); h = Math.imul(h, 16777619); } return h >>> 0; }
function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6d2b79f5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
const pick = (r, arr) => arr[Math.floor(r() * arr.length)];

/* ---------------- avatars: an 8x8 blocky face, unique per name ---------------- */
const SKIN = [['#f6d2b0', '#e2b48e'], ['#e8b88a', '#cf9a6c'], ['#c68c5c', '#a8723f'], ['#9a6440', '#7c4c2c'], ['#6e4429', '#55321d'], ['#f0c8a0', '#d8a77c']];
const HAIR = ['#2b1d14', '#5a3a1e', '#a8652a', '#e3b04b', '#d9482b', '#1f2a44', '#7f3fbf', '#2f9e6e', '#ececec'];
const EYES = ['#2a6fd6', '#2f9e44', '#6b3e1f', '#7b3fd1', '#1b2a4a'];
const BGS = ['#4cc9f0', '#8ae234', '#ff9f1c', '#f15bb5', '#9b5de5', '#ffd60a', '#00bbf9', '#fb5607'];

function avatarPixels(name) {
  const r = rng(hashString(name));
  const [skin, skinDark] = pick(r, SKIN);
  const hair = pick(r, HAIR); const eye = pick(r, EYES); const bg = pick(r, BGS);
  const style = Math.floor(r() * 5); const mouth = Math.floor(r() * 3); const extra = Math.floor(r() * 4);
  const hat = pick(r, BGS.filter((c) => c !== bg));
  const g = Array.from({ length: 8 }, () => Array(8).fill(skin));
  const set = (x, y, c) => { if (x >= 0 && x < 8 && y >= 0 && y < 8) g[y][x] = c; };
  for (let x = 0; x < 8; x++) set(x, 7, skinDark); // chin shade
  // hair styles
  if (style === 0) { for (let x = 0; x < 8; x++) { set(x, 0, hair); set(x, 1, hair); } set(0, 2, hair); set(7, 2, hair); }
  if (style === 1) { for (let x = 0; x < 8; x++) set(x, 0, hair); for (let y = 0; y < 6; y++) { set(0, y, hair); set(7, y, hair); } set(1, 1, hair); set(6, 1, hair); }
  if (style === 2) { for (let x = 0; x < 8; x++) { set(x, 0, hat); set(x, 1, hat); } for (let x = 0; x < 3; x++) set(x + 5, 2, hat); } // cap
  if (style === 3) { set(3, 0, hair); set(4, 0, hair); set(3, 1, hair); set(4, 1, hair); for (let x = 0; x < 8; x++) if (x < 2 || x > 5) set(x, 0, skinDark); } // mohawk
  if (style === 4) { for (let x = 0; x < 8; x++) set(x, 0, hair); for (let x = 1; x < 7; x++) set(x, 1, hair); }
  // eyes
  set(1, 3, '#ffffff'); set(2, 3, eye); set(5, 3, '#ffffff'); set(6, 3, eye);
  // mouth
  if (mouth === 0) { set(2, 5, '#5a2a1e'); set(3, 6, '#5a2a1e'); set(4, 6, '#5a2a1e'); set(5, 5, '#5a2a1e'); }
  if (mouth === 1) { for (let x = 2; x < 6; x++) set(x, 5, '#5a2a1e'); }
  if (mouth === 2) { set(3, 5, '#5a2a1e'); set(4, 5, '#5a2a1e'); set(3, 6, '#c0392b'); set(4, 6, '#c0392b'); }
  // extras: beard, freckles, glasses
  if (extra === 1 && style !== 2) { for (let x = 1; x < 7; x++) set(x, 7, hair); set(1, 6, hair); set(6, 6, hair); }
  if (extra === 2) { set(1, 4, skinDark); set(6, 4, skinDark); }
  if (extra === 3) { for (let x = 0; x < 8; x++) if (x !== 3 && x !== 4) set(x, 3, x === 0 || x === 7 ? '#1b2a4a' : g[3][x]); set(3, 3, '#1b2a4a'); set(4, 3, '#1b2a4a'); set(0, 2, '#1b2a4a'); set(7, 2, '#1b2a4a'); set(2, 2, '#1b2a4a'); set(5, 2, '#1b2a4a'); }
  return { grid: g, bg };
}
const avatarCache = new Map();
/** data: URL of a name's avatar (face on a colored tile with a dark pixel border). */
function avatarUrl(name, px = 12) {
  const key = `${name}|${px}`;
  if (avatarCache.has(key)) return avatarCache.get(key);
  const { grid, bg } = avatarPixels(name);
  const c = document.createElement('canvas');
  c.width = c.height = 12 * px;
  const x = c.getContext('2d');
  x.fillStyle = '#1b2a4a'; x.fillRect(0, 0, 12 * px, 12 * px);
  x.fillStyle = bg; x.fillRect(px, px, 10 * px, 10 * px);
  grid.forEach((row, y) => row.forEach((col, xx) => { x.fillStyle = col; x.fillRect((2 + xx) * px, (2 + y) * px, px, px); }));
  const url = c.toDataURL();
  avatarCache.set(key, url);
  return url;
}

/* ---------------- parallax pixel landscape ---------------- */
function valueNoise(seed) {
  const r = (i) => { const t = Math.sin((i + seed * 101.7) * 127.1) * 43758.5453; return t - Math.floor(t); };
  return (x) => { const i = Math.floor(x); const f = x - i; const s = f * f * (3 - 2 * f); return r(i) * (1 - s) + r(i + 1) * s; };
}
/** Draws a slowly scrolling landscape into `canvas`. Returns { stop() }. Art pixels are `px` CSS pixels big. */
function startLandscape(canvas, { px = 4, speed = 1, palette = 'day' } = {}) {
  const ctx = canvas.getContext('2d');
  const P = palette === 'dusk'
    ? { sky: ['#2d1b4e', '#4a2a6e', '#7a3b7f', '#c4577a', '#f08a5d'], sun: '#ffd27f', far: '#3b2a5c', mid: '#2e5a4a', grass: '#4f9a3a', grassHi: '#7cc04e', dirt: '#6b4a2e', dirtLo: '#553a24', cloud: '#f6c7d0', leaf: '#2f7a3a', trunk: '#4a2e1a' }
    : { sky: ['#4cc9f0', '#62d2f4', '#7fdbff', '#a8e8ff', '#cdf3ff'], sun: '#ffe066', far: '#7aa3d9', mid: '#3f9a5a', grass: '#5cbf3a', grassHi: '#8ae234', dirt: '#8a5a32', dirtLo: '#6e4524', cloud: '#ffffff', leaf: '#2f8f3a', trunk: '#6b4226' };
  const far = valueNoise(1), mid = valueNoise(2), near = valueNoise(3), cl = valueNoise(4);
  let raf = 0; let t0 = performance.now(); let W = 0, H = 0;
  const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
  function resize() {
    const dpr = window.devicePixelRatio || 1;
    const r = canvas.getBoundingClientRect();
    W = Math.max(1, Math.ceil(r.width / px)); H = Math.max(1, Math.ceil(r.height / px));
    canvas.width = W; canvas.height = H; // 1 canvas pixel = 1 art pixel, CSS scales it up crisply
    canvas.style.imageRendering = 'pixelated';
    void dpr;
  }
  function frame(now) {
    const t = reduce ? 0 : (now - t0) / 1000 * speed;
    // sky bands
    const band = Math.ceil(H / P.sky.length);
    P.sky.forEach((c, i) => { ctx.fillStyle = c; ctx.fillRect(0, i * band, W, band); });
    // sun
    ctx.fillStyle = P.sun; const sx = W - 30, sy = 8;
    ctx.fillRect(sx, sy + 2, 12, 8); ctx.fillRect(sx + 2, sy, 8, 12);
    // clouds
    ctx.fillStyle = P.cloud;
    for (let i = 0; i < 6; i++) {
      const cx = ((i * 71 - t * 3) % (W + 60) + W + 60) % (W + 60) - 30;
      const cy = 6 + Math.floor(cl(i * 3.3) * H * 0.3);
      const w = 14 + Math.floor(cl(i * 7.1) * 14);
      ctx.fillRect(Math.floor(cx), cy, w, 3); ctx.fillRect(Math.floor(cx) + 3, cy - 2, w - 7, 2);
    }
    // layers: far mountains, mid hills, near ground with blocks and trees
    const layer = (noise, base, amp, scrollSpeed, color, step) => {
      ctx.fillStyle = color;
      const off = t * scrollSpeed;
      for (let x = 0; x < W; x += step) {
        const h = Math.floor(base + noise((x + off) / 40) * amp);
        ctx.fillRect(x, H - h, step, h);
      }
    };
    layer(far, H * 0.35, H * 0.35, 2, P.far, 3);
    layer(mid, H * 0.22, H * 0.22, 6, P.mid, 2);
    const off = t * 14;
    for (let x = 0; x < W; x++) {
      const wx = Math.floor(x + off);
      const col = Math.floor(wx / 4);
      const h = Math.max(6, Math.floor((H * 0.12 + near(col / 6) * H * 0.14) / 4) * 4); // block-stepped
      const top = H - h;
      ctx.fillStyle = P.dirt; ctx.fillRect(x, top + 2, 1, h - 2);
      if ((wx % 4 === 0) || ((H - top) % 4 === 0)) { ctx.fillStyle = P.dirtLo; ctx.fillRect(x, top + 2, 1, h - 2); }
      ctx.fillStyle = (wx % 4 === 1) ? P.grassHi : P.grass; ctx.fillRect(x, top, 1, 2);
      // trees at deterministic columns
      if (hashString('t' + col) % 9 === 0 && wx % 4 === 1) {
        ctx.fillStyle = P.trunk; ctx.fillRect(x, top - 6, 2, 6);
        ctx.fillStyle = P.leaf; ctx.fillRect(x - 3, top - 12, 8, 6); ctx.fillRect(x - 1, top - 14, 4, 2);
      }
    }
    // dirt texture rows
    ctx.fillStyle = 'rgba(0,0,0,0.08)';
    for (let y = H - 4; y > H * 0.7; y -= 4) ctx.fillRect(0, y, W, 1);
    if (!reduce) raf = requestAnimationFrame(frame);
  }
  resize();
  const ro = new ResizeObserver(resize); ro.observe(canvas);
  raf = requestAnimationFrame(frame);
  return { stop() { cancelAnimationFrame(raf); ro.disconnect(); } };
}

/* ---------------- synthesized UI sounds ---------------- */
const Sound = {
  on: true, ctx: null,
  _ac() { if (!this.ctx) this.ctx = new (window.AudioContext || window.webkitAudioContext)(); return this.ctx; },
  tone(freqs, { dur = 0.07, type = 'square', vol = 0.05, gap = 0.0, slide = 0 } = {}) {
    if (!this.on) return;
    try {
      const ac = this._ac(); let t = ac.currentTime;
      for (const f of freqs) {
        const o = ac.createOscillator(); const g = ac.createGain();
        o.type = type; o.frequency.setValueAtTime(f, t);
        if (slide) o.frequency.exponentialRampToValueAtTime(Math.max(40, f * slide), t + dur);
        g.gain.setValueAtTime(vol, t); g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
        o.connect(g).connect(ac.destination); o.start(t); o.stop(t + dur + 0.02);
        t += dur + gap;
      }
    } catch { /* audio unavailable */ }
  },
  hover() { this.tone([1320], { dur: 0.025, vol: 0.015 }); },
  click() { this.tone([660, 990], { dur: 0.04, vol: 0.04 }); },
  pick() { this.tone([523, 784], { dur: 0.06, vol: 0.05 }); },
  back() { this.tone([660, 440], { dur: 0.05, vol: 0.04 }); },
  success() { this.tone([523, 659, 784, 1047], { dur: 0.08, vol: 0.05 }); },
  error() { this.tone([220, 165], { dur: 0.12, vol: 0.06, type: 'sawtooth' }); },
  launch() { this.tone([220], { dur: 0.5, vol: 0.05, slide: 4, type: 'triangle' }); this.tone([330, 440, 660, 880], { dur: 0.07, vol: 0.04, gap: 0.03 }); },
  tick() { this.tone([880], { dur: 0.05, vol: 0.05 }); },
};

/* ---------------- keyboard + gamepad spatial navigation ---------------- */
const Nav = {
  scope() { const m = document.querySelector('.modal.open, .overlay.open'); return m || document; },
  items() {
    return [...this.scope().querySelectorAll('[data-nav]:not([disabled])')].filter((el) => el.offsetParent !== null);
  },
  move(dir) {
    const items = this.items(); if (!items.length) return;
    const cur = document.activeElement && items.includes(document.activeElement) ? document.activeElement : null;
    if (!cur) { items[0].focus(); return; }
    const a = cur.getBoundingClientRect(); const ax = a.left + a.width / 2, ay = a.top + a.height / 2;
    let best = null, bestScore = Infinity;
    for (const el of items) {
      if (el === cur) continue;
      const b = el.getBoundingClientRect(); const bx = b.left + b.width / 2, by = b.top + b.height / 2;
      const dx = bx - ax, dy = by - ay;
      const ok = dir === 'left' ? dx < -4 : dir === 'right' ? dx > 4 : dir === 'up' ? dy < -4 : dy > 4;
      if (!ok) continue;
      const main = dir === 'left' || dir === 'right' ? Math.abs(dx) : Math.abs(dy);
      const cross = dir === 'left' || dir === 'right' ? Math.abs(dy) : Math.abs(dx);
      const score = main + cross * 2.5;
      if (score < bestScore) { bestScore = score; best = el; }
    }
    if (best) { best.focus(); Sound.hover(); }
  },
  start(onBack) {
    addEventListener('keydown', (e) => {
      const typing = e.target.matches('input, textarea');
      const map = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down' };
      if (map[e.key] && !typing) { e.preventDefault(); this.move(map[e.key]); }
      if (e.key === 'Escape') onBack();
    });
    // Gamepad: d-pad / left stick moves, A clicks, B goes back.
    const prev = {};
    let lastMove = 0;
    const poll = () => {
      if (!document.hidden && document.hasFocus()) {
        for (const gp of navigator.getGamepads ? navigator.getGamepads() : []) {
          if (!gp) continue;
          const b = (i) => gp.buttons[i]?.pressed;
          const now = performance.now();
          const ax = gp.axes[0] || 0, ay = gp.axes[1] || 0;
          const dir = b(14) || ax < -0.6 ? 'left' : b(15) || ax > 0.6 ? 'right' : b(12) || ay < -0.6 ? 'up' : b(13) || ay > 0.6 ? 'down' : null;
          if (dir && now - lastMove > 180) { this.move(dir); lastMove = now; }
          if (b(0) && !prev[gp.index + 'a']) { const el = document.activeElement; if (el && el !== document.body) el.click(); else this.move('down'); }
          if (b(1) && !prev[gp.index + 'b']) onBack();
          prev[gp.index + 'a'] = b(0); prev[gp.index + 'b'] = b(1);
        }
      }
      requestAnimationFrame(poll);
    };
    requestAnimationFrame(poll);
  },
};

window.PixelKit = { pixelText, renderPixelLabels, avatarUrl, startLandscape, Sound, Nav, hashString, rng };
})();
