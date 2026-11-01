"""One command after a raid: what worked (verify.py), where bots stalled (stalls.py) and the raid overview (raidlog.py), for the newest trace.

    python -X utf8 -I tools/review.py [trace] [--short]

--short leaves out raidlog.py's long sections (only its errors and summaries are printed).
"""
import os
import subprocess
import sys

TOOLS = os.path.dirname(os.path.abspath(__file__))


def run(script, args):
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    r = subprocess.run([sys.executable, "-X", "utf8", "-I", os.path.join(TOOLS, script)] + args, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", env=env)
    return (r.stdout or "") + (r.stderr or "")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    short = "--short" in sys.argv
    print("#" * 20, "что сработало (verify.py)", "#" * 20)
    print(run("verify.py", args))
    print("#" * 20, "простои и пинг-понги (stalls.py)", "#" * 20)
    print(run("stalls.py", args))
    print("#" * 20, "обзор рейда (raidlog.py)", "#" * 20)
    out = run("raidlog.py", args)
    if short:
        keep = []
        on = False
        for ln in out.splitlines():
            if ln.startswith("== "):
                on = ln.startswith("== errors") or ln.startswith("== summaries") or ln.startswith("== health")
            if on or ln.startswith("file:"):
                keep.append(ln)
        out = "\n".join(keep)
    print(out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
