#!/usr/bin/env python3
"""
apply-prs - compose a buildable branch from selected fork PRs.

Usage:
    python tools/apply-prs.py <new-branch> <pr> [<pr> ...]   # compose those PRs (+ deps)
    python tools/apply-prs.py my-build 15 16 21              # 16 auto-pulls 15
    python tools/apply-prs.py my-build 25 --run              # compose, build, launch
    python tools/apply-prs.py --list                         # list known PRs

<new-branch> is created off master and must not already exist (use --force to replace).
The dependency closure is resolved from tools/prs.json, topologically ordered, and each
PR branch is merged in order. The locale CSVs use a `merge=union` driver so their
append-only rows never conflict; any other conflict is reported and the merge aborted
so you can resolve it by hand.

master already integrates PRs 1-8 and 24 (the "in_master" baseline); requesting one of
those just prints a note and skips it. Because the branch starts at master, the composed
build inherits the RP.Math reference + App.config, so it builds even though some old PR
branches don't build standalone.
"""
import argparse, json, os, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(ROOT, "tools", "prs.json")
MSBUILD = r"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
SOLUTION = "ioSender XL/ioSender XL.sln"
MASTER = "master"
UNION_LINE = "Locale/**/*.csv merge=union"


def git(*args, check=True, capture=True):
    r = subprocess.run(["git"] + list(args), cwd=ROOT, capture_output=capture, text=True)
    if check and r.returncode != 0:
        sys.stderr.write((r.stderr or r.stdout or "") + "\n")
        raise SystemExit(f"git {' '.join(args)} failed ({r.returncode})")
    return r


def load_manifest():
    with open(MANIFEST, encoding="utf-8") as fh:
        return json.load(fh)["prs"]


def closure(requested, prs):
    """Composable PRs needed (skipping in_master deps), in topo order (deps first)."""
    need, stack = set(), [n for n in requested if not prs[str(n)]["in_master"]]
    while stack:
        n = stack.pop()
        if n in need:
            continue
        if str(n) not in prs:
            raise SystemExit(f"unknown PR {n} (see --list)")
        need.add(n)
        for r in prs[str(n)]["requires"]:
            if not prs[str(r)]["in_master"]:
                stack.append(r)
    def cdeps(m):  # composable deps still in play
        return [r for r in prs[str(m)]["requires"] if not prs[str(r)]["in_master"]]
    order, placed = [], set()
    while len(order) < len(need):
        ready = sorted(m for m in need if m not in placed and set(cdeps(m)) <= placed)
        if not ready:
            raise SystemExit(f"dependency cycle among {sorted(need - placed)}")
        for m in ready:
            order.append(m); placed.add(m)
    return order


def ensure_clean_tree():
    dirty = [l for l in git("status", "--porcelain").stdout.splitlines()
             if l and not l.startswith("??")]
    if dirty:
        raise SystemExit("working tree has uncommitted changes - commit/stash first:\n  "
                         + "\n  ".join(dirty))


def main():
    ap = argparse.ArgumentParser(description="Compose a buildable branch from selected fork PRs.")
    ap.add_argument("target", nargs="?", help="name of the new branch to create (must not exist)")
    ap.add_argument("prs", nargs="*", type=int, help="PR numbers to include")
    ap.add_argument("--no-build", action="store_true", help="skip the verification build")
    ap.add_argument("--run", action="store_true", help="launch ioSender.exe after a successful build")
    ap.add_argument("--force", action="store_true", help="replace the target branch if it exists")
    ap.add_argument("--list", action="store_true", help="list known PRs and exit")
    args = ap.parse_args()
    prs = load_manifest()

    if args.list:
        for n in sorted(prs, key=int):
            d = prs[n]
            tag = "in master" if d["in_master"] else "composable"
            dep = f"  (requires {d['requires']})" if d["requires"] else ""
            print(f"  PR{int(n):>2}  [{tag:<10}] {d['branch']:<32} {d['title']}{dep}")
        return
    if not args.target or not args.prs:
        ap.error("usage: apply-prs <new-branch> <pr> [<pr> ...]   (or --list)")

    name = args.target
    baseline = [n for n in args.prs if str(n) in prs and prs[str(n)]["in_master"]]
    for n in baseline:
        print(f"  PR{n} ({prs[str(n)]['branch']}) is already in master - skipping (baseline).")
    selectable = [n for n in args.prs if not (str(n) in prs and prs[str(n)]["in_master"])]
    if not selectable:
        raise SystemExit("nothing to compose - all requested PRs are already in master.")

    order = closure(selectable, prs)
    added = [n for n in order if n not in args.prs]
    print(f"\nRequested : {sorted(args.prs)}")
    if added:
        print(f"+ deps    : {added}")
    print("Merge order:")
    for n in order:
        print(f"   PR{n:>2}  {prs[str(n)]['branch']}")
    print(f"Branch    : {name}  (off {MASTER})\n")

    ensure_clean_tree()
    start = git("rev-parse", "--abbrev-ref", "HEAD").stdout.strip()
    if git("rev-parse", "--verify", "--quiet", name, check=False).returncode == 0:
        if not args.force:
            raise SystemExit(f"branch '{name}' already exists (pick another name or use --force).")
        git("branch", "-D", name)
    git("checkout", "-q", "-B", name, MASTER)

    ga = os.path.join(ROOT, ".gitattributes")
    text = open(ga, encoding="utf-8").read() if os.path.exists(ga) else ""
    if UNION_LINE not in text:
        with open(ga, "a", encoding="utf-8", newline="\n") as fh:
            if text and not text.endswith("\n"):
                fh.write("\n")
            fh.write(UNION_LINE + "\n")
        git("add", ".gitattributes")
        git("commit", "-q", "-m", "apply-prs: union-merge locale CSVs")
    git("config", "rerere.enabled", "true")

    for n in order:
        br = prs[str(n)]["branch"]
        r = git("merge", "--no-edit", br, check=False)
        if r.returncode != 0:
            conflicts = git("diff", "--name-only", "--diff-filter=U").stdout.strip()
            git("merge", "--abort", check=False)
            git("checkout", "-q", start)
            git("branch", "-D", name, check=False)
            msg = "\n     ".join(conflicts.splitlines()) if conflicts else r.stderr
            raise SystemExit(f"\n!! PR{n} ({br}) conflicts with an already-merged PR in:\n     {msg}\n"
                             "These edit the same lines. Resolve manually, or compose a different set.")
        print(f"   merged PR{n:>2}  {br}")

    print(f"\nComposed branch '{name}' is ready ({len(order)} PR(s) on top of master).")
    if args.no_build:
        print(f"  git checkout {name}   # build in VS / msbuild")
        return
    if not os.path.exists(MSBUILD):
        print(f"  (MSBuild not found at {MSBUILD}; skipping build)")
        return
    print("Building Release ...")
    b = subprocess.run([MSBUILD, SOLUTION, "-t:Build", "-p:Configuration=Release",
                        "-m", "-v:minimal", "-nologo"], cwd=ROOT, capture_output=True, text=True)
    if b.returncode != 0:
        print("BUILD FAILED:")
        for e in [l for l in b.stdout.splitlines() if ": error" in l.lower()][:8]:
            print("   " + e.strip())
        print("\nA missing-symbol error usually means an undeclared dependency - add it to "
              "the requires[] of the offending PR in tools/prs.json, then re-run.")
        raise SystemExit(1)
    exe = os.path.join(ROOT, "ioSender XL", "ioSender XL", "bin", "Release", "ioSender.exe")
    print(f"BUILD OK -> {exe}")
    if args.run and os.path.exists(exe):
        print("Launching ...")
        subprocess.Popen([exe], cwd=os.path.dirname(exe))


if __name__ == "__main__":
    main()
