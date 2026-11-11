"""Builds the demo map of LiveRaid Studio (workspace/demo): a field with a ditch, a two-floor building with a ramp, doors, exits, places.
It lets the editor be tried without a dump from the game. Run: python make_demo.py [workspace-dir]"""
import json
import math
import os
import random
import struct
import sys

verts, index, tris, areas = [], {}, [], []


def build(workspace):
    for lst in (verts, tris, areas):
        lst.clear()
    index.clear()
    OUT = os.path.join(workspace, "demo")
    os.makedirs(OUT, exist_ok=True)
    def vid(x, y, z):
        k = (round(x * 100), round(y * 100), round(z * 100))
        if k not in index:
            index[k] = len(verts)
            verts.append((x, y, z))
        return index[k]


    def quad(x0, z0, x1, z1, y0, y1=None, y2=None, y3=None):
        """A cell with corner heights (x0z0, x1z0, x1z1, x0z1)."""
        h = [y0, y0 if y1 is None else y1, y0 if y2 is None else y2, y0 if y3 is None else y3]
        a, b, c, d = vid(x0, h[0], z0), vid(x1, h[1], z0), vid(x1, h[2], z1), vid(x0, h[3], z1)
        tris.extend([a, d, c, a, c, b])
        areas.extend([0, 0])


    random.seed(7)
    S = 2.0
    # Field -100..100, ditch x in 20..28 with no navmesh, building footprint -30..0 x -12..12 (cut out of the field, its own floor)
    for ix in range(-50, 50):
        for iz in range(-50, 50):
            x0, z0 = ix * S, iz * S
            cx, cz = x0 + S / 2, z0 + S / 2
            if 20 <= cx <= 28:
                continue
            if -30 <= cx <= 0 and -12 <= cz <= 12:
                continue
            y = 0.4 * math.sin(cx / 25.0) + 0.25 * math.cos(cz / 18.0)
            quad(x0, z0, x0 + S, z0 + S, y)

    # Building: ground floor y=0.2 (everything inside), a closet wall gap at x=-30 (door at z 0..2)
    for ix in range(-15, 0):
        for iz in range(-6, 6):
            quad(ix * S, iz * S, ix * S + S, iz * S + S, 0.2)

    # Second floor y=3.4 over x -30..-8, ramp (3 m wide) from the ground floor at x -8..0 rising to the second floor, z 6..9
    for ix in range(-15, -4):
        for iz in range(-6, 6):
            quad(ix * S, iz * S, ix * S + S, iz * S + S, 3.4)

    # Ramp: from x=-8 (y 3.4) down to x=0 (y 0.2) along z -4..-1, in 4 steps of 2 m; the floor above is cut there
    steps = 4
    # Remove the part of the second floor over the ramp is not needed: the ramp ends at the plate edge (x=-8..0 has no plate)
    for k in range(steps):
        xa, xb = -8 + 2 * k, -8 + 2 * (k + 1)
        ya = 3.4 - (3.4 - 0.2) * k / steps
        yb = 3.4 - (3.4 - 0.2) * (k + 1) / steps
        for iz in (-2, -1):
            quad(xa, iz * S, xb, iz * S + S, ya, yb, yb, ya)

    # A roof-top island not connected to anything: the player can climb it, the navmesh does not know the way
    for ix in range(8, 12):
        for iz in range(-4, 0):
            quad(ix * S, iz * S, ix * S + S, iz * S + S, 2.2)

    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "demo.mesh.bin"), "wb") as f:
        f.write(b"LRMESH1\0")
        f.write(struct.pack("<II", len(verts), len(tris)))
        for v in verts:
            f.write(struct.pack("<3f", *v))
        f.write(struct.pack("<%dI" % len(tris), *tris))
        f.write(bytes(areas))

    xs = [v[0] for v in verts]
    ys = [v[1] for v in verts]
    zs = [v[2] for v in verts]
    pois = []
    for _ in range(60):
        x, z = random.uniform(-90, 90), random.uniform(-90, 90)
        if 18 < x < 30:
            continue
        pois.append([round(x, 2), 0.2, round(z, 2), random.choice(["Container", "LooseLoot", "Open", "LootSpot"]), round(random.uniform(0.2, 4), 2)])
    for _ in range(14):
        pois.append([round(random.uniform(-28, -9), 2), 3.4, round(random.uniform(-10, 10), 2), "Container", round(random.uniform(1, 5), 2)])

    trails = []
    # The player walks across the ditch (a jump) and up the roof island: both off the navmesh
    for i in range(0, 60):
        trails.append([round(-40 + i * 1.4, 2), 0.3, 40.0, 1])
    for i in range(0, 10):
        trails.append([round(18 + i * 1.2, 2), 0.1 + 0.3 * math.sin(i / 3), 40.0, 0])
    for i in range(0, 12):
        trails.append([round(30 + i * 1.5, 2), 0.3, 40.0, 1])
    for i in range(0, 8):
        trails.append([round(16 + i * 0.7, 2), 0.2 + i * 0.25, -6.0, 0 if i > 2 else 1])

    meta = {
        "Format": "liveraid-dump/1", "Map": "demo", "Saved": "", "Mod": "demo",
        "Verts": len(verts), "Tris": len(tris) // 3,
        "Min": [min(xs), min(ys), min(zs)], "Max": [max(xs), max(ys), max(zs)],
        "Doors": [
            {"Id": "door_demo_1", "X": -30, "Y": 0.2, "Z": 1, "State": "Open", "Key": ""},
            {"Id": "door_demo_2", "X": -15, "Y": 3.4, "Z": 12, "State": "Locked", "Key": "demo-key"},
        ],
        "Exfils": [
            {"Name": "Demo Gate", "Kind": "pmc", "X": -92, "Y": 0.1, "Z": 0},
            {"Name": "Demo Road", "Kind": "shared", "X": 92, "Y": 0.2, "Z": 10},
        ],
        "Spawns": [{"X": -80, "Y": 0.1, "Z": -80}, {"X": 80, "Y": 0.1, "Z": 80}, {"X": 70, "Y": 0.2, "Z": -70}],
        "Pois": pois, "Learned": [[16, 0.2, -6, 2.5], [36, 0.3, 40, 1.5]], "Trails": trails,
    }
    with open(os.path.join(OUT, "demo.meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False)
    print("demo map:", len(verts), "vertices,", len(tris) // 3, "triangles ->", OUT)


if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    build(sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "workspace"))
