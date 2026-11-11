"""LiveRaid Studio - local server of the map editor.

Serves the editor page (web/) and reads/writes the files of the game folder:
  <game>/BepInEx/plugins/LiveRaid/studio/dump/<map>.mesh.bin|meta.json   written by the mod in the first raid on a map (the navmesh)
  <game>/BepInEx/plugins/LiveRaid/studio/edits/<map>.json                 written here, read by the mod (and re-read while a raid runs)
  <game>/BepInEx/plugins/LiveRaid/{cache,learned,routes}/                 read only: loot places, places learned from the player, recorded routes
Drafts (autosave) and the demo map live in the workspace folder next to the program.
"""
import argparse
import csv
import glob
import http.server
import json
import os
import shutil
import socketserver
import struct
import subprocess
import sys
import threading
import time
import urllib.parse
import webbrowser

FROZEN = getattr(sys, "frozen", False)
HERE = os.path.dirname(os.path.abspath(sys.executable if FROZEN else __file__))
WEB = os.path.join(getattr(sys, "_MEIPASS", HERE), "web")
WORKSPACE = os.path.join(HERE, "workspace")
CONFIG = os.path.join(HERE, "studio_config.json")

MAPS = {
    "bigmap": "Таможня",
    "factory4_day": "Завод (день)",
    "factory4_night": "Завод (ночь)",
    "Sandbox": "Эпицентр",
    "Sandbox_high": "Эпицентр (высокий уровень)",
    "Interchange": "Развязка",
    "laboratory": "Лаборатория",
    "Labyrinth": "Лабиринт",
    "Lighthouse": "Маяк",
    "RezervBase": "Резерв",
    "Shoreline": "Берег",
    "TarkovStreets": "Улицы Таркова",
    "Woods": "Лес",
}
DEMO = "demo"
VERSION = "1.0.0"
_last_ping = [0.0]


def load_config():
    try:
        with open(CONFIG, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_config(cfg):
    with open(CONFIG, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)


def detect_game():
    cfg = load_config()
    candidates = [os.environ.get("LR_GAME"), cfg.get("gameDir"), os.path.dirname(HERE), r"C:\Games\SPT"]
    for c in candidates:
        if c and os.path.isdir(os.path.join(c, "BepInEx")):
            return os.path.abspath(c)
    return cfg.get("gameDir") or ""


GAME = detect_game()


def plugin_dir():
    return os.path.join(GAME, "BepInEx", "plugins", "LiveRaid") if GAME else ""


def dump_dir():
    return os.path.join(plugin_dir(), "studio", "dump")


def edits_dir():
    return os.path.join(plugin_dir(), "studio", "edits")


def draft_dir():
    return os.path.join(WORKSPACE, "drafts")


def safe_map(name):
    name = urllib.parse.unquote(name)
    if not name or any(c in name for c in '/\\:*?"<>|') or name.startswith("."):
        raise ValueError("bad map name")
    return name


def read_json(path, default=None):
    try:
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def mtime(path):
    try:
        return os.path.getmtime(path)
    except OSError:
        return 0


def write_atomic(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
    os.replace(tmp, path)


def mesh_path(m):
    if m == DEMO:
        return os.path.join(WORKSPACE, "demo", "demo.mesh.bin")
    return os.path.join(dump_dir(), m + ".mesh.bin")


def meta_path(m):
    if m == DEMO:
        return os.path.join(WORKSPACE, "demo", "demo.meta.json")
    return os.path.join(dump_dir(), m + ".meta.json")


def edits_path(m):
    if m == DEMO:
        return os.path.join(WORKSPACE, "demo", "demo.edits.json")
    return os.path.join(edits_dir(), m + ".json")


def list_maps():
    found = {}
    if GAME and os.path.isdir(dump_dir()):
        for p in glob.glob(os.path.join(dump_dir(), "*.mesh.bin")):
            found[os.path.basename(p)[: -len(".mesh.bin")]] = True
    result = []
    names = list(MAPS) + [m for m in found if m not in MAPS]
    for m in names:
        has = m in found and os.path.exists(meta_path(m))
        meta = read_json(meta_path(m), {}) if has else {}
        result.append({
            "id": m, "name": MAPS.get(m, m), "hasDump": has,
            "dumped": meta.get("Saved", ""), "tris": meta.get("Tris", 0),
            "hasEdits": os.path.exists(edits_path(m)), "editsTime": mtime(edits_path(m)),
        })
    if os.path.exists(mesh_path(DEMO)):
        result.insert(0, {"id": DEMO, "name": "Учебная карта (демо)", "hasDump": True, "dumped": "", "tris": 0,
                          "hasEdits": os.path.exists(edits_path(DEMO)), "editsTime": mtime(edits_path(DEMO))})
    return result


def load_trails(m):
    """The player's own walks (learned/trails): where he went, and which of it is not on the navmesh (a missing floor)."""
    out = []
    files = sorted(glob.glob(os.path.join(plugin_dir(), "learned", "trails", m + "_*.csv")))[-12:]
    for p in files:
        last = None
        try:
            with open(p, encoding="utf-8", newline="") as f:
                for row in csv.DictReader(f):
                    try:
                        x, y, z = float(row["x"]), float(row["y"]), float(row["z"])
                        on = 1 if row.get("on_navmesh", "1") == "1" else 0
                    except (KeyError, ValueError):
                        continue
                    if last and not on == 0 and (x - last[0]) ** 2 + (z - last[2]) ** 2 < 1.5 ** 2 and abs(y - last[1]) < 0.8:
                        continue
                    last = (x, y, z)
                    out.append([round(x, 2), round(y, 2), round(z, 2), on])
        except OSError:
            continue
    if len(out) > 80000:
        step = len(out) // 80000 + 1
        out = [p for i, p in enumerate(out) if i % step == 0 or p[3] == 0]
    return out


def load_info(m):
    meta = read_json(meta_path(m), {})
    info = {"map": m, "name": MAPS.get(m, m), "meta": meta, "pois": [], "learned": [], "legacyRoutes": [], "trails": []}
    if m != DEMO and GAME:
        cache = read_json(os.path.join(plugin_dir(), "cache", m + ".json"), {})
        info["pois"] = [[round(p["X"], 2), round(p["Y"], 2), round(p["Z"], 2), p.get("Kind", "Open"), round(p.get("W", 1), 2)] for p in cache.get("Pois", [])]
        learned = read_json(os.path.join(plugin_dir(), "learned", m + ".json"), {})
        info["learned"] = [[round(p["X"], 2), round(p["Y"], 2), round(p["Z"], 2), round(p.get("W", 1), 2)] for p in learned.get("Places", [])]
        legacy = read_json(os.path.join(plugin_dir(), "routes", m + ".json"), {})
        info["legacyRoutes"] = legacy.get("Routes", [])
        info["trails"] = load_trails(m)
    elif m == DEMO:
        info["pois"] = meta.get("Pois", [])
        info["learned"] = meta.get("Learned", [])
        info["trails"] = meta.get("Trails", [])
    edits = read_json(edits_path(m))
    draft = read_json(os.path.join(draft_dir(), m + ".json"))
    info["edits"] = edits
    info["editsTime"] = mtime(edits_path(m))
    info["draft"] = draft
    info["draftTime"] = mtime(os.path.join(draft_dir(), m + ".json"))
    return info


def export_edits(m, data):
    data["Format"] = "liveraid-edits/1"
    data["Map"] = m
    old = read_json(edits_path(m), {})
    data["Revision"] = int(old.get("Revision", 0)) + 1
    data["Saved"] = time.strftime("%Y-%m-%d %H:%M:%S")
    path = edits_path(m)
    if os.path.exists(path):
        bdir = os.path.join(os.path.dirname(path), "backup")
        os.makedirs(bdir, exist_ok=True)
        shutil.copy2(path, os.path.join(bdir, "%s_%s.json" % (m, time.strftime("%Y%m%d_%H%M%S"))))
        old_backups = sorted(glob.glob(os.path.join(bdir, m + "_*.json")))
        for p in old_backups[:-20]:
            try:
                os.remove(p)
            except OSError:
                pass
    write_atomic(path, json.dumps(data, ensure_ascii=False, indent=1))
    try:  # what was autosaved is in the game now
        os.remove(os.path.join(draft_dir(), m + ".json"))
    except OSError:
        pass
    return {"path": path, "revision": data["Revision"]}


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "LiveRaidStudio/" + VERSION

    def log_message(self, fmt, *args):
        pass

    def send(self, code, body, ctype="application/json; charset=utf-8", extra=None):
        if isinstance(body, str):
            body = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def json(self, obj, code=200):
        self.send(code, json.dumps(obj, ensure_ascii=False))

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n else b""

    def do_GET(self):
        try:
            self.route(self.command)
        except ValueError as e:
            self.json({"error": str(e)}, 400)
        except Exception as e:  # keep the server alive whatever a request does
            self.json({"error": "%s: %s" % (type(e).__name__, e)}, 500)

    do_PUT = do_POST = do_GET

    def route(self, method):
        path = urllib.parse.urlparse(self.path).path
        if path.startswith("/api/"):
            return self.api(method, path[5:])
        if path == "/":
            path = "/index.html"
        full = os.path.normpath(os.path.join(WEB, *path.lstrip("/").split("/")))
        if not full.startswith(os.path.normpath(WEB)) or not os.path.isfile(full):
            return self.send(404, "not found", "text/plain")
        ctype = {".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8",
                 ".svg": "image/svg+xml", ".png": "image/png", ".json": "application/json"}.get(os.path.splitext(full)[1], "application/octet-stream")
        with open(full, "rb") as f:
            self.send(200, f.read(), ctype)

    def api(self, method, path):
        global GAME
        parts = path.strip("/").split("/")
        if parts == ["ping"]:
            _last_ping[0] = time.time()
            return self.json({"ok": True})
        if parts == ["state"]:
            return self.json({"gameDir": GAME, "pluginDir": plugin_dir(), "hasPlugin": bool(GAME) and os.path.isdir(plugin_dir()),
                              "workspace": WORKSPACE, "version": VERSION})
        if parts == ["game"] and method == "POST":
            d = json.loads(self.body() or b"{}")
            g = os.path.abspath(d.get("gameDir", ""))
            if not os.path.isdir(os.path.join(g, "BepInEx")):
                return self.json({"error": "В этой папке нет BepInEx - это не папка игры SPT"}, 400)
            GAME = g
            cfg = load_config()
            cfg["gameDir"] = g
            save_config(cfg)
            return self.json({"gameDir": GAME})
        if parts == ["maps"]:
            return self.json(list_maps())
        if len(parts) == 3 and parts[0] == "map":
            m = safe_map(parts[1])
            what = parts[2]
            if what == "info":
                return self.json(load_info(m))
            if what == "mesh":
                p = mesh_path(m)
                if not os.path.exists(p):
                    return self.json({"error": "У этой карты нет выгрузки навмеша: проведите на ней один рейд с модом"}, 404)
                with open(p, "rb") as f:
                    return self.send(200, f.read(), "application/octet-stream")
            if what == "draft" and method == "PUT":
                d = json.loads(self.body())
                write_atomic(os.path.join(draft_dir(), m + ".json"), json.dumps(d, ensure_ascii=False))
                return self.json({"ok": True})
            if what == "export" and method == "POST":
                if m != DEMO and not (GAME and os.path.isdir(plugin_dir())):
                    return self.json({"error": "Не найдена папка игры (BepInEx/plugins/LiveRaid). Укажите её в настройках"}, 400)
                return self.json(export_edits(m, json.loads(self.body())))
        self.json({"error": "unknown request " + path}, 404)


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def find_browser():
    for base in (os.environ.get("ProgramFiles(x86)"), os.environ.get("ProgramFiles"), os.environ.get("LocalAppData")):
        if not base:
            continue
        for rel in (r"Microsoft\Edge\Application\msedge.exe", r"Google\Chrome\Application\chrome.exe"):
            p = os.path.join(base, rel)
            if os.path.exists(p):
                return p
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=0)
    ap.add_argument("--no-browser", action="store_true")
    ap.add_argument("--keep", action="store_true", help="do not stop when the window is closed")
    args = ap.parse_args()

    os.makedirs(WORKSPACE, exist_ok=True)
    if not os.path.exists(mesh_path(DEMO)):
        try:
            import make_demo
            make_demo.build(WORKSPACE)
        except Exception as e:  # the demo map is a convenience
            print("demo map not built:", e)
    srv = None
    for port in ([args.port] if args.port else [8777, 8778, 8779, 0]):
        try:
            srv = Server(("127.0.0.1", port), Handler)
            break
        except OSError:
            continue
    if srv is None:
        print("no free port")
        return 1
    port = srv.server_address[1]
    url = "http://127.0.0.1:%d/" % port
    print("LiveRaid Studio:", url, "| game:", GAME or "(not found)")
    threading.Thread(target=srv.serve_forever, daemon=True).start()

    if not args.no_browser:
        browser = find_browser()
        opened = False
        if browser:
            try:
                profile = os.path.join(WORKSPACE, "browser")
                subprocess.Popen([browser, "--app=" + url, "--window-size=1600,950", "--user-data-dir=" + profile, "--no-first-run"])
                opened = True
            except OSError:
                pass
        if not opened:
            webbrowser.open(url)

    # Closing the window stops the program: the page pings every few seconds
    started = time.time()
    try:
        while True:
            time.sleep(1)
            if args.keep:
                continue
            if _last_ping[0] and time.time() - _last_ping[0] > 20:
                break
            if not _last_ping[0] and time.time() - started > 90:
                break
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
