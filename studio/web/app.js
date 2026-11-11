// LiveRaid Studio - the editor. Draws navmesh patches, no-go boxes, links, climbs, bot walks and extra places over the map's navmesh
// and writes them to the game folder (studio/edits/<map>.json), where the LiveRaid mod reads them.
import { View } from './view.js';
import * as G from './geom.js';

const $ = (s, r = document) => r.querySelector(s);
function h(tag, attrs = {}, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (k === 'class') e.className = v;
    else if (k === 'style') e.style.cssText = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else if (v === true) e.setAttribute(k, '');
    else if (v !== false && v != null) e.setAttribute(k, v);
  }
  for (const c of kids.flat()) if (c != null && c !== false) e.append(c.nodeType ? c : document.createTextNode(c));
  return e;
}
const uid = () => Math.random().toString(36).slice(2, 8);
const rgb = (hex) => [(hex >> 16 & 255) / 255, (hex >> 8 & 255) / 255, (hex & 255) / 255];
const hex = (n) => '#' + n.toString(16).padStart(6, '0');
const r2 = (v) => Math.round(v * 100) / 100;
const clone = (o) => JSON.parse(JSON.stringify(o));
const api = async (path, opts) => {
  const r = await fetch('/api/' + path, opts);
  if (!r.ok) { let m = r.statusText; try { m = (await r.json()).error || m; } catch { /* not json */ } throw new Error(m); }
  return r;
};

// ---------------------------------------------------------------- what can be drawn
const TYPES = {
  patch:   { list: 'Patches',  label: 'Навмеш',     one: 'Патч',     color: 0x3fcf7a, icon: '▱', key: 'P', hint: 'Рисовать навмеш: куски пола, трапы, площадки' },
  ramp:    { list: 'Patches',  label: 'Трап',       one: 'Трап',     color: 0x3fcf7a, icon: '⟋', key: 'G', hint: 'Трап / доска: два клика - полоса навмеша заданной ширины между точками (с наклоном)' },
  blocker: { list: 'Blockers', label: 'Запрет',     one: 'Запрет',   color: 0xef5b5b, icon: '▨', key: 'B', hint: 'Запретная область: навмеш под коробкой вырезается' },
  link:    { list: 'Links',    label: 'Связка',     one: 'Связка',   color: 0xf2d43d, icon: '⤢', key: 'L', hint: 'Связка двух мест навмеша (шаг через щель, дверь, дыру в заборе)' },
  climb:   { list: 'Routes',   label: 'Лазание',    one: 'Лазание',  color: 0xff9a2e, icon: '↯', key: 'R', hint: 'Лазание: опоры по порядку, прыжки и приседания; боты лезут по точкам' },
  path:    { list: 'Paths',    label: 'Маршрут',    one: 'Маршрут',  color: 0x38d4ff, icon: '➜', key: 'T', hint: 'Маршрут ботов: по точкам, с остановками' },
  spot:    { list: 'Spots',    label: 'Место',      one: 'Место',    color: 0xff52d8, icon: '◆', key: 'S', hint: 'Место интереса: боты ходят туда за лутом / осмотреться' },
};
const OBJ_TYPES = ['patch', 'blocker', 'link', 'climb', 'path', 'spot'];  // ramp makes a patch
const LIST_TYPE = { Patches: 'patch', Blockers: 'blocker', Links: 'link', Routes: 'climb', Paths: 'path', Spots: 'spot' };
const TOOLS = [
  { id: 'select', icon: '⌖', label: 'Выбор', key: 'V', hint: 'Выбор и правка: тащите точки, двойной щелчок по линии - новая точка, Delete - удалить' },
  ...['patch', 'ramp', 'blocker', 'link', 'climb', 'path', 'spot'].map(t => ({ id: t, ...TYPES[t] })),
  { id: 'measure', icon: '⟷', label: 'Линейка', key: 'M', hint: 'Линейка: два клика' },
];

const ACC = {
  patch: { get: (o, i) => o.Verts[i], set: (o, i, p) => { o.Verts[i] = p; }, n: o => o.Verts.length, min: 3, ins: (o, i, p) => o.Verts.splice(i, 0, p), del: (o, i) => o.Verts.splice(i, 1) },
  link: { get: (o, i) => i === 0 ? o.A : o.B, set: (o, i, p) => { if (i === 0) o.A = p; else o.B = p; }, n: () => 2, min: 2 },
  climb: {
    get: (o, i) => [o.Points[i].X, o.Points[i].Y, o.Points[i].Z], set: (o, i, p) => { Object.assign(o.Points[i], { X: p[0], Y: p[1], Z: p[2] }); },
    n: o => o.Points.length, min: 2, ins: (o, i, p) => o.Points.splice(i, 0, { X: p[0], Y: p[1], Z: p[2], Pose: 1, Jump: false }), del: (o, i) => o.Points.splice(i, 1),
  },
  path: {
    get: (o, i) => o.Points[i].P, set: (o, i, p) => { o.Points[i].P = p; }, n: o => o.Points.length, min: 2,
    ins: (o, i, p) => o.Points.splice(i, 0, { P: p, Wait: 2, Hold: false }), del: (o, i) => o.Points.splice(i, 1),
  },
  spot: { get: (o) => o.P, set: (o, i, p) => { o.P = p; }, n: () => 1, min: 1 },
  blocker: {
    get: (o, i) => i === 0 ? o.C : G.blockerCorners(o)[i - 1],
    set: (o, i, p) => {
      if (i === 0) { o.C = [p[0], o.C[1], p[2]]; return; }
      const cs = G.blockerCorners(o), opp = cs[(i - 1 + 2) % 4];
      const ex = G.rotY(1, 0, o.Yaw), ez = G.rotY(0, 1, o.Yaw);
      const dx = p[0] - opp[0], dz = p[2] - opp[2];
      o.Size = [Math.max(0.3, Math.abs(dx * ex[0] + dz * ex[1])), o.Size[1], Math.max(0.3, Math.abs(dx * ez[0] + dz * ez[1]))];
      o.C = [(p[0] + opp[0]) / 2, o.C[1], (p[2] + opp[2]) / 2];
    }, n: () => 5, min: 5,
  },
};

// ---------------------------------------------------------------- state
const S = {
  state: null, maps: [], mapId: null, info: null, mesh: null, base: null, patchIdx: new G.TriIndex(3),
  edits: null, exportedJson: '', exportedRev: 0,
  tool: 'select', sel: null, selV: null, draft: null, hover: null, measure: null,
  viewMode: 'plan', yr: [0, 1], band: { lo: -1e5, hi: 1e5 }, bandAll: true,
  layers: { navmesh: true, ghost: true, grid: false, shade: true, pois: true, openpoi: false, learned: true, doors: true, exfils: true, spawns: true, trails: true, offnav: true, legacy: true, labels: true },
  snap: { verts: true, grid: false },
  opt: { rampW: 1.6, blockH: 4, pathMode: 'pingpong', pathWho: 'all', pathWait: 2 },
  undo: [], redo: [], tab: 'objects', issues: [], counters: {}, saved: 'none',
};
let view;
const LAYER_DEFS = [
  ['navmesh', 'Навмеш', '#6aa8e8'], ['ghost', 'Другие этажи бледно', '#6aa8e8'], ['grid', 'Рёбра треугольников', '#9aa'], ['shade', 'Объёмная подсветка', '#9aa'],
  ['pois', 'Лут-точки и контейнеры', '#f2c94c'], ['openpoi', 'Открытые места ботов', '#7a8599'], ['learned', 'Места, где стояли вы', '#b58cff'],
  ['doors', 'Двери', '#c68a54'], ['exfils', 'Выходы', '#3fcf7a'], ['spawns', 'Точки спавна', '#e8eef9'],
  ['trails', 'Ваши следы', '#b6d36a'], ['offnav', 'Следы вне навмеша (нет пола!)', '#ff4d4d'], ['legacy', 'Записанные маршруты', '#c9822e'], ['labels', 'Подписи', '#aaa'],
];

const emptyEdits = (map) => ({ Format: 'liveraid-edits/1', Map: map, Revision: 0, Patches: [], Blockers: [], Links: [], Routes: [], Paths: [], Spots: [] });

// ---------------------------------------------------------------- helpers
function toast(msg, bad = false, ms = 3800) {
  const t = $('#toast'); t.textContent = msg; t.className = bad ? 'bad' : ''; t.style.display = 'block';
  clearTimeout(toast.t); toast.t = setTimeout(() => { t.style.display = 'none'; }, ms);
}
function modal(title, body, buttons) {
  const m = $('#modal'), card = $('.card', m);
  card.replaceChildren(h('h3', {}, title), body, h('div', { class: 'row' }, buttons.map(b => h('button', {
    class: 'btn' + (b.primary ? ' primary' : ''), onclick: () => { m.style.display = 'none'; b.cb?.(); },
  }, b.text))));
  m.style.display = 'flex';
}
const closeModal = () => { $('#modal').style.display = 'none'; };

function nextName(type) {
  S.counters[type] = (S.counters[type] || 0) + 1;
  return TYPES[type].one + ' ' + S.counters[type];
}
function allObjects() {
  const out = [];
  for (const t of OBJ_TYPES) for (const o of S.edits[TYPES[t].list]) out.push([t, o]);
  return out;
}
function findObj(type, id) { return S.edits[TYPES[type].list].find(o => o.Id === id) || null; }
const selObj = () => S.sel ? findObj(S.sel.type, S.sel.id) : null;
const isOn = (o) => o.Enabled !== false;

// ---------------------------------------------------------------- history, autosave, validation
function snapshot() {
  S.undo.push(JSON.stringify(S.edits));
  if (S.undo.length > 120) S.undo.shift();
  S.redo = [];
}
function restore(json) {
  S.edits = JSON.parse(json);
  if (S.sel && !selObj()) { S.sel = null; S.selV = null; }
  S.draft = null;
  changed(false);
}
function undo() { if (!S.undo.length) return; S.redo.push(JSON.stringify(S.edits)); restore(S.undo.pop()); }
function redo() { if (!S.redo.length) return; S.undo.push(JSON.stringify(S.edits)); restore(S.redo.pop()); }

let tAuto, tVal;
function changed(save = true) {
  for (const p of S.edits.Patches) p.Tris = G.triangulate(p.Verts);
  rebuildPatchIndex();
  refreshAll();
  clearTimeout(tVal); tVal = setTimeout(() => { validate(); renderSide(); }, 350);
  clearTimeout(tAuto);
  tAuto = setTimeout(async () => {
    if (!S.mapId) return;
    try { await api(`map/${S.mapId}/draft`, { method: 'PUT', body: JSON.stringify(S.edits) }); updateSaveChip(true); } catch { /* the draft is a convenience */ }
  }, 1200);
  updateSaveChip(false);
}
function rebuildPatchIndex() {
  const verts = [], idx = [];
  for (const p of S.edits.Patches) {
    if (!isOn(p)) continue;
    const base = verts.length / 3;
    for (const v of p.Verts) verts.push(v[0], v[1], v[2]);
    for (const t of p.Tris) idx.push(base + t);
  }
  S.patchIdx.build(new Float32Array(verts), new Uint32Array(idx));
}
function updateSaveChip(draftSaved) {
  const chip = $('#saveChip');
  const dirty = JSON.stringify(S.edits) !== S.exportedJson;
  if (!S.mapId) { chip.textContent = ''; return; }
  if (dirty) { chip.className = 'chip dirty'; chip.textContent = '● не выгружено в игру' + (draftSaved ? ' · черновик сохранён' : ''); }
  else { chip.className = 'chip ok'; chip.textContent = S.exportedRev ? `✓ в игре: ревизия ${S.exportedRev}` : '✓ без изменений'; }
}

// height of the walkable surface under (x,z): the old navmesh or a patch
function navHeight(x, z, y, tol = 0.8) {
  let best = null;
  for (const idx of [S.base, S.patchIdx]) {
    if (!idx) continue;
    const hh = idx.heightAt(x, z, y - tol, y + tol, y);
    if (hh !== null && Math.abs(hh - y) <= tol && (best === null || Math.abs(hh - y) < Math.abs(best - y))) best = hh;
  }
  return best;
}
// how far (m) the point is from the navmesh in height / null when nothing is under it
function offNav(p, tol = 1.2) {
  const hh = navHeight(p[0], p[2], p[1], tol);
  if (hh !== null) return 0;
  if (S.base?.nearestVertex(p[0], p[2], p[1] - tol, p[1] + tol, 1.0)) return 0.5;
  return -1;
}

function validate() {
  const out = [];
  const add = (sev, text, type, id, at) => out.push({ sev, text, type, id, at });
  let ok = 0;
  const nm = (o) => `«${o.Name || o.Id}»`;
  for (const p of S.edits.Patches) {
    if (!isOn(p)) continue;
    if (!p.Tris.length) { add('err', `Навмеш ${nm(p)}: контур самопересекается или вырожден, патч не построится`, 'patch', p.Id, p.Verts[0]); continue; }
    const area = Math.abs(G.polyArea(p.Verts));
    let per = 0; for (let i = 0; i < p.Verts.length; i++) per += G.dist2(p.Verts[i], p.Verts[(i + 1) % p.Verts.length]);
    const trisV = []; for (let i = 0; i < p.Tris.length; i += 3) trisV.push(p.Tris[i], p.Tris[i + 1], p.Tris[i + 2]);
    const slope = G.maxSlopeDeg(p.Verts, p.Tris);
    let bad = false;
    if (area < 1.2) { add('warn', `Навмеш ${nm(p)}: площадь ${area.toFixed(1)} м² - Unity может выбросить такой клочок`, 'patch', p.Id, p.Verts[0]); bad = true; }
    if (area / Math.max(0.1, per) * 4 < 0.95) { add('warn', `Навмеш ${nm(p)}: слишком узкий (< ~1 м): края срезаются по радиусу бота, проход может исчезнуть`, 'patch', p.Id, p.Verts[0]); bad = true; }
    if (slope > 52) { add('warn', `Навмеш ${nm(p)}: уклон до ${slope.toFixed(0)}° - боты не взойдут по такому склону`, 'patch', p.Id, p.Verts[0]); bad = true; }
    const gates = S.base ? G.computeGates(p, S.base) : [];
    const joined = gates.length > 0 || S.edits.Links.some(l => isOn(l) && [l.A, l.B].some(e => G.pointInPoly(e[0], e[2], p.Verts))) ||
      S.edits.Routes.some(r => isOn(r) && r.Points.length > 1 && [r.Points[0], r.Points[r.Points.length - 1]].some(e => G.pointInPoly(e.X, e.Z, p.Verts)));
    if (!joined) { add('warn', `Навмеш ${nm(p)}: не касается старого навмеша - боты не смогут на него попасть. Сдвиньте край на навмеш (прилипание) или добавьте связку`, 'patch', p.Id, p.Verts[0]); bad = true; }
    if (!bad) ok++;
  }
  for (const b of S.edits.Blockers) {
    if (!isOn(b)) continue;
    let bad = false;
    if (b.Size[0] < 0.5 || b.Size[2] < 0.5) { add('warn', `Запрет ${nm(b)}: меньше 0,5 м - может не вырезать навмеш`, 'blocker', b.Id, b.C); bad = true; }
    const cs = G.blockerCorners(b);
    for (const e of (S.info?.meta?.Exfils || [])) if (G.pointInPoly(e.X, e.Z, cs) && Math.abs(e.Y - b.C[1]) < b.Size[1]) { add('err', `Запрет ${nm(b)} закрывает выход «${e.Name}» - боты не смогут уйти`, 'blocker', b.Id, b.C); bad = true; }
    for (const e of (S.info?.meta?.Spawns || [])) if (G.pointInPoly(e.X, e.Z, cs) && Math.abs(e.Y - b.C[1]) < b.Size[1]) { add('warn', `Запрет ${nm(b)} лежит на точке спавна`, 'blocker', b.Id, b.C); bad = true; }
    if (!bad) ok++;
  }
  for (const l of S.edits.Links) {
    if (!isOn(l)) continue;
    let bad = false;
    [l.A, l.B].forEach((e, i) => { if (offNav(e, 1.2) < 0) { add('err', `Связка ${nm(l)}: конец ${i ? 'B' : 'A'} не на навмеше - в игре связка не создастся`, 'link', l.Id, e); bad = true; } });
    const d = G.dist3(l.A, l.B);
    if (d < 0.5) { add('warn', `Связка ${nm(l)}: короче 0,5 м`, 'link', l.Id, l.A); bad = true; }
    if (d > 40) { add('warn', `Связка ${nm(l)}: ${d.toFixed(0)} м - слишком длинная, боты будут «телепортироваться» по ней`, 'link', l.Id, l.A); bad = true; }
    if (!bad) ok++;
  }
  for (const r of S.edits.Routes) {
    if (!isOn(r)) continue;
    let bad = false;
    if (r.Points.length < 2) { add('err', `Лазание ${nm(r)}: меньше двух точек`, 'climb', r.Id, null); continue; }
    const f = [r.Points[0].X, r.Points[0].Y, r.Points[0].Z], l = r.Points[r.Points.length - 1], le = [l.X, l.Y, l.Z];
    if (offNav(f, 1.2) < 0) { add('err', `Лазание ${nm(r)}: первая точка не на навмеше - маршрут не подключится`, 'climb', r.Id, f); bad = true; }
    if (offNav(le, 1.2) < 0) { add('err', `Лазание ${nm(r)}: последняя точка не на навмеше - маршрут не подключится`, 'climb', r.Id, le); bad = true; }
    for (let i = 1; i < r.Points.length; i++) {
      const a = r.Points[i - 1], b = r.Points[i];
      if (G.dist3([a.X, a.Y, a.Z], [b.X, b.Y, b.Z]) > 3.2 && !b.Jump && !a.Jump) { add('warn', `Лазание ${nm(r)}: шаг ${i}→${i + 1} длиннее 3 м без прыжка`, 'climb', r.Id, [b.X, b.Y, b.Z]); bad = true; break; }
    }
    if (!bad) ok++;
  }
  for (const p of S.edits.Paths) {
    if (!isOn(p)) continue;
    let bad = false;
    if (p.Points.length < 2) { add('err', `Маршрут ${nm(p)}: меньше двух точек`, 'path', p.Id, null); continue; }
    const offs = p.Points.map((q, i) => [i, offNav(q.P, 1.6)]).filter(x => x[1] < 0);
    if (offs.length) { add('warn', `Маршрут ${nm(p)}: точки ${offs.slice(0, 5).map(x => x[0] + 1).join(', ')} не на навмеше - QuestingBots сдвинет их на ближайший пол или отбросит`, 'path', p.Id, p.Points[offs[0][0]].P); bad = true; }
    let len = 0; for (let i = 1; i < p.Points.length; i++) len += G.dist3(p.Points[i - 1].P, p.Points[i].P);
    if (len > 700) { add('warn', `Маршрут ${nm(p)}: ${len.toFixed(0)} м - очень длинный, бот бросит его по времени (${p.MaxTime} с)`, 'path', p.Id, p.Points[0].P); bad = true; }
    if (!bad) ok++;
  }
  for (const s of S.edits.Spots) {
    if (!isOn(s)) continue;
    if (offNav(s.P, 1.6) < 0) add('warn', `Место ${nm(s)}: не на навмеше - бот пойдёт на ближайший пол или откажется`, 'spot', s.Id, s.P); else ok++;
  }
  S.issues = out;
  S.okCount = ok;
  const bd = $('#tabs .badge'); void bd;
}

// ---------------------------------------------------------------- snapping and picking
function pickY(x, z, prefer) {
  const { lo, hi } = S.band;
  let best = null;
  for (const idx of [S.base, S.patchIdx]) {
    if (!idx) continue;
    const hh = idx.heightAt(x, z, lo, hi, prefer);
    if (hh !== null && (best === null || (prefer != null ? Math.abs(hh - prefer) < Math.abs(best - prefer) : hh > best))) best = hh;
  }
  return best;
}
const pxToWorld = (px) => view.mode === 'plan' ? px / view.ppm : px * view.dist * 0.0019;

/** Pointer -> game point [x,y,z] with snapping. opts: planeY (3D drag), preferY, skip:{type,id,vi} (the dragged handle). */
function worldPos(ev, opts = {}) {
  const pr = view.pointer(ev, opts.planeY);
  if (!pr) return null;
  let { x, z, y } = pr;
  const rect = view.canvas.getBoundingClientRect();
  const sx = ev.clientX - rect.left, sy = ev.clientY - rect.top;
  if (y == null) { const hh = pickY(x, z, opts.preferY ?? S.lastY); y = hh !== null ? hh : (opts.preferY ?? S.lastY ?? view.target[1]); }
  let snapped = null;
  if (!ev.altKey) {
    // 1. the vertices of the edits themselves
    let bd = 13;
    for (const hd of handles()) {
      if (opts.skip && hd.type === opts.skip.type && hd.id === opts.skip.id && (opts.skip.all || hd.vi === opts.skip.vi)) continue;
      const s = view.worldToScreen(hd.p);
      const d = Math.hypot(s[0] - sx, s[1] - sy);
      if (d < bd) { bd = d; snapped = [...hd.p]; }
    }
    // 2. vertices of the navmesh
    if (!snapped && S.snap.verts && S.base) {
      const v = S.base.nearestVertex(x, z, y - 1, y + 1, pxToWorld(11));
      if (v) snapped = v;
    }
    if (!snapped && S.snap.grid) {
      x = Math.round(x * 2) / 2; z = Math.round(z * 2) / 2;
      const hh = pickY(x, z, y); if (hh !== null) y = hh;
    }
  }
  const p = snapped || [x, y, z];
  S.lastY = p[1];
  return p;
}

function handles() {
  if (S._handles && S._handlesRev === S._rev) return S._handles;
  const out = [];
  for (const [type, o] of allObjects()) {
    if (!isOn(o)) continue;
    const a = ACC[type];
    for (let i = 0; i < a.n(o); i++) out.push({ type, id: o.Id, vi: i, p: a.get(o, i) });
  }
  S._handles = out; S._handlesRev = S._rev;
  return out;
}

function pickHandle(ev, radius = 11) {
  const rect = view.canvas.getBoundingClientRect(), sx = ev.clientX - rect.left, sy = ev.clientY - rect.top;
  let best = null, bd = radius;
  for (const hd of handles()) {
    const s = view.worldToScreen(hd.p), d = Math.hypot(s[0] - sx, s[1] - sy);
    // the selected object wins a tie
    const bias = S.sel && S.sel.type === hd.type && S.sel.id === hd.id ? -2 : 0;
    if (d + bias < bd) { bd = d + bias; best = hd; }
  }
  return best;
}
function segDist(px, py, a, b) {
  const dx = b[0] - a[0], dy = b[1] - a[1], l2 = dx * dx + dy * dy;
  let t = l2 ? ((px - a[0]) * dx + (py - a[1]) * dy) / l2 : 0; t = Math.max(0, Math.min(1, t));
  return [Math.hypot(px - (a[0] + dx * t), py - (a[1] + dy * t)), t];
}
function outlineOf(type, o) {
  if (type === 'patch') return { pts: o.Verts, closed: true };
  if (type === 'blocker') return { pts: G.blockerCorners(o), closed: true };
  if (type === 'link') return { pts: [o.A, o.B], closed: false };
  if (type === 'climb') return { pts: o.Points.map(q => [q.X, q.Y, q.Z]), closed: false };
  if (type === 'path') return { pts: o.Points.map(q => q.P), closed: false };
  return { pts: [o.P], closed: false };
}
/** What lies under the pointer: a segment (distance in px) or the inside of a patch/blocker. */
function pickObject(ev) {
  const rect = view.canvas.getBoundingClientRect(), sx = ev.clientX - rect.left, sy = ev.clientY - rect.top;
  let best = null, bd = 9;
  for (const [type, o] of allObjects()) {
    if (!isOn(o)) continue;
    const { pts, closed } = outlineOf(type, o);
    const sp = pts.map(p => view.worldToScreen(p));
    const n = closed ? sp.length : sp.length - 1;
    for (let i = 0; i < n; i++) {
      const [d, t] = segDist(sx, sy, sp[i], sp[(i + 1) % sp.length]);
      if (d < bd) { bd = d; best = { type, id: o.Id, seg: i, t }; }
    }
  }
  if (best) return best;
  const pr = view.pointer(ev);
  if (pr) {
    for (const [type, o] of [...allObjects()].reverse()) {
      if (!isOn(o) || (type !== 'patch' && type !== 'blocker')) continue;
      const poly = type === 'patch' ? o.Verts : G.blockerCorners(o);
      if (G.pointInPoly(pr.x, pr.z, poly)) {
        const ys = poly.map(p => p[1]);
        if (pr.y != null && (pr.y < Math.min(...ys) - 2 || pr.y > Math.max(...ys) + 2)) continue;
        return { type, id: o.Id, inside: true };
      }
    }
  }
  return null;
}

// ---------------------------------------------------------------- creating objects
function finishDraft() {
  const d = S.draft; if (!d) return;
  const pts = d.pts;
  const need = { patch: 3, climb: 2, path: 2 }[d.type] || 99;
  if (pts.length < need) { toast(`Нужно хотя бы ${need} точки`, true); return; }
  snapshot();
  let o;
  if (d.type === 'patch') o = { Id: 'patch_' + uid(), Name: nextName('patch'), Enabled: true, Verts: pts.map(p => [...p]), Tris: [], Gates: [] };
  else if (d.type === 'climb') o = { Id: `${S.mapId}_${uid()}`, Name: nextName('climb'), Enabled: true, Points: pts.map((p, i) => ({ X: p[0], Y: p[1], Z: p[2], Pose: d.pose?.[i] ?? 1, Jump: !!d.jump?.[i] })) };
  else if (d.type === 'path') o = {
    Id: 'path_' + uid(), Name: nextName('path'), Enabled: true, Points: pts.map(p => ({ P: [...p], Wait: S.opt.pathWait, Hold: false })),
    Mode: S.opt.pathMode, Who: S.opt.pathWho, Desirability: 40, MaxBots: 2, MaxTime: 300,
  };
  const list = S.edits[TYPES[d.type].list]; list.push(o);
  S.draft = null; S.sel = { type: d.type, id: o.Id }; S.selV = null; S.tab = 'props';
  changed();
}
function addPoint(p) {
  if (S.tool === 'measure') {
    S.measure = S.measure && S.measure.length === 1 ? [S.measure[0], p] : [p];
    if (S.measure.length === 2) { const m = S.measure; toast(`${G.dist3(m[0], m[1]).toFixed(2)} м по прямой · по земле ${G.dist2(m[0], m[1]).toFixed(2)} м · перепад ${(m[1][1] - m[0][1]).toFixed(2)} м`); }
    refreshAll(); return;
  }
  if (S.tool === 'spot') {
    snapshot();
    const o = { Id: 'spot_' + uid(), Name: nextName('spot'), Enabled: true, P: [...p], Kind: 'loot', W: 2 };
    S.edits.Spots.push(o); S.sel = { type: 'spot', id: o.Id }; S.selV = null; S.tab = 'props'; changed(); return;
  }
  const type = S.tool;
  if (!S.draft || S.draft.type !== type) S.draft = { type, pts: [], jump: [], pose: [] };
  const d = S.draft;
  if (type === 'patch' && d.pts.length >= 3 && G.dist3(p, d.pts[0]) < 0.05) { finishDraft(); return; }
  d.pts.push(p); d.jump.push(false); d.pose.push(1);
  if (type === 'link' && d.pts.length === 2) {
    snapshot();
    const o = { Id: 'link_' + uid(), Name: nextName('link'), Enabled: true, A: d.pts[0], B: d.pts[1], Cost: 1, TwoWay: true };
    S.edits.Links.push(o); S.draft = null; S.sel = { type: 'link', id: o.Id }; S.selV = null; S.tab = 'props'; changed(); return;
  }
  if (type === 'ramp' && d.pts.length === 2) {
    const [a, b] = d.pts, dx = b[0] - a[0], dz = b[2] - a[2], l = Math.hypot(dx, dz);
    if (l < 0.3) { S.draft = null; refreshAll(); return; }
    const nx = -dz / l * S.opt.rampW / 2, nz = dx / l * S.opt.rampW / 2;
    snapshot();
    const o = { Id: 'patch_' + uid(), Name: nextName('ramp'), Enabled: true, Verts: [[a[0] + nx, a[1], a[2] + nz], [b[0] + nx, b[1], b[2] + nz], [b[0] - nx, b[1], b[2] - nz], [a[0] - nx, a[1], a[2] - nz]], Tris: [], Gates: [] };
    S.edits.Patches.push(o); S.draft = null; S.sel = { type: 'patch', id: o.Id }; S.selV = null; S.tab = 'props'; changed(); return;
  }
  if (type === 'blocker' && d.pts.length === 3) {
    const [a, b, c] = d.pts, dx = b[0] - a[0], dz = b[2] - a[2], l = Math.hypot(dx, dz);
    if (l < 0.3) { S.draft = null; refreshAll(); return; }
    const ex = [dx / l, dz / l], nrm = [-ex[1], ex[0]];
    const side = (c[0] - a[0]) * nrm[0] + (c[2] - a[2]) * nrm[1];
    const depth = Math.max(0.3, Math.abs(side)), sg = side >= 0 ? 1 : -1;
    const yaw = Math.atan2(-ex[1], ex[0]) * 180 / Math.PI;
    snapshot();
    const o = {
      Id: 'block_' + uid(), Name: nextName('blocker'), Enabled: true, Yaw: r2(yaw),
      C: [r2((a[0] + b[0]) / 2 + nrm[0] * depth / 2 * sg), r2(Math.min(a[1], b[1]) + S.opt.blockH / 2 - 0.5), r2((a[2] + b[2]) / 2 + nrm[1] * depth / 2 * sg)],
      Size: [r2(l), S.opt.blockH, r2(depth)],
    };
    S.edits.Blockers.push(o); S.draft = null; S.sel = { type: 'blocker', id: o.Id }; S.selV = null; S.tab = 'props'; changed(); return;
  }
  refreshAll();
}

// ---------------------------------------------------------------- scene for the viewport
function buildScene() {
  const sc = { fills: [], lines: [], points: [], labels: [] };
  const sel = S.sel;
  const lab = S.layers.labels;
  for (const [type, o] of allObjects()) {
    const on = isOn(o), isSel = sel && sel.type === type && sel.id === o.Id;
    const def = TYPES[type], col = isSel ? 0xffffff : def.color, al = on ? 1 : 0.25;
    const { pts, closed } = outlineOf(type, o);
    if (!pts.length) continue;
    if (type === 'patch') {
      if (o.Tris.length) sc.fills.push({ pts: o.Verts, tris: o.Tris, color: def.color, alpha: on ? (isSel ? 0.42 : 0.28) : 0.08 });
      sc.lines.push({ pts, color: col, closed: true, alpha: al });
    } else if (type === 'blocker') {
      sc.fills.push({ pts, tris: [0, 1, 2, 0, 2, 3], color: def.color, alpha: on ? (isSel ? 0.38 : 0.22) : 0.06 });
      sc.lines.push({ pts, color: col, closed: true, alpha: al });
      const top = pts.map(p => [p[0], p[1] + o.Size[1], p[2]]);
      sc.lines.push({ pts: top, color: col, closed: true, alpha: al * 0.45 });
    } else if (type !== 'spot') {
      sc.lines.push({ pts, color: col, closed: false, alpha: al });
    }
    if (!on) continue;
    const a = ACC[type];
    for (let i = 0; i < a.n(o); i++) {
      const p = a.get(o, i);
      let c = def.color, s = isSel ? 11 : 7;
      if (type === 'climb') { const q = o.Points[i]; if (q.Jump) { c = 0xff3b3b; s += 3; } else if (q.Pose < 0.6) { c = 0xb48cff; } }
      if (type === 'blocker' && i === 0) c = 0xffffff;
      if (type === 'spot') s = 11 + Math.min(8, (o.W || 1) * 1.5);
      if (isSel && S.selV === i) { c = 0xffe14d; s = 17; }
      sc.points.push({ p, c: rgb(c), s });
      if (isSel && (type === 'path' || type === 'climb' || type === 'patch') && lab) sc.labels.push({ p, text: String(i + 1) });
    }
    if (lab && type !== 'patch' && type !== 'blocker' && type !== 'link') sc.labels.push({ p: type === 'spot' ? o.P : pts[0], text: o.Name || '' });
    else if (lab && isSel) sc.labels.push({ p: pts[0], text: o.Name || '' });
  }
  // the climbs recorded in the game (read only)
  if (S.layers.legacy) for (const r of S.info?.legacyRoutes || []) {
    const pts = r.Points.map(q => [q.X, q.Y, q.Z]);
    if (pts.length > 1) { sc.lines.push({ pts, color: 0x9a6a2a, closed: false, dashed: true, alpha: 0.9 }); sc.points.push({ p: pts[0], c: rgb(0x9a6a2a), s: 7 }); sc.points.push({ p: pts[pts.length - 1], c: rgb(0x9a6a2a), s: 7 }); }
  }
  // exits' names
  if (S.layers.exfils && lab) for (const e of S.info?.meta?.Exfils || []) sc.labels.push({ p: [e.X, e.Y, e.Z], text: e.Name });
  // in progress
  const d = S.draft;
  if (d) {
    const col = TYPES[d.type === 'ramp' ? 'patch' : d.type].color;
    const pts = [...d.pts]; if (S.hover) pts.push(S.hover);
    sc.lines.push({ pts, color: col, closed: false, dashed: true });
    if (d.type === 'patch' && pts.length >= 3) sc.lines.push({ pts: [pts[pts.length - 1], pts[0]], color: col, dashed: true, alpha: 0.4 });
    d.pts.forEach((p, i) => { sc.points.push({ p, c: rgb(d.jump[i] ? 0xff3b3b : 0xffffff), s: 9 }); if (lab) sc.labels.push({ p, text: String(i + 1) }); });
    if (d.type === 'ramp' && d.pts.length === 1 && S.hover) {
      const a = d.pts[0], b = S.hover, dx = b[0] - a[0], dz = b[2] - a[2], l = Math.hypot(dx, dz) || 1, nx = -dz / l * S.opt.rampW / 2, nz = dx / l * S.opt.rampW / 2;
      sc.lines.push({ pts: [[a[0] + nx, a[1], a[2] + nz], [b[0] + nx, b[1], b[2] + nz], [b[0] - nx, b[1], b[2] - nz], [a[0] - nx, a[1], a[2] - nz]], color: col, closed: true, alpha: 0.7 });
    }
    if (d.type === 'blocker' && d.pts.length === 2 && S.hover) {
      const [a, b] = d.pts, c = S.hover, dx = b[0] - a[0], dz = b[2] - a[2], l = Math.hypot(dx, dz) || 1, nx = -dz / l, nz = dx / l;
      const side = (c[0] - a[0]) * nx + (c[2] - a[2]) * nz;
      sc.lines.push({ pts: [a, b, [b[0] + nx * side, b[1], b[2] + nz * side], [a[0] + nx * side, a[1], a[2] + nz * side]], color: col, closed: true, alpha: 0.7 });
    }
  }
  if (S.measure) {
    const m = S.measure.length === 1 && S.hover ? [S.measure[0], S.hover] : S.measure;
    if (m.length === 2) { sc.lines.push({ pts: m, color: 0xffffff, dashed: true }); sc.labels.push({ p: [(m[0][0] + m[1][0]) / 2, (m[0][1] + m[1][1]) / 2, (m[0][2] + m[1][2]) / 2], text: G.dist3(m[0], m[1]).toFixed(2) + ' м' }); }
    S.measure.forEach(p => sc.points.push({ p, c: rgb(0xffffff), s: 9 }));
  }
  return sc;
}

function refreshAll() {
  S._rev = (S._rev || 0) + 1;
  view.setEdits(buildScene());
  renderTools(); renderHint(); renderSide(); renderFloor();
}

// ---------------------------------------------------------------- layers of the map itself
function rebuildStaticLayers() {
  const info = S.info, meta = info?.meta || {};
  const poiCol = { Container: 0xf2c94c, LooseLoot: 0xe8a33d, LootSpot: 0xc9a14a, Open: 0x7a8599 };
  const pois = [], open = [];
  for (const p of info?.pois || []) {
    const item = { p: [p[0], p[1], p[2]], c: rgb(poiCol[p[3]] || 0xf2c94c), s: p[3] === 'Container' ? 7 : 5 };
    (p[3] === 'Open' ? open : pois).push(item);
  }
  view.setLayer('pois', pois, 6); view.setLayer('openpoi', open, 4);
  view.setLayer('learned', (info?.learned || []).map(p => ({ p: [p[0], p[1], p[2]], c: rgb(0xb58cff), s: 6 + Math.min(6, p[3]) })), 8);
  view.setLayer('doors', (meta.Doors || []).map(d => ({ p: [d.X, d.Y, d.Z], c: rgb(d.State === 'Locked' ? 0xd9573b : 0xc68a54), s: 7 })), 7);
  view.setLayer('exfils', (meta.Exfils || []).map(e => ({ p: [e.X, e.Y, e.Z], c: rgb(e.Kind === 'scav' ? 0xe0c34a : 0x3fcf7a), s: 15 })), 15);
  view.setLayer('spawns', (meta.Spawns || []).map(e => ({ p: [e.X, e.Y, e.Z], c: rgb(0xe8eef9), s: 9 })), 9);
  const tr = info?.trails || [];
  view.setLayer('trails', tr.filter(t => t[3] === 1).map(t => ({ p: [t[0], t[1], t[2]], c: rgb(0xb6d36a), s: 3 })), 3);
  view.setLayer('offnav', tr.filter(t => t[3] === 0).map(t => ({ p: [t[0], t[1], t[2]], c: rgb(0xff4d4d), s: 6 })), 6);
  for (const [k] of LAYER_DEFS) if (!['navmesh', 'ghost', 'grid', 'shade', 'labels', 'legacy'].includes(k)) view.setLayerVisible(k, S.layers[k]);
  applyNavOptions();
}
function applyNavOptions() {
  view.setNavVisible(S.layers.navmesh);
  view.setNavOptions({ ghost: S.layers.ghost ? 0.12 : 0, grid: S.layers.grid, shade: S.layers.shade || S.viewMode === '3d', dim: S.bandAll ? 1 : 0.15 });
}

// ---------------------------------------------------------------- UI: tools, hint, floor, side panel
function renderTools() {
  const nav = $('#tools');
  nav.replaceChildren(...TOOLS.flatMap((t, i) => {
    const b = h('button', { class: 'tool' + (S.tool === t.id ? ' on' : ''), title: `${t.hint} (${t.key})`, onclick: () => setTool(t.id) },
      h('span', { class: 'ic', style: t.color ? `color:${hex(t.color)}` : '' }, t.icon), h('span', { class: 'nm' }, t.label), h('span', { class: 'key' }, t.key));
    return (i === 1 || i === TOOLS.length - 1) ? [h('div', { class: 'tsep' }), b] : [b];
  }));
}
function setTool(id) {
  S.tool = id; S.draft = null; S.measure = null; S.hover = null;
  if (id !== 'select') S.selV = null;
  refreshAll();
}
function renderHint() {
  const t = TOOLS.find(x => x.id === S.tool);
  let txt = t.hint;
  const d = S.draft;
  if (d) {
    if (d.type === 'patch') txt = `Контур: ${d.pts.length} точек. Щёлкайте по углам; Enter или двойной щелчок - закончить (нужно ≥3), Backspace - убрать последнюю, Esc - отмена`;
    if (d.type === 'climb') txt = `Лазание: ${d.pts.length} точек. J - последняя точка с прыжком, C - с приседанием, Enter - закончить. Первая и последняя - на навмеше`;
    if (d.type === 'path') txt = `Маршрут: ${d.pts.length} точек. Enter или двойной щелчок - закончить, Backspace - убрать последнюю`;
    if (d.type === 'blocker') txt = d.pts.length === 1 ? 'Запрет: щёлкните второй угол первой стороны' : 'Запрет: щёлкните, куда тянется вторая сторона';
    if (d.type === 'link') txt = 'Связка: щёлкните второй конец (оба - на навмеше)';
    if (d.type === 'ramp') txt = `Трап шириной ${S.opt.rampW} м: щёлкните второй конец`;
  } else if (S.tool === 'ramp') txt = `Трап (ширина ${S.opt.rampW} м): щёлкните начало`;
  else if (S.tool === 'blocker') txt = 'Запрет: щёлкните первый угол стороны (затем второй угол и глубину)';
  else if (S.tool === 'link') txt = 'Связка: щёлкните первый конец (на навмеше)';
  $('#hint').textContent = txt;
}
function renderFloor() {
  const box = $('#floor'), [y0, y1] = S.yr;
  const lo = S.band.lo, hi = S.band.hi;
  const inp = (v, cb) => h('input', { type: 'number', class: 'num', step: '0.5', value: r2(v), onchange: e => cb(parseFloat(e.target.value)) });
  box.replaceChildren(
    h('h4', {}, h('span', {}, 'Этаж (высота, м)'), h('span', {}, S.bandAll ? 'все' : `${r2(lo)} … ${r2(hi)}`)),
    h('div', { class: 'row' }, h('label', {}, 'от'), h('input', { type: 'range', min: y0, max: y1, step: 0.1, value: Math.max(y0, lo), oninput: e => setBand(parseFloat(e.target.value), Math.max(S.band.hi, parseFloat(e.target.value)), false) }), inp(Math.max(y0, lo), v => setBand(v, Math.max(v, S.band.hi), false))),
    h('div', { class: 'row' }, h('label', {}, 'до'), h('input', { type: 'range', min: y0, max: y1, step: 0.1, value: Math.min(y1, hi), oninput: e => setBand(Math.min(S.band.lo, parseFloat(e.target.value)), parseFloat(e.target.value), false) }), inp(Math.min(y1, hi), v => setBand(Math.min(S.band.lo, v), v, false))),
    h('div', { class: 'btns' },
      h('button', { class: 'btn small' + (S.bandAll ? ' on' : ''), title: 'Все этажи (A)', onclick: () => setBand(y0, y1, true) }, 'Все'),
      h('button', { class: 'btn small', title: 'Этаж под курсором (F)', onclick: () => floorAtCursor() }, 'Под курсором'),
      h('button', { class: 'btn small', title: 'Выше (PageUp)', onclick: () => shiftBand(1) }, '▲'),
      h('button', { class: 'btn small', title: 'Ниже (PageDown)', onclick: () => shiftBand(-1) }, '▼')),
  );
}
function setBand(lo, hi, all) {
  S.bandAll = all;
  S.band = all ? { lo: -1e5, hi: 1e5 } : { lo, hi };
  view.setBand(S.band.lo, S.band.hi);
  applyNavOptions(); renderFloor();
}
function floorAtCursor() {
  const y = S.hover ? S.hover[1] : (S.lastY ?? view.target[1]);
  setBand(y - 1.2, y + 2.6, false);
}
function shiftBand(dir) {
  const [y0, y1] = S.yr;
  if (S.bandAll) { floorAtCursor(); return; }
  const step = 3.2 * dir;
  setBand(S.band.lo + step, S.band.hi + step, false);
  void y0; void y1;
}

function renderSide() {
  const errs = S.issues.filter(i => i.sev === 'err').length, warns = S.issues.filter(i => i.sev === 'warn').length;
  const tabs = [['objects', 'Объекты'], ['props', 'Свойства'], ['check', 'Проверка'], ['layers', 'Слои']];
  $('#tabs').replaceChildren(...tabs.map(([id, name]) => h('button', { class: S.tab === id ? 'on' : '', onclick: () => { S.tab = id; renderSide(); } },
    name, id === 'check' && (errs || warns) ? h('span', { class: 'badge' + (errs ? '' : ' w') }, String(errs + warns)) : (id === 'check' && S.mapId ? h('span', { class: 'badge g' }, '✓') : null))));
  const pane = $('#pane'), scroll = pane.scrollTop;
  const fn = { objects: paneObjects, props: paneProps, check: paneCheck, layers: paneLayers }[S.tab];
  pane.replaceChildren(...fn());
  pane.scrollTop = scroll;
}

function paneObjects() {
  const out = [];
  const total = allObjects().length;
  if (!total) out.push(h('p', { class: 'hint2' }, 'Пока ничего не нарисовано. Выберите инструмент слева (P - навмеш, G - трап, R - лазание, T - маршрут ботов) и щёлкайте по карте.'));
  for (const t of OBJ_TYPES) {
    const list = S.edits[TYPES[t].list];
    if (!list.length) continue;
    out.push(h('h4', {}, `${TYPES[t].label} · ${list.length}`));
    out.push(h('div', { class: 'list' }, list.map(o => h('div', {
      class: 'item' + (S.sel?.id === o.Id ? ' sel' : '') + (isOn(o) ? '' : ' off'),
      onclick: () => selectObj(t, o.Id, true),
    }, h('span', { class: 'dot', style: `background:${hex(TYPES[t].color)}` }), h('span', { class: 'nm' }, o.Name || o.Id),
    h('span', { class: 'meta' }, metaOf(t, o)),
    h('span', { class: 'ico', title: isOn(o) ? 'Выключить (не попадёт в игру)' : 'Включить', onclick: e => { e.stopPropagation(); snapshot(); o.Enabled = !isOn(o); changed(); } }, isOn(o) ? '◉' : '○'),
    h('span', { class: 'ico', title: 'Удалить', onclick: e => { e.stopPropagation(); deleteObj(t, o.Id); } }, '✕')))));
  }
  const legacy = (S.info?.legacyRoutes || []).filter(r => !S.edits.Routes.some(x => x.Id === r.Id));
  if (legacy.length) {
    out.push(h('h4', {}, 'Записанные маршруты (routes/<карта>.json)'));
    out.push(h('p', { class: 'hint2' }, `В игре уже записано ${legacy.length}. Игра их читает сама; импортируйте, чтобы править здесь (тот же Id - дубликата не будет).`));
    out.push(h('button', { class: 'btn small', onclick: importLegacy }, `Импортировать ${legacy.length}`));
  }
  return out;
}
function metaOf(t, o) {
  if (t === 'patch') return `${o.Verts.length} т · ${Math.abs(G.polyArea(o.Verts)).toFixed(0)} м²`;
  if (t === 'blocker') return `${o.Size[0].toFixed(0)}×${o.Size[2].toFixed(0)} м`;
  if (t === 'link') return `${G.dist3(o.A, o.B).toFixed(1)} м`;
  if (t === 'climb') return `${o.Points.length} т`;
  if (t === 'path') return `${o.Points.length} т`;
  return o.Kind === 'loot' ? 'лут' : o.Kind === 'camp' ? 'засада' : 'осмотр';
}
function selectObj(type, id, focus) {
  S.sel = { type, id }; S.selV = null;
  if (focus) {
    const o = selObj();
    if (o) { const { pts } = outlineOf(type, o); const c = pts.reduce((a, p) => [a[0] + p[0] / pts.length, a[1] + p[1] / pts.length, a[2] + p[2] / pts.length], [0, 0, 0]); view.focus(c); }
  }
  S.tab = 'props';
  refreshAll();
}
function deleteObj(type, id) {
  snapshot();
  const list = S.edits[TYPES[type].list], i = list.findIndex(o => o.Id === id);
  if (i >= 0) list.splice(i, 1);
  if (S.sel?.id === id) { S.sel = null; S.selV = null; }
  changed();
}
function importLegacy() {
  snapshot();
  for (const r of S.info.legacyRoutes) if (!S.edits.Routes.some(x => x.Id === r.Id)) S.edits.Routes.push({ ...clone(r), Name: r.Id, Enabled: true });
  changed(); toast('Маршруты импортированы');
}

function field(label, ...ctrl) { return h('div', { class: 'field' }, h('label', {}, label), ctrl.length > 1 ? h('div', { class: 'multi' }, ctrl) : ctrl[0]); }
function textIn(o, key, after) { return h('input', { type: 'text', value: o[key] ?? '', onchange: e => { snapshot(); o[key] = e.target.value; changed(); after?.(); } }); }
function numIn(o, key, { step = 0.1, min, max, cb } = {}) {
  return h('input', { type: 'number', step, min, max, value: r2(o[key] ?? 0), onchange: e => { const v = parseFloat(e.target.value); if (Number.isNaN(v)) return; snapshot(); o[key] = v; changed(); cb?.(); } });
}
function selIn(o, key, opts) { return h('select', { onchange: e => { snapshot(); o[key] = e.target.value; changed(); } }, opts.map(([v, t]) => h('option', { value: v, selected: o[key] === v }, t))); }
function chk(o, key, label) { return h('label', { style: 'display:inline-flex;gap:5px;align-items:center' }, h('input', { type: 'checkbox', checked: !!o[key], onchange: e => { snapshot(); o[key] = e.target.checked; changed(); } }), label); }

function paneProps() {
  const out = [];
  const o = selObj();
  // tool options first
  if (!o) {
    if (S.tool === 'ramp') out.push(h('h4', {}, 'Трап'), field('Ширина, м', h('input', { type: 'number', step: 0.1, min: 1, value: S.opt.rampW, onchange: e => { S.opt.rampW = Math.max(1, parseFloat(e.target.value) || 1.6); renderHint(); } })), h('p', { class: 'hint2' }, 'Минимум ~1 м: края срезаются по радиусу бота. Высота концов берётся с пола под точками - наклон получится сам.'));
    else if (S.tool === 'blocker') out.push(h('h4', {}, 'Запрет'), field('Высота коробки, м', h('input', { type: 'number', step: 0.5, min: 1, value: S.opt.blockH, onchange: e => { S.opt.blockH = Math.max(1, parseFloat(e.target.value) || 4); } })));
    else if (S.tool === 'path') out.push(h('h4', {}, 'Новые маршруты'),
      field('Ход', h('select', { onchange: e => { S.opt.pathMode = e.target.value; } }, [['pingpong', 'Туда и обратно'], ['loop', 'По кругу'], ['once', 'Один раз']].map(([v, t]) => h('option', { value: v, selected: S.opt.pathMode === v }, t)))),
      field('Кто ходит', h('select', { onchange: e => { S.opt.pathWho = e.target.value; } }, [['all', 'Все боты'], ['pmc', 'Только ЧВК'], ['scav', 'Только дикие']].map(([v, t]) => h('option', { value: v, selected: S.opt.pathWho === v }, t)))),
      field('Пауза в точке, с', h('input', { type: 'number', step: 0.5, min: 0, value: S.opt.pathWait, onchange: e => { S.opt.pathWait = Math.max(0, parseFloat(e.target.value) || 0); } })));
    out.push(h('p', { class: 'hint2' }, 'Ничего не выбрано. Выберите объект на карте или в списке «Объекты».'));
    return out;
  }
  const type = S.sel.type, def = TYPES[type];
  out.push(h('h4', {}, def.one), field('Название', textIn(o, 'Name')));
  out.push(field('', chk(o, 'Enabled', 'Включено (попадёт в игру)')));
  if (type === 'patch') {
    const gates = S.base ? G.computeGates(o, S.base) : [];
    out.push(h('p', { class: 'hint2' }, `${o.Verts.length} вершин, ${o.Tris.length / 3} треугольников, ${Math.abs(G.polyArea(o.Verts)).toFixed(1)} м², уклон до ${o.Tris.length ? G.maxSlopeDeg(o.Verts, o.Tris).toFixed(0) : 0}°. Стыков со старым навмешем: ${gates.length}.`));
    out.push(h('div', { style: 'display:flex;gap:6px;flex-wrap:wrap' },
      h('button', { class: 'btn small', onclick: () => levelPatch(o) }, 'Выровнять по средней высоте'),
      h('button', { class: 'btn small', onclick: () => dropPatch(o) }, 'Прижать к полу')));
  }
  if (type === 'blocker') {
    out.push(field('Центр x/y/z', ...[0, 1, 2].map(i => h('input', { type: 'number', step: 0.1, value: r2(o.C[i]), onchange: e => { snapshot(); o.C[i] = parseFloat(e.target.value) || 0; changed(); } }))));
    out.push(field('Размер (ш/в/г)', ...[0, 1, 2].map(i => h('input', { type: 'number', step: 0.1, min: 0.3, value: r2(o.Size[i]), onchange: e => { snapshot(); o.Size[i] = Math.max(0.3, parseFloat(e.target.value) || 1); changed(); } }))));
    out.push(field('Поворот, °', numIn(o, 'Yaw', { step: 1 })));
    out.push(h('p', { class: 'hint2' }, 'В игре навмеш под коробкой вырезается (карвинг). Боты обходят её и не заходят внутрь.'));
  }
  if (type === 'link') {
    out.push(field('Цена прохода', numIn(o, 'Cost', { step: 0.1, min: 0 })), field('', chk(o, 'TwoWay', 'В обе стороны')));
    out.push(h('p', { class: 'hint2' }, `Длина ${G.dist3(o.A, o.B).toFixed(1)} м. Цена 1 - как по полу; больше - боты берут связку, только если обход намного длиннее.`));
  }
  if (type === 'climb') out.push(h('p', { class: 'hint2' }, 'Бот лезет по точкам; прыжок - в точках с галкой «прыжок». Ссылка на навмеш - по первой и последней точке. Видимое игроку лазание проигрывается по точкам, невидимое - сразу в конец.'));
  if (type === 'path') {
    out.push(field('Ход', selIn(o, 'Mode', [['pingpong', 'Туда и обратно'], ['loop', 'По кругу'], ['once', 'Один раз']])));
    out.push(field('Кто ходит', selIn(o, 'Who', [['all', 'Все боты'], ['pmc', 'Только ЧВК'], ['scav', 'Только дикие']])));
    out.push(field('Желанность', numIn(o, 'Desirability', { step: 5, min: 0, max: 100 })));
    out.push(field('Ботов одновременно', numIn(o, 'MaxBots', { step: 1, min: 1, max: 10 })));
    out.push(field('Время на маршрут, с', numIn(o, 'MaxTime', { step: 30, min: 60 })));
    out.push(h('p', { class: 'hint2' }, 'В игре это квест QuestingBots «LR Path …»: бот идёт по точкам по порядку. Желанность 40 - обычная, 70+ - боты охотно берут. Маршруты применяются со следующего рейда.'));
  }
  if (type === 'spot') {
    out.push(field('Что это', selIn(o, 'Kind', [['loot', 'Лут (бот осмотрит)'], ['hold', 'Осмотреться'], ['camp', 'Засада']])));
    out.push(field('Вес', numIn(o, 'W', { step: 0.5, min: 0.5, max: 12 })));
    out.push(h('p', { class: 'hint2' }, 'Место входит в маршруты исследования района. Больше вес - чаще бот сюда заходит. Со следующего рейда.'));
  }
  // vertices
  const a = ACC[type];
  if (a.n(o) > 1 && type !== 'blocker') {
    out.push(h('h4', {}, 'Точки'));
    const rows = [];
    for (let i = 0; i < a.n(o); i++) {
      const p = a.get(o, i), q = type === 'climb' ? o.Points[i] : type === 'path' ? o.Points[i] : null;
      rows.push(h('div', { class: 'pt' + (S.selV === i ? ' sel' : ''), onclick: () => { S.selV = i; view.focus(p); refreshAll(); } },
        h('span', {}, String(i + 1)), h('span', {}, `${p[0].toFixed(1)}, ${p[1].toFixed(1)}, ${p[2].toFixed(1)}`),
        q && type === 'climb' ? h('span', { class: 'tg' }, h('label', {}, h('input', { type: 'checkbox', checked: q.Jump, onclick: e => e.stopPropagation(), onchange: e => { snapshot(); q.Jump = e.target.checked; changed(); } }), 'прыжок'),
          h('label', {}, h('input', { type: 'checkbox', checked: q.Pose < 0.6, onclick: e => e.stopPropagation(), onchange: e => { snapshot(); q.Pose = e.target.checked ? 0 : 1; changed(); } }), 'присед')) :
          q && type === 'path' ? h('span', { class: 'tg' }, h('input', { type: 'number', step: 0.5, min: 0, style: 'width:48px;padding:1px 3px', title: 'Пауза, с', value: q.Wait, onclick: e => e.stopPropagation(), onchange: e => { snapshot(); q.Wait = Math.max(0, parseFloat(e.target.value) || 0); changed(); } }),
            h('label', {}, h('input', { type: 'checkbox', checked: q.Hold, onclick: e => e.stopPropagation(), onchange: e => { snapshot(); q.Hold = e.target.checked; changed(); } }), 'осмотр')) : h('span')));
    }
    out.push(h('div', { class: 'pts' }, rows));
  }
  if (S.selV != null && type !== 'blocker' || (type === 'blocker' && S.selV === 0)) {
    const p = a.get(o, S.selV);
    out.push(h('h4', {}, `Точка ${S.selV + 1}`));
    out.push(field('x / y / z', ...[0, 1, 2].map(i => h('input', { type: 'number', step: 0.1, value: r2(p[i]), onchange: e => { snapshot(); const np = [...p]; np[i] = parseFloat(e.target.value) || 0; a.set(o, S.selV, np); changed(); } }))));
    out.push(h('div', { style: 'display:flex;gap:6px' },
      h('button', { class: 'btn small', onclick: () => nudgeY(0.1) }, 'Выше 0,1 ]'), h('button', { class: 'btn small', onclick: () => nudgeY(-0.1) }, 'Ниже 0,1 ['),
      h('button', { class: 'btn small', onclick: () => deleteVertex() }, 'Удалить точку')));
  }
  out.push(h('div', { style: 'margin-top:14px;display:flex;gap:6px' },
    h('button', { class: 'btn small', onclick: () => duplicateObj() }, 'Дублировать'),
    h('button', { class: 'btn small', onclick: () => deleteObj(type, o.Id) }, 'Удалить объект')));
  return out;
}
function nudgeY(dy) {
  const o = selObj(); if (!o || S.selV == null) return;
  const a = ACC[S.sel.type], p = [...a.get(o, S.selV)]; p[1] += dy;
  snapshot(); a.set(o, S.selV, p); changed();
}
function deleteVertex() {
  const o = selObj(); if (!o || S.selV == null) return;
  const a = ACC[S.sel.type];
  if (!a.del || a.n(o) <= a.min) { toast(`Нельзя: меньше ${a.min} точек`, true); return; }
  snapshot(); a.del(o, S.selV); S.selV = Math.min(S.selV, a.n(o) - 1); changed();
}
function levelPatch(o) { snapshot(); const m = o.Verts.reduce((s, v) => s + v[1], 0) / o.Verts.length; o.Verts.forEach(v => { v[1] = r2(m); }); changed(); }
function dropPatch(o) {
  snapshot();
  o.Verts.forEach(v => { const hh = S.base?.heightAt(v[0], v[2], v[1] - 1.5, v[1] + 1.5, v[1]); if (hh !== null && hh !== undefined) v[1] = r2(hh); });
  changed();
}
function duplicateObj() {
  const o = selObj(); if (!o) return;
  snapshot();
  const c = clone(o); c.Id = o.Id.split('_')[0] + '_' + uid(); c.Name = (o.Name || '') + ' (копия)';
  const dx = 3;
  const type = S.sel.type, a = ACC[type];
  for (let i = 0; i < a.n(c); i++) { if (type === 'blocker') { c.C[0] += dx; break; } const p = [...a.get(c, i)]; p[0] += dx; a.set(c, i, p); }
  S.edits[TYPES[type].list].push(c); S.sel = { type, id: c.Id }; S.selV = null; changed();
}

function paneCheck() {
  const out = [];
  if (!S.issues.length) out.push(h('div', { class: 'issue ok' }, h('span', { class: 'sv' }, '✓'), h('div', {}, S.okCount ? `Всё в порядке (проверено объектов: ${S.okCount}).` : 'Нечего проверять: ничего не нарисовано.')));
  for (const i of S.issues) out.push(h('div', { class: 'issue ' + i.sev, onclick: () => { if (i.type && i.id) { selectObj(i.type, i.id, false); S.tab = 'check'; } if (i.at) view.focus(i.at); refreshAll(); } },
    h('span', { class: 'sv' }, i.sev === 'err' ? '✕' : '!'), h('div', {}, i.text)));
  out.push(h('p', { class: 'hint2' }, 'Ошибки (✕) - то, что в игре не заработает; предупреждения (!) - может не сработать или сработать не так. Проверка идёт по навмешу, который выгрузила игра.'));
  const stats = S.info?.meta;
  if (stats && stats.Tris) out.push(h('p', { class: 'hint2' }, `Карта: ${stats.Tris} треугольников навмеша, выгрузка ${stats.Saved || '-'} (мод ${stats.Mod || '-'}).`));
  return out;
}
function paneLayers() {
  const out = [h('h4', {}, 'Слои карты')];
  for (const [k, name, color] of LAYER_DEFS) {
    out.push(h('label', { class: 'layer' }, h('input', { type: 'checkbox', checked: S.layers[k], onchange: e => { S.layers[k] = e.target.checked; if (k === 'labels' || k === 'legacy') refreshAll(); else { rebuildStaticLayers(); } } }),
      h('span', { class: 'sw', style: `background:${color}` }), name));
  }
  out.push(h('h4', {}, 'Прилипание'),
    h('label', { class: 'layer' }, h('input', { type: 'checkbox', checked: S.snap.verts, onchange: e => { S.snap.verts = e.target.checked; } }), 'К вершинам навмеша (края патчей встают точно на пол)'),
    h('label', { class: 'layer' }, h('input', { type: 'checkbox', checked: S.snap.grid, onchange: e => { S.snap.grid = e.target.checked; } }), 'К сетке 0,5 м'),
    h('p', { class: 'hint2' }, 'Alt при щелчке - без прилипания. Прилипание к точкам ваших же объектов работает всегда.'));
  return out;
}

// ---------------------------------------------------------------- input on the viewport
function bindInput() {
  const cv = $('#view');
  let drag = null;
  cv.addEventListener('pointerdown', ev => {
    if (ev.button !== 0 || !S.mesh) return;
    cv.setPointerCapture?.(ev.pointerId);
    if (S.tool !== 'select') { drag = { kind: 'click', x: ev.clientX, y: ev.clientY }; return; }
    const hd = pickHandle(ev);
    if (hd) {
      S.sel = { type: hd.type, id: hd.id }; S.selV = hd.vi;
      snapshot(); S.undo.pop(); // the snapshot is taken at the first real move, below
      drag = { kind: 'handle', hd, x: ev.clientX, y: ev.clientY, moved: false, before: JSON.stringify(S.edits) };
      refreshAll(); return;
    }
    const ob = pickObject(ev);
    if (ob) {
      S.sel = { type: ob.type, id: ob.id }; S.selV = null;
      drag = { kind: 'object', ob, x: ev.clientX, y: ev.clientY, moved: false, before: JSON.stringify(S.edits), start: worldPos(ev, { skip: { type: ob.type, id: ob.id, all: true } }) };
      refreshAll(); return;
    }
    drag = { kind: 'pan', x: ev.clientX, y: ev.clientY };
    view.beginDrag(ev, 'pan', moved => { if (!moved && (S.sel || S.selV != null)) { S.sel = null; S.selV = null; refreshAll(); } drag = null; });
  });
  cv.addEventListener('pointermove', ev => {
    if (!S.mesh) return;
    if (drag && (drag.kind === 'handle' || drag.kind === 'object')) {
      if (!drag.moved && Math.abs(ev.clientX - drag.x) + Math.abs(ev.clientY - drag.y) < 4) return;
      if (!drag.moved) { drag.moved = true; S.undo.push(drag.before); if (S.undo.length > 120) S.undo.shift(); S.redo = []; }
      const o = selObj(); if (!o) return;
      if (drag.kind === 'handle') {
        const a = ACC[drag.hd.type], old = a.get(o, drag.hd.vi);
        const p = worldPos(ev, { planeY: view.mode === '3d' ? old[1] : undefined, preferY: old[1], skip: { type: drag.hd.type, id: drag.hd.id, vi: drag.hd.vi } });
        if (p) a.set(o, drag.hd.vi, p);
      } else {
        const p = worldPos(ev, { planeY: view.mode === '3d' ? drag.start?.[1] : undefined, skip: { type: drag.ob.type, id: drag.ob.id, all: true } });
        if (p && drag.start) {
          const base = JSON.parse(drag.before), src = findIn(base, drag.ob.type, drag.ob.id);
          const dx = p[0] - drag.start[0], dz = p[2] - drag.start[2], a = ACC[drag.ob.type];
          for (let i = 0; i < a.n(src); i++) {
            if (drag.ob.type === 'blocker') { o.C = [src.C[0] + dx, src.C[1], src.C[2] + dz]; break; }
            const q = a.get(src, i); a.set(o, i, [q[0] + dx, q[1], q[2] + dz]);
          }
        }
      }
      for (const pp of S.edits.Patches) pp.Tris = G.triangulate(pp.Verts);
      rebuildPatchIndex(); S._rev++; view.setEdits(buildScene()); renderSideLight();
      return;
    }
    const needHover = S.tool !== 'select' || !drag;
    if (needHover) {
      const p = worldPos(ev, { preferY: S.draft?.pts.length ? S.draft.pts[S.draft.pts.length - 1][1] : undefined });
      S.hover = p; updateStatus(p, ev);
      if (S.draft || (S.measure && S.measure.length === 1)) { view.setEdits(buildScene()); }
      if (S.tool === 'select' && !drag) cv.style.cursor = pickHandle(ev) ? 'move' : pickObject(ev) ? 'pointer' : 'default';
      else cv.style.cursor = S.tool === 'select' ? 'default' : 'crosshair';
    }
  });
  cv.addEventListener('pointerup', ev => {
    const d = drag; drag = null;
    if (!d) return;
    if (d.kind === 'click' && Math.abs(ev.clientX - d.x) + Math.abs(ev.clientY - d.y) < 5) {
      const p = worldPos(ev, { preferY: S.draft?.pts.length ? S.draft.pts[S.draft.pts.length - 1][1] : undefined });
      if (p) addPoint(p);
    } else if ((d.kind === 'handle' || d.kind === 'object') && d.moved) {
      changed(false); changed();
    }
  });
  cv.addEventListener('dblclick', ev => {
    if (!S.mesh) return;
    if (S.draft && (S.draft.type === 'patch' || S.draft.type === 'path' || S.draft.type === 'climb')) {
      // the double click already added a point on its second click: drop the duplicate and finish
      const d = S.draft; if (d.pts.length > 1 && G.dist3(d.pts[d.pts.length - 1], d.pts[d.pts.length - 2]) < 0.05) { d.pts.pop(); d.jump.pop(); d.pose.pop(); }
      finishDraft(); return;
    }
    if (S.tool === 'select') {
      const ob = pickObject(ev);
      if (ob && ob.seg !== undefined && S.sel?.id === ob.id && ACC[ob.type].ins) {
        const o = selObj(), a = ACC[ob.type], n = a.n(o);
        const p0 = a.get(o, ob.seg), p1 = a.get(o, (ob.seg + 1) % n);
        snapshot();
        a.ins(o, ob.seg + 1, [r2(p0[0] + (p1[0] - p0[0]) * ob.t), r2(p0[1] + (p1[1] - p0[1]) * ob.t), r2(p0[2] + (p1[2] - p0[2]) * ob.t)]);
        S.selV = ob.seg + 1; changed();
      }
    }
  });
  window.addEventListener('keydown', onKey);
}
function findIn(edits, type, id) { return edits[TYPES[type].list].find(o => o.Id === id); }
function renderSideLight() { if (S.tab === 'props') { const sc = $('#pane').scrollTop; $('#pane').replaceChildren(...paneProps()); $('#pane').scrollTop = sc; } }

function updateStatus(p, ev) {
  if (!p) return;
  const on = offNav(p, 0.6) >= 0;
  $('#stCursor').textContent = `x ${p[0].toFixed(1)} · y ${p[1].toFixed(2)} · z ${p[2].toFixed(1)} · ${on ? 'на навмеше' : 'мимо навмеша'}`;
  void ev;
}

function onKey(ev) {
  if (/^(INPUT|SELECT|TEXTAREA)$/.test(ev.target.tagName) && ev.key !== 'Escape') return;
  const k = ev.key, ctrl = ev.ctrlKey || ev.metaKey;
  if (ctrl && k.toLowerCase() === 'z') { ev.preventDefault(); undo(); return; }
  if (ctrl && (k.toLowerCase() === 'y' || (ev.shiftKey && k.toLowerCase() === 'z'))) { ev.preventDefault(); redo(); return; }
  if (ctrl && k.toLowerCase() === 's') { ev.preventDefault(); exportToGame(); return; }
  if (ctrl) return;
  const tool = TOOLS.find(t => t.key.toLowerCase() === k.toLowerCase());
  if (tool && !ev.shiftKey) { setTool(tool.id); return; }
  if (k === 'Escape') { if (S.draft || S.measure) { S.draft = null; S.measure = null; refreshAll(); } else if (S.tool !== 'select') setTool('select'); else { S.sel = null; S.selV = null; refreshAll(); } return; }
  if (k === 'Enter') { finishDraft(); return; }
  if (k === 'Backspace' || k === 'Delete') {
    ev.preventDefault();
    if (S.draft && k === 'Backspace') { S.draft.pts.pop(); S.draft.jump.pop(); S.draft.pose.pop(); if (!S.draft.pts.length) S.draft = null; refreshAll(); return; }
    if (S.sel) { if (k === 'Backspace' && S.selV != null) deleteVertex(); else deleteObj(S.sel.type, S.sel.id); }
    return;
  }
  if (k === 'Tab') { ev.preventDefault(); setViewMode(S.viewMode === 'plan' ? '3d' : 'plan'); return; }
  if (k.toLowerCase() === 'f') { floorAtCursor(); return; }
  if (k.toLowerCase() === 'a') { setBand(S.yr[0], S.yr[1], true); return; }
  if (k === 'PageUp') { shiftBand(1); return; }
  if (k === 'PageDown') { shiftBand(-1); return; }
  if (k.toLowerCase() === 'h') { fitView(); return; }
  if (k === ']') { nudgeY(ev.shiftKey ? 1 : 0.1); return; }
  if (k === '[') { nudgeY(ev.shiftKey ? -1 : -0.1); return; }
  if ((k.toLowerCase() === 'j' || k.toLowerCase() === 'c') && S.draft?.type === 'climb' && S.draft.pts.length) {
    const i = S.draft.pts.length - 1;
    if (k.toLowerCase() === 'j') S.draft.jump[i] = !S.draft.jump[i]; else S.draft.pose[i] = S.draft.pose[i] < 0.6 ? 1 : 0;
    refreshAll(); return;
  }
}

// ---------------------------------------------------------------- loading maps, saving
async function loadMaps(keep) {
  S.maps = await (await api('maps')).json();
  const sel = $('#mapSel');
  sel.replaceChildren(...S.maps.map(m => h('option', { value: m.id, selected: m.id === (keep || S.mapId) },
    `${m.name}${m.hasDump ? '' : ' - нет навмеша'}${m.hasEdits ? ' ✎' : ''}`)));
}
async function loadMap(id) {
  S.mapId = id; localStorage.setItem('lr-studio-map', id);
  S.sel = null; S.selV = null; S.draft = null; S.measure = null; S.undo = []; S.redo = []; S.counters = {}; S.issues = [];
  $('#loading').style.display = 'flex'; $('#empty').style.display = 'none';
  const m = S.maps.find(x => x.id === id);
  try {
    const info = await (await api(`map/${id}/info`)).json();
    S.info = info;
    if (!m?.hasDump) { S.mesh = null; S.base = null; view.setMesh(null); S.edits = emptyEdits(id); showEmpty(m); $('#loading').style.display = 'none'; refreshAll(); return; }
    const mesh = G.parseMesh(await (await api(`map/${id}/mesh`)).arrayBuffer());
    S.mesh = mesh;
    S.base = new G.TriIndex(4).build(mesh.verts, mesh.idx);
    view.setMesh(mesh);
    S.yr = view.yRange || [0, 1];
    const meta = info.meta || {};
    const bounds = meta.Min ? { min: meta.Min, max: meta.Max } : { min: [-100, 0, -100], max: [100, 10, 100] };
    // what is in the game now, or the autosaved draft when it is newer
    let edits = info.edits ? { ...emptyEdits(id), ...info.edits } : emptyEdits(id);
    S.exportedRev = info.edits?.Revision || 0;
    S.exportedJson = JSON.stringify(normalize(edits, id));
    if (info.draft && info.draftTime > info.editsTime + 1 && JSON.stringify(normalize(info.draft, id)) !== S.exportedJson) {
      const when = new Date(info.draftTime * 1000).toLocaleString('ru-RU');
      await new Promise(res => modal('Найден черновик', h('p', {}, `От ${when}: правки, которые вы не успели выгрузить в игру. Восстановить?`), [
        { text: 'Взять версию из игры', cb: () => res() },
        { text: 'Восстановить черновик', primary: true, cb: () => { edits = { ...emptyEdits(id), ...info.draft }; res(); } }]));
    }
    S.edits = normalize(edits, id);
    for (const t of OBJ_TYPES) S.counters[t] = S.edits[TYPES[t].list].length;
    for (const p of S.edits.Patches) p.Tris = G.triangulate(p.Verts);
    rebuildPatchIndex();
    S.layers.ghost = true;
    rebuildStaticLayers();
    setBand(S.yr[0], S.yr[1], true);
    await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
    fitView(bounds);
    validate();
    $('#stMesh').textContent = `навмеш: ${mesh.nt.toLocaleString('ru-RU')} треугольников · выгрузка ${meta.Saved || '—'}`;
    refreshAll(); updateSaveChip(false);
    $('#stInfo').textContent = '';
  } catch (e) {
    toast('Не удалось открыть карту: ' + e.message, true, 8000);
  } finally {
    $('#loading').style.display = 'none';
  }
}
function normalize(e, id) {
  const o = { ...emptyEdits(id), ...clone(e) };
  for (const k of ['Patches', 'Blockers', 'Links', 'Routes', 'Paths', 'Spots']) o[k] = o[k] || [];
  for (const p of o.Patches) { p.Verts = p.Verts || []; p.Tris = p.Tris || []; p.Gates = p.Gates || []; if (p.Enabled === undefined) p.Enabled = true; }
  for (const r of o.Routes) { if (r.Enabled === undefined) r.Enabled = true; r.Name = r.Name || r.Id; }
  return o;
}
function showEmpty(m) {
  const box = $('#empty .box');
  box.replaceChildren(h('h3', {}, `${m?.name || S.mapId}: нет навмеша для редактора`),
    h('p', {}, 'Навмеш карты знает только игра. Мод записывает его сам в первом рейде на карте (достаточно загрузиться в рейд и можно выйти): файлы появятся в BepInEx/plugins/LiveRaid/studio/dump.'),
    h('p', { class: 'hint2' }, 'Если рейд уже был, а карты тут нет - проверьте в F12 → «Маршруты через завалы» пункт «Выгружать навмеш для Studio» и нажмите ⟳ сверху.'),
    S.maps.some(x => x.id === 'demo') ? h('button', { class: 'btn primary', onclick: () => { $('#mapSel').value = 'demo'; loadMap('demo'); } }, 'Открыть учебную карту') : null);
  $('#empty').style.display = 'flex';
}
function fitView(b) {
  const meta = S.info?.meta;
  b = b || (meta?.Min ? { min: meta.Min, max: meta.Max } : null);
  if (b) view.fit(b);
}
function setViewMode(m) {
  S.viewMode = m; view.setMode(m);
  document.querySelectorAll('#viewSeg button').forEach(b => b.classList.toggle('on', b.dataset.v === m));
  applyNavOptions(); refreshAll();
}

function exportable() {
  const e = clone(S.edits);
  for (const p of e.Patches) { p.Tris = G.triangulate(p.Verts); p.Gates = S.base && isOn(p) ? G.computeGates(p, S.base).map(g => ({ O: g.O.map(r2), I: g.I.map(r2) })) : []; p.Verts = p.Verts.map(v => v.map(r2)); }
  e.Routes = e.Routes.map(r => ({ ...r, Points: r.Points.map(q => ({ X: r2(q.X), Y: r2(q.Y), Z: r2(q.Z), Pose: q.Pose, Jump: !!q.Jump })) }));
  return e;
}
async function exportToGame() {
  if (!S.mapId || !S.mesh) { toast('Откройте карту', true); return; }
  validate();
  const errs = S.issues.filter(i => i.sev === 'err');
  const go = async () => {
    clearTimeout(tAuto);
    try {
      const res = await (await api(`map/${S.mapId}/export`, { method: 'POST', body: JSON.stringify(exportable()) })).json();
      S.exportedRev = res.revision; S.exportedJson = JSON.stringify(S.edits);
      updateSaveChip(false);
      toast(`Выгружено в игру: ревизия ${res.revision}. Если рейд идёт, навмеш, запреты, связки и лазания обновятся за ~2 секунды; маршруты и места - со следующего рейда.`, false, 7000);
    } catch (e) { toast('Не выгрузилось: ' + e.message, true, 8000); }
  };
  if (!errs.length) return go();
  modal('В правках есть ошибки', h('div', {}, h('p', {}, 'В игре это не заработает:'), h('ul', {}, errs.slice(0, 8).map(i => h('li', {}, i.text)))),
    [{ text: 'Исправить' , cb: () => { S.tab = 'check'; renderSide(); } }, { text: 'Всё равно выгрузить', primary: true, cb: go }]);
}

function showHelp() {
  const keys = [['V / P / G / B', 'выбор / навмеш / трап / запрет'], ['L / R / T / S / M', 'связка / лазание / маршрут / место / линейка'],
    ['Enter, двойной щелчок', 'закончить контур, лазание, маршрут'], ['Backspace', 'убрать последнюю точку (в рисовании) / выбранную вершину'],
    ['Delete', 'удалить выбранный объект'], ['Esc', 'отмена, затем снять выбор'], ['Двойной щелчок по линии', 'добавить точку в выбранный объект'],
    ['Колесо', 'масштаб'], ['Правая / средняя кнопка', 'двигать карту (в 3D правая - вращать, Shift+правая - двигать)'],
    ['Tab', 'план ↔ 3D'], ['F / A / PgUp / PgDn', 'этаж под курсором / все этажи / этаж выше / ниже'],
    ['[ и ]', 'выбранную точку ниже / выше на 0,1 м (Shift - на 1 м)'], ['J / C', 'в лазании: прыжок / приседание в последней точке'],
    ['Alt при щелчке', 'без прилипания'], ['Ctrl+Z / Ctrl+Y', 'отменить / вернуть'], ['Ctrl+S', 'выгрузить в игру'], ['H', 'показать всю карту']];
  modal('Клавиши', h('div', { class: 'keys' }, keys.flatMap(([a, b]) => [h('div', {}, a), h('div', {}, b)])), [{ text: 'Закрыть', primary: true }]);
}
async function showSettings() {
  const st = await (await api('state')).json();
  const inp = h('input', { type: 'text', value: st.gameDir || '', style: 'width:100%', placeholder: 'C:\\Games\\SPT' });
  modal('Папка игры', h('div', {}, h('p', { class: 'hint2' }, 'Папка SPT, где лежит BepInEx. Отсюда Studio берёт навмеш карт и сюда выгружает правки (BepInEx/plugins/LiveRaid/studio/edits).'), inp,
    h('p', { class: 'hint2' }, st.hasPlugin ? `Мод LiveRaid найден: ${st.pluginDir}` : 'Папка LiveRaid в BepInEx/plugins не найдена - сначала запустите игру с модом.')),
  [{ text: 'Закрыть' }, { text: 'Применить', primary: true, cb: async () => {
    try { await api('game', { method: 'POST', body: JSON.stringify({ gameDir: inp.value }) }); await loadMaps(); toast('Папка игры сохранена'); } catch (e) { toast(e.message, true); }
  } }]);
}

// ---------------------------------------------------------------- start
async function main() {
  if (window.matchMedia?.('(prefers-color-scheme: light)').matches) document.documentElement.dataset.theme = 'light';
  view = new View($('#view'), $('#labels'));
  window.__studio = { S, view, G }; // for debugging in the browser console
  bindInput();
  $('#btnUndo').onclick = undo; $('#btnRedo').onclick = redo;
  $('#btnExport').onclick = exportToGame; $('#btnHelp').onclick = showHelp; $('#btnSettings').onclick = showSettings;
  $('#btnMaps').onclick = async () => { await loadMaps(); toast('Список карт обновлён'); };
  $('#mapSel').onchange = e => switchMap(e.target.value);
  document.querySelectorAll('#viewSeg button').forEach(b => { b.onclick = () => setViewMode(b.dataset.v); });
  $('#modal').addEventListener('mousedown', e => { if (e.target.id === 'modal') closeModal(); });
  setInterval(() => fetch('/api/ping').catch(() => {}), 4000);
  S.edits = emptyEdits(''); renderTools(); renderHint(); renderFloor(); renderSide();
  try {
    S.state = await (await api('state')).json();
    await loadMaps();
    const last = localStorage.getItem('lr-studio-map');
    const pick = S.maps.find(m => m.id === last && m.hasDump) || S.maps.find(m => m.hasDump && m.id !== 'demo') || S.maps.find(m => m.id === 'demo') || S.maps[0];
    if (pick) { $('#mapSel').value = pick.id; await loadMap(pick.id); }
    if (!S.state.hasPlugin) showSettings();
  } catch (e) { toast('Нет связи с программой: ' + e.message, true, 10000); }
}
async function switchMap(id) {
  if (JSON.stringify(S.edits) !== S.exportedJson && S.mapId) {
    const ok = await new Promise(res => modal('Есть невыгруженные правки', h('p', {}, 'Черновик сохранён автоматически, его можно восстановить при следующем открытии карты. Перейти на другую карту?'),
      [{ text: 'Остаться', cb: () => res(false) }, { text: 'Перейти', primary: true, cb: () => res(true) }]));
    if (!ok) { $('#mapSel').value = S.mapId; return; }
  }
  await loadMap(id);
}
main();
