// Geometry helpers of LiveRaid Studio. World coordinates are the game's (Unity): x east, y up, z north.

export function parseMesh(buf) {
  const dv = new DataView(buf);
  const magic = String.fromCharCode(...new Uint8Array(buf, 0, 7));
  if (magic !== 'LRMESH1') throw new Error('Это не выгрузка навмеша LiveRaid (LRMESH1)');
  const nv = dv.getUint32(8, true), ni = dv.getUint32(12, true);
  const verts = new Float32Array(buf.slice(16, 16 + nv * 12));
  const idx = new Uint32Array(buf.slice(16 + nv * 12, 16 + nv * 12 + ni * 4));
  const areas = new Uint8Array(buf.slice(16 + nv * 12 + ni * 4, 16 + nv * 12 + ni * 4 + ni / 3));
  return { verts, idx, areas, nv, nt: ni / 3 };
}

/** Uniform xz grid over triangles: height of the surface under a point, nearest vertex. */
export class TriIndex {
  constructor(cell = 4) {
    this.cell = cell;
    this.verts = new Float32Array(0);
    this.idx = new Uint32Array(0);
    this.grid = new Map();
  }
  build(verts, idx) {
    this.verts = verts; this.idx = idx; this.grid = new Map();
    const c = this.cell;
    for (let t = 0; t < idx.length; t += 3) {
      const a = idx[t] * 3, b = idx[t + 1] * 3, d = idx[t + 2] * 3;
      const x0 = Math.min(verts[a], verts[b], verts[d]), x1 = Math.max(verts[a], verts[b], verts[d]);
      const z0 = Math.min(verts[a + 2], verts[b + 2], verts[d + 2]), z1 = Math.max(verts[a + 2], verts[b + 2], verts[d + 2]);
      for (let gx = Math.floor(x0 / c); gx <= Math.floor(x1 / c); gx++)
        for (let gz = Math.floor(z0 / c); gz <= Math.floor(z1 / c); gz++) {
          const k = gx * 73856093 ^ gz * 19349663;
          let l = this.grid.get(k);
          if (!l) this.grid.set(k, l = []);
          l.push(t);
        }
    }
    return this;
  }
  tris(x, z) { return this.grid.get(Math.floor(x / this.cell) * 73856093 ^ Math.floor(z / this.cell) * 19349663) || []; }
  /** All surface heights at (x,z), ascending. */
  heights(x, z) {
    const out = [], v = this.verts, idx = this.idx;
    for (const t of this.tris(x, z)) {
      const a = idx[t] * 3, b = idx[t + 1] * 3, c = idx[t + 2] * 3;
      const d = (v[b + 2] - v[c + 2]) * (v[a] - v[c]) + (v[c] - v[b]) * (v[a + 2] - v[c + 2]);
      if (Math.abs(d) < 1e-9) continue;
      const l1 = ((v[b + 2] - v[c + 2]) * (x - v[c]) + (v[c] - v[b]) * (z - v[c + 2])) / d;
      const l2 = ((v[c + 2] - v[a + 2]) * (x - v[c]) + (v[a] - v[c]) * (z - v[c + 2])) / d;
      const l3 = 1 - l1 - l2, e = -1e-4;
      if (l1 >= e && l2 >= e && l3 >= e) out.push(l1 * v[a + 1] + l2 * v[b + 1] + l3 * v[c + 1]);
    }
    return out.sort((p, q) => p - q);
  }
  /** Surface at (x,z) inside [lo,hi]; the one nearest to `prefer` if given, else the highest. null when there is none. */
  heightAt(x, z, lo, hi, prefer) {
    const hs = this.heights(x, z).filter(h => h >= lo - 0.4 && h <= hi + 0.4);
    if (!hs.length) return null;
    if (prefer === undefined || prefer === null) return hs[hs.length - 1];
    let best = hs[0];
    for (const h of hs) if (Math.abs(h - prefer) < Math.abs(best - prefer)) best = h;
    return best;
  }
  /** Nearest mesh vertex within r (xz) and the y band. */
  nearestVertex(x, z, lo, hi, r) {
    const c = this.cell, v = this.verts, idx = this.idx;
    let best = null, bd = r * r;
    for (let gx = Math.floor((x - r) / c); gx <= Math.floor((x + r) / c); gx++)
      for (let gz = Math.floor((z - r) / c); gz <= Math.floor((z + r) / c); gz++) {
        const l = this.grid.get(gx * 73856093 ^ gz * 19349663);
        if (!l) continue;
        for (const t of l) for (let k = 0; k < 3; k++) {
          const i = idx[t + k] * 3, y = v[i + 1];
          if (y < lo - 0.4 || y > hi + 0.4) continue;
          const d = (v[i] - x) ** 2 + (v[i + 2] - z) ** 2;
          if (d < bd) { bd = d; best = [v[i], y, v[i + 2]]; }
        }
      }
    return best;
  }
}

export function polyArea(p) { // signed, xz plane (positive = counter-clockwise seen with z up)
  let s = 0;
  for (let i = 0; i < p.length; i++) { const a = p[i], b = p[(i + 1) % p.length]; s += a[0] * b[2] - b[0] * a[2]; }
  return s / 2;
}

export function pointInPoly(x, z, p) {
  let inside = false;
  for (let i = 0, j = p.length - 1; i < p.length; j = i++) {
    const a = p[i], b = p[j];
    if ((a[2] > z) !== (b[2] > z) && x < (b[0] - a[0]) * (z - a[2]) / (b[2] - a[2]) + a[0]) inside = !inside;
  }
  return inside;
}

function segInter(a, b, c, d) {
  const o = (p, q, r) => (q[0] - p[0]) * (r[2] - p[2]) - (q[2] - p[2]) * (r[0] - p[0]);
  const o1 = o(a, b, c), o2 = o(a, b, d), o3 = o(c, d, a), o4 = o(c, d, b);
  return o1 * o2 < -1e-9 && o3 * o4 < -1e-9;
}

export function polyIsSimple(p) {
  const n = p.length;
  for (let i = 0; i < n; i++) for (let j = i + 1; j < n; j++) {
    if (Math.abs(i - j) <= 1 || (i === 0 && j === n - 1)) continue;
    if (segInter(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n])) return false;
  }
  return true;
}

/** Ear clipping in the xz plane; returns triangle indices into p, or [] for a degenerate polygon. */
export function triangulate(p) {
  const n = p.length;
  if (n < 3) return [];
  const ord = [...Array(n).keys()];
  if (polyArea(p) < 0) ord.reverse();
  const out = [];
  const cross = (a, b, c) => (b[0] - a[0]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[0] - a[0]);
  const inTri = (pt, a, b, c) => cross(a, b, pt) >= -1e-9 && cross(b, c, pt) >= -1e-9 && cross(c, a, pt) >= -1e-9;
  let guard = 0;
  while (ord.length > 3 && guard++ < 10000) {
    let clipped = false;
    for (let i = 0; i < ord.length; i++) {
      const i0 = ord[(i + ord.length - 1) % ord.length], i1 = ord[i], i2 = ord[(i + 1) % ord.length];
      const a = p[i0], b = p[i1], c = p[i2];
      if (cross(a, b, c) <= 1e-9) continue;
      let ear = true;
      for (const k of ord) {
        if (k === i0 || k === i1 || k === i2) continue;
        if (inTri(p[k], a, b, c)) { ear = false; break; }
      }
      if (!ear) continue;
      out.push(i0, i1, i2);
      ord.splice(i, 1);
      clipped = true;
      break;
    }
    if (!clipped) return []; // self-intersecting or all collinear
  }
  if (ord.length === 3 && cross(p[ord[0]], p[ord[1]], p[ord[2]]) > 1e-9) out.push(ord[0], ord[1], ord[2]);
  return out;
}

export function maxSlopeDeg(verts, tris) {
  let m = 0;
  for (let i = 0; i < tris.length; i += 3) {
    const a = verts[tris[i]], b = verts[tris[i + 1]], c = verts[tris[i + 2]];
    const u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], v = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
    const n = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]];
    const len = Math.hypot(n[0], n[1], n[2]);
    if (len < 1e-9) continue;
    m = Math.max(m, Math.acos(Math.min(1, Math.abs(n[1]) / len)) * 180 / Math.PI);
  }
  return m;
}

/** Height of a patch's own surface at (x,z), or null. */
export function patchHeightAt(patch, x, z) {
  const v = patch.Verts, t = patch.Tris;
  for (let i = 0; i < t.length; i += 3) {
    const a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
    const d = (b[2] - c[2]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[2] - c[2]);
    if (Math.abs(d) < 1e-9) continue;
    const l1 = ((b[2] - c[2]) * (x - c[0]) + (c[0] - b[0]) * (z - c[2])) / d;
    const l2 = ((c[2] - a[2]) * (x - c[0]) + (a[0] - c[0]) * (z - c[2])) / d;
    const l3 = 1 - l1 - l2;
    if (l1 >= -1e-4 && l2 >= -1e-4 && l3 >= -1e-4) return l1 * a[1] + l2 * b[1] + l3 * c[1];
  }
  return null;
}

/**
 * Where the patch meets the old navmesh: O stands on the old mesh, I inside the patch (see PatchGate in the mod).
 * `base` is a TriIndex of the old mesh only.
 */
export function computeGates(patch, base, maxGates = 14) {
  const v = patch.Verts, n = v.length;
  if (n < 3 || !patch.Tris.length) return [];
  const ccw = polyArea(v) > 0;
  const found = [];
  for (let i = 0; i < n; i++) {
    const a = v[i], b = v[(i + 1) % n];
    const dx = b[0] - a[0], dz = b[2] - a[2], len = Math.hypot(dx, dz);
    if (len < 1e-6) continue;
    // inward normal: left of the edge for counter-clockwise polygons
    let nx = -dz / len, nz = dx / len;
    if (!ccw) { nx = -nx; nz = -nz; }
    const steps = Math.max(1, Math.ceil(len / 3));
    for (let s = 0; s <= steps; s++) {
      const t = steps === 1 ? (s === 0 ? 0.12 : 0.88) : Math.min(0.98, Math.max(0.02, s / steps));
      const sx = a[0] + dx * t, sz = a[2] + dz * t, sy = a[1] + (b[1] - a[1]) * t;
      const h = base.heightAt(sx, sz, sy - 0.6, sy + 0.6, sy);
      if (h === null || Math.abs(h - sy) > 0.6) continue;
      let ix = sx + nx * 0.8, iz = sz + nz * 0.8;
      if (!pointInPoly(ix, iz, v)) { ix = sx + nx * 0.4; iz = sz + nz * 0.4; }
      if (!pointInPoly(ix, iz, v)) continue;
      const iy = patchHeightAt(patch, ix, iz);
      found.push({ O: [sx, h, sz], I: [ix, iy === null ? sy : iy, iz] });
    }
  }
  if (found.length <= maxGates) return found;
  const out = [];
  for (let k = 0; k < maxGates; k++) out.push(found[Math.floor(k * found.length / maxGates)]);
  return out;
}

export function rotY(x, z, yawDeg) { // Unity Quaternion.Euler(0, yaw, 0) on a vector (x, 0, z)
  const r = yawDeg * Math.PI / 180, c = Math.cos(r), s = Math.sin(r);
  return [x * c + z * s, -x * s + z * c];
}

export function blockerCorners(b) {
  const hx = b.Size[0] / 2, hz = b.Size[2] / 2, out = [];
  for (const [sx, sz] of [[-1, -1], [1, -1], [1, 1], [-1, 1]]) {
    const [x, z] = rotY(sx * hx, sz * hz, b.Yaw);
    out.push([b.C[0] + x, b.C[1], b.C[2] + z]);
  }
  return out;
}

export const dist2 = (a, b) => Math.hypot(a[0] - b[0], a[2] - b[2]);
export const dist3 = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]);
