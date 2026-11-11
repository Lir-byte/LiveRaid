// The 3D/plan viewport of LiveRaid Studio. World coordinates are the game's (x east, y up, z north); Three.js is right-handed,
// so a point is drawn at (x, y, -z) and the plan camera looks down with north (+z) up on the screen.
import * as THREE from 'three';

const T = (p) => new THREE.Vector3(p[0], p[1], -p[2]);

const NAV_VS = `
attribute vec3 bary; attribute float area;
varying vec3 vB; varying vec3 vW; varying float vA;
void main(){ vB = bary; vW = vec3(position.x, position.y, -position.z); vA = area; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`;
const NAV_FS = `
precision highp float;
uniform float uLo, uHi, uMin, uMax, uGrid, uGhost, uShade;
varying vec3 vB; varying vec3 vW; varying float vA;
void main(){
  bool inb = vW.y >= uLo - 0.05 && vW.y <= uHi + 0.05;
  if (uGhost > 0.0) { if (inb) discard; } else if (!inb) discard;
  float t = clamp((vW.y - uMin) / max(0.001, uMax - uMin), 0.0, 1.0);
  vec3 col = mix(vec3(0.10, 0.22, 0.42), vec3(0.62, 0.80, 0.96), t);
  if (vA > 0.5) col = mix(col, vec3(0.95, 0.55, 0.2), 0.55);
  vec3 n = normalize(cross(dFdx(vW), dFdy(vW)));
  float lit = 0.62 + 0.38 * abs(n.y);
  col *= mix(1.0, lit, uShade);
  float e = min(min(vB.x, vB.y), vB.z);
  float w = fwidth(e);
  float line = 1.0 - smoothstep(0.0, max(w * 1.3, 0.0001), e);
  col = mix(col, col * 0.45, line * uGrid);
  gl_FragColor = vec4(col, uGhost > 0.0 ? uGhost : 1.0);
}`;
const PT_VS = `
attribute vec3 color; attribute float size;
uniform float uLo, uHi, uBand;
varying vec3 vC; varying float vOut;
void main(){ vC = color; vOut = (uBand > 0.5 && (position.y < uLo - 0.5 || position.y > uHi + 0.5)) ? 1.0 : 0.0;
  gl_PointSize = size; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`;
const PT_FS = `
precision highp float;
uniform float uDim;
varying vec3 vC; varying float vOut;
void main(){
  vec2 c = gl_PointCoord - 0.5; float d = length(c);
  if (d > 0.5) discard;
  float a = vOut > 0.5 ? uDim : 1.0;
  if (a <= 0.0) discard;
  float rim = smoothstep(0.38, 0.5, d);
  gl_FragColor = vec4(mix(vC, vec3(0.05), rim * 0.7), a);
}`;

export class View {
  constructor(canvas, labelsEl) {
    this.canvas = canvas; this.labelsEl = labelsEl;
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0x0b0d11);
    this.mode = 'plan';
    this.target = [0, 0, 0];          // game coordinates
    this.ppm = 4;                      // plan: pixels per metre
    this.yaw = 0.7; this.pitch = 0.75; this.dist = 220;
    this.ortho = new THREE.OrthographicCamera(-1, 1, 1, -1, -5000, 5000);
    this.persp = new THREE.PerspectiveCamera(50, 1, 0.5, 8000);
    this.cam = this.ortho;
    this.band = { lo: -1e5, hi: 1e5 };
    this.uni = {
      uLo: { value: -1e5 }, uHi: { value: 1e5 }, uMin: { value: 0 }, uMax: { value: 1 },
      uGrid: { value: 0 }, uGhost: { value: 0 }, uShade: { value: 0 },
    };
    this.ptUni = { uLo: this.uni.uLo, uHi: this.uni.uHi, uBand: { value: 1 }, uDim: { value: 0.18 } };
    this.navGroup = new THREE.Group(); this.layerGroup = new THREE.Group(); this.editGroup = new THREE.Group();
    this.scene.add(this.navGroup, this.layerGroup, this.editGroup);
    this.layers = {};
    this.labels = [];
    this.dirty = true;
    this.raycaster = new THREE.Raycaster();
    this._drag = null;
    this._bindEvents();
    this._loop = this._loop.bind(this);
    requestAnimationFrame(this._loop);
    new ResizeObserver(() => { this.dirty = true; }).observe(canvas.parentElement);
  }

  // ---------- navmesh ----------
  setMesh(mesh) {
    for (const o of [...this.navGroup.children]) { this.navGroup.remove(o); o.geometry?.dispose(); }
    if (!mesh) { this.navMesh = null; this.dirty = true; return; }
    const { verts, idx, areas, nt } = mesh;
    const pos = new Float32Array(nt * 9), bary = new Float32Array(nt * 9), ar = new Float32Array(nt * 3);
    let ymin = 1e9, ymax = -1e9;
    for (let t = 0; t < nt; t++) {
      for (let k = 0; k < 3; k++) {
        const v = idx[t * 3 + k] * 3, o = t * 9 + k * 3;
        pos[o] = verts[v]; pos[o + 1] = verts[v + 1]; pos[o + 2] = -verts[v + 2];
        bary[o + k] = 1;
        ar[t * 3 + k] = areas[t] || 0;
        if (verts[v + 1] < ymin) ymin = verts[v + 1];
        if (verts[v + 1] > ymax) ymax = verts[v + 1];
      }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    g.setAttribute('bary', new THREE.BufferAttribute(bary, 3));
    g.setAttribute('area', new THREE.BufferAttribute(ar, 1));
    g.computeBoundingSphere();
    this.uni.uMin.value = ymin; this.uni.uMax.value = ymax;
    const solid = new THREE.ShaderMaterial({ vertexShader: NAV_VS, fragmentShader: NAV_FS, uniforms: this.uni, side: THREE.DoubleSide, extensions: { derivatives: true } });
    const ghost = new THREE.ShaderMaterial({
      vertexShader: NAV_VS, fragmentShader: NAV_FS, side: THREE.DoubleSide, transparent: true, depthWrite: false,
      uniforms: { ...this.uni, uGhost: { value: 0.12 } },
    });
    this.navMesh = new THREE.Mesh(g, solid);
    this.ghostMesh = new THREE.Mesh(g, ghost);
    this.ghostMesh.renderOrder = -1;
    this.navGroup.add(this.navMesh, this.ghostMesh);
    this.yRange = [ymin, ymax];
    this.dirty = true;
  }

  setBand(lo, hi) {
    this.band = { lo, hi };
    this.uni.uLo.value = lo; this.uni.uHi.value = hi;
    this.dirty = true;
  }
  setNavOptions({ ghost, grid, shade, dim }) {
    if (this.ghostMesh) { this.ghostMesh.visible = ghost > 0; this.ghostMesh.material.uniforms.uGhost.value = Math.max(0.01, ghost); }
    this.uni.uGrid.value = grid ? 1 : 0;
    this.ghostMesh && (this.ghostMesh.material.uniforms.uGrid.value = grid ? 1 : 0);
    this.uni.uShade.value = shade ? 1 : 0;
    this.ghostMesh && (this.ghostMesh.material.uniforms.uShade.value = shade ? 1 : 0);
    this.ptUni.uDim.value = dim;
    this.dirty = true;
  }
  setNavVisible(v) { this.navGroup.visible = v; this.dirty = true; }

  // ---------- static layers (loot places, doors, trails...) ----------
  setLayer(name, pts, size) { // pts: [{p:[x,y,z], c:[r,g,b]}]
    const old = this.layers[name];
    if (old) { this.layerGroup.remove(old); old.geometry.dispose(); }
    if (!pts.length) { delete this.layers[name]; this.dirty = true; return; }
    const o = this._points(pts, size, true);
    o.userData.name = name; o.visible = this._vis?.[name] !== false;
    this.layers[name] = o; this.layerGroup.add(o); this.dirty = true;
  }
  setLayerVisible(name, v) { (this._vis = this._vis || {})[name] = v; if (this.layers[name]) this.layers[name].visible = v; this.dirty = true; }

  _points(pts, size, banded) {
    const n = pts.length, pos = new Float32Array(n * 3), col = new Float32Array(n * 3), sz = new Float32Array(n);
    for (let i = 0; i < n; i++) {
      const p = pts[i].p;
      pos[i * 3] = p[0]; pos[i * 3 + 1] = p[1]; pos[i * 3 + 2] = -p[2];
      const c = pts[i].c || [1, 1, 1]; col.set(c, i * 3);
      sz[i] = pts[i].s || size;
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    g.setAttribute('color', new THREE.BufferAttribute(col, 3));
    g.setAttribute('size', new THREE.BufferAttribute(sz, 1));
    const m = new THREE.ShaderMaterial({
      vertexShader: PT_VS, fragmentShader: PT_FS, transparent: true, depthTest: false,
      uniforms: banded ? this.ptUni : { ...this.ptUni, uBand: { value: 0 } },
    });
    const o = new THREE.Points(g, m);
    o.frustumCulled = false; o.renderOrder = 5;
    return o;
  }

  // ---------- edit visuals: rebuilt on every change ----------
  setEdits(scene) { // { fills:[{pts,tris,color,alpha}], lines:[{pts,color,closed,dashed,alpha}], points:[{p,c,s}], labels:[{p,text}] }
    for (const o of [...this.editGroup.children]) { this.editGroup.remove(o); o.geometry?.dispose(); }
    for (const f of scene.fills || []) {
      if (!f.tris.length) continue;
      const g = new THREE.BufferGeometry();
      g.setFromPoints(f.pts.map(T));
      g.setIndex(f.tris);
      const m = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ color: f.color, transparent: true, opacity: f.alpha ?? 0.35, side: THREE.DoubleSide, depthTest: false }));
      m.renderOrder = 10; this.editGroup.add(m);
    }
    for (const l of scene.lines || []) {
      if (l.pts.length < 2) continue;
      const g = new THREE.BufferGeometry().setFromPoints(l.pts.map(T));
      const Mat = l.dashed ? THREE.LineDashedMaterial : THREE.LineBasicMaterial;
      const m = new Mat({ color: l.color, transparent: true, opacity: l.alpha ?? 1, depthTest: false, ...(l.dashed ? { dashSize: 0.8, gapSize: 0.5 } : {}) });
      const o = l.closed ? new THREE.LineLoop(g, m) : new THREE.Line(g, m);
      if (l.dashed) o.computeLineDistances();
      o.renderOrder = 11; this.editGroup.add(o);
    }
    if (scene.points?.length) { const o = this._points(scene.points, 10, false); o.renderOrder = 12; this.editGroup.add(o); }
    this.labels = scene.labels || [];
    this._syncLabels();
    this.dirty = true;
  }

  _syncLabels() {
    const el = this.labelsEl;
    while (el.children.length < this.labels.length) el.appendChild(document.createElement('span'));
    while (el.children.length > this.labels.length) el.removeChild(el.lastChild);
    this.labels.forEach((l, i) => { el.children[i].textContent = l.text; });
  }

  // ---------- camera ----------
  setMode(mode) {
    this.mode = mode;
    this.cam = mode === 'plan' ? this.ortho : this.persp;
    this.dirty = true;
  }
  fit(bounds) { // bounds: {min:[x,y,z], max:[x,y,z]}
    const cx = (bounds.min[0] + bounds.max[0]) / 2, cz = (bounds.min[2] + bounds.max[2]) / 2;
    const w = Math.max(10, bounds.max[0] - bounds.min[0]), d = Math.max(10, bounds.max[2] - bounds.min[2]);
    this.target = [cx, (bounds.min[1] + bounds.max[1]) / 2, cz];
    const r = this.canvas.parentElement.getBoundingClientRect();
    this.ppm = Math.min(Math.max(100, r.width) / w, Math.max(100, r.height) / d) * 0.9;
    this.dist = Math.max(w, d) * 0.9;
    this.dirty = true;
  }
  focus(p, ppm) { this.target = [p[0], p[1], p[2]]; if (ppm) this.ppm = ppm; this.dirty = true; }

  _applyCamera() {
    const r = this.canvas.getBoundingClientRect();
    const w = Math.max(2, r.width), h = Math.max(2, r.height);
    const [tx, ty, tz] = this.target;
    if (this.mode === 'plan') {
      const c = this.ortho;
      c.left = -w / 2 / this.ppm; c.right = w / 2 / this.ppm; c.top = h / 2 / this.ppm; c.bottom = -h / 2 / this.ppm;
      c.position.set(tx, ty + 1500, -tz); c.up.set(0, 0, -1); c.lookAt(tx, ty, -tz); c.updateProjectionMatrix();
    } else {
      const c = this.persp;
      c.aspect = w / h;
      const cp = Math.cos(this.pitch), sp = Math.sin(this.pitch);
      c.position.set(tx + this.dist * cp * Math.sin(this.yaw), ty + this.dist * sp, -tz + this.dist * cp * Math.cos(this.yaw));
      c.up.set(0, 1, 0); c.lookAt(tx, ty, -tz); c.updateProjectionMatrix();
    }
    this.cam.updateMatrixWorld();
  }

  worldToScreen(p) {
    const v = T(p).project(this.cam);
    const r = this.canvas.getBoundingClientRect();
    return [(v.x * 0.5 + 0.5) * r.width, (-v.y * 0.5 + 0.5) * r.height, v.z];
  }

  /** Game-space point under the pointer. planeY: intersect that horizontal plane; else (3D) the navmesh inside the band. Returns {x,z,y|null}. */
  pointer(ev, planeY) {
    this._applyCamera();
    const r = this.canvas.getBoundingClientRect();
    const nx = (ev.clientX - r.left) / r.width * 2 - 1, ny = -((ev.clientY - r.top) / r.height * 2 - 1);
    if (this.mode === 'plan') {
      const x = this.target[0] + nx * (r.width / 2) / this.ppm;
      const z = this.target[2] + ny * (r.height / 2) / this.ppm;
      return { x, z, y: null };
    }
    this.raycaster.setFromCamera({ x: nx, y: ny }, this.cam);
    if (planeY !== undefined && planeY !== null) {
      const plane = new THREE.Plane(new THREE.Vector3(0, 1, 0), -planeY);
      const hit = new THREE.Vector3();
      if (this.raycaster.ray.intersectPlane(plane, hit)) return { x: hit.x, z: -hit.z, y: planeY };
      return null;
    }
    if (this.navMesh) {
      const hits = this.raycaster.intersectObject(this.navMesh, false);
      for (const h of hits) if (h.point.y >= this.band.lo - 0.1 && h.point.y <= this.band.hi + 0.1) return { x: h.point.x, z: -h.point.z, y: h.point.y };
    }
    const plane = new THREE.Plane(new THREE.Vector3(0, 1, 0), -this.target[1]);
    const hit = new THREE.Vector3();
    return this.raycaster.ray.intersectPlane(plane, hit) ? { x: hit.x, z: -hit.z, y: null } : null;
  }

  // ---------- input: camera navigation (tools get the left button) ----------
  _bindEvents() {
    const c = this.canvas;
    c.addEventListener('contextmenu', e => e.preventDefault());
    c.addEventListener('wheel', e => {
      e.preventDefault();
      const f = Math.exp(-e.deltaY * 0.0015);
      if (this.mode === 'plan') {
        const before = this.pointer(e);
        this.ppm = Math.min(200, Math.max(0.15, this.ppm * f));
        const after = this.pointer(e);
        this.target[0] += before.x - after.x; this.target[2] += before.z - after.z;
      } else {
        this.dist = Math.min(4000, Math.max(2, this.dist / f));
      }
      this.dirty = true;
    }, { passive: false });
    c.addEventListener('pointerdown', e => {
      if (e.button === 1 || e.button === 2) this.beginDrag(e, e.button === 2 && this.mode === '3d' && !e.shiftKey ? 'orbit' : 'pan');
    });
    window.addEventListener('pointermove', e => {
      const d = this._drag; if (!d) return;
      const dx = e.clientX - d.x, dy = e.clientY - d.y; d.x = e.clientX; d.y = e.clientY;
      if (Math.abs(e.clientX - d.sx) + Math.abs(e.clientY - d.sy) > 3) d.moved = true;
      if (d.kind === 'orbit') {
        this.yaw -= dx * 0.006; this.pitch = Math.min(1.5, Math.max(0.05, this.pitch + dy * 0.006));
      } else if (this.mode === 'plan') {
        this.target[0] -= dx / this.ppm; this.target[2] += dy / this.ppm;
      } else {
        const k = this.dist * 0.0016, sy = Math.sin(this.yaw), cy = Math.cos(this.yaw);
        this.target[0] -= (dx * cy) * k; this.target[2] += (dx * sy) * k;
        this.target[0] += (dy * sy) * k * 1; this.target[2] += (dy * cy) * k * 1;
      }
      this.dirty = true;
    });
    window.addEventListener('pointerup', () => { if (this._drag) { const d = this._drag; this._drag = null; d.done?.(d.moved); } });
  }
  beginDrag(e, kind = 'pan', done) {
    this._drag = { kind, x: e.clientX, y: e.clientY, sx: e.clientX, sy: e.clientY, moved: false, done };
  }
  get dragging() { return !!this._drag; }

  _loop() {
    requestAnimationFrame(this._loop);
    const r = this.canvas.parentElement.getBoundingClientRect();
    if (this.canvas.width !== Math.round(r.width * this.renderer.getPixelRatio()) || this.canvas.height !== Math.round(r.height * this.renderer.getPixelRatio())) {
      this.renderer.setSize(r.width, r.height, false); this.dirty = true;
    }
    if (!this.dirty) return;
    this.dirty = false;
    this._applyCamera();
    this.renderer.render(this.scene, this.cam);
    const els = this.labelsEl.children;
    for (let i = 0; i < this.labels.length && i < els.length; i++) {
      const s = this.worldToScreen(this.labels[i].p);
      els[i].style.left = s[0] + 'px'; els[i].style.top = s[1] + 'px';
      els[i].style.display = s[2] > 1 ? 'none' : '';
    }
  }
}
