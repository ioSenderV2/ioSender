#!/usr/bin/env python3
"""
apply-prs - compose a buildable branch from selected fork PRs (multi-fork).

Usage:
    python tools/apply-prs.py <new-branch> <pr> [<pr> ...]            # ioSender (default fork)
    python tools/apply-prs.py my-build --fork sim 1 3 5              # the Simulator fork
    python tools/apply-prs.py my-build --fork sim 2 --run           # compose, build, launch
    python tools/apply-prs.py --check --fork sim 3 5                 # overlaps only, no branch
    python tools/apply-prs.py --list [--fork sim]                    # list a fork's PRs

Forks are declared in tools/forks.json (repo path, base branch, manifest, build, union
drivers). For each fork the named branch is created off its base and the selected PRs
(+ their dependency closure, topologically ordered) are merged in. Append-only files
named in the fork's "union" list merge with a union driver so they never conflict; other
overlaps are reported and the merge aborted for a manual resolve. A pre-flight (git
merge-tree, no checkout) reports same-line overlaps before composing.

ioSender's master already integrates PRs 1-8 and 24 (in_master baseline); the Simulator's
master is pristine, so all its PRs are composable. The branch starts at the fork's base,
so old/standalone-unbuildable branches still compose and build here.
"""
import argparse, json, os, subprocess, sys, fnmatch

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
IO_ROOT = os.path.dirname(SCRIPT_DIR)                 # the ioSender repo (where this tool lives)
FORKS_JSON = os.path.join(SCRIPT_DIR, "forks.json")
MSBUILD = r"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"

# active fork context (set in main)
REPO = IO_ROOT
BASE = "master"
UNION = []


def git(*args, check=True, capture=True):
    r = subprocess.run(["git"] + list(args), cwd=REPO, capture_output=capture, text=True)
    if check and r.returncode != 0:
        sys.stderr.write((r.stderr or r.stdout or "") + "\n")
        raise SystemExit(f"git {' '.join(args)} failed ({r.returncode})")
    return r


def load_manifest(path):
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)["prs"]


def is_union(path):
    p = path.replace("\\", "/")
    return any(fnmatch.fnmatch(p, g) for g in UNION)


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
    def cdeps(m):
        return [r for r in prs[str(m)]["requires"] if not prs[str(r)]["in_master"]]
    order, placed = [], set()
    while len(order) < len(need):
        ready = sorted(m for m in need if m not in placed and set(cdeps(m)) <= placed)
        if not ready:
            raise SystemExit(f"dependency cycle among {sorted(need - placed)}")
        for m in ready:
            order.append(m); placed.add(m)
    return order


def _requires_rel(a, b, prs):
    def reqs(n, acc):
        for r in prs[str(n)]["requires"]:
            if r not in acc:
                acc.add(r); reqs(r, acc)
        return acc
    return a in reqs(b, set()) or b in reqs(a, set())


def preflight(order, prs):
    """Pairwise overlap check (git merge-tree, no checkout). Returns {(a,b): [non-union files]}.
    Skips dependency pairs (merged in order) and union-handled overlaps."""
    import itertools, re
    fline = re.compile(r'^\s+(?:our|their|base)\s+\d+\s+[0-9a-f]+\s+(.+)$')
    hits = {}
    for a, b in itertools.combinations(order, 2):
        if _requires_rel(a, b, prs):
            continue
        out = subprocess.run(["git", "merge-tree", BASE,
                              prs[str(a)]["branch"], prs[str(b)]["branch"]],
                             cwd=REPO, capture_output=True).stdout.decode("utf-8", "replace")
        cur, files = None, set()
        for ln in out.splitlines():
            m = fline.match(ln)
            if m:
                cur = m.group(1).strip()
            elif "<<<<<<<" in ln and cur and not is_union(cur):
                files.add(cur)
        if files:
            hits[(a, b)] = sorted(files)
    return hits


def ensure_clean_tree():
    dirty = [l for l in git("status", "--porcelain").stdout.splitlines()
             if l and not l.startswith("??")]
    if dirty:
        raise SystemExit("working tree has uncommitted changes - commit/stash first:\n  "
                         + "\n  ".join(dirty))


def run_build(cfg):
    """Returns (ok, [error lines])."""
    b = cfg["build"]; t = b["type"]
    if t == "msbuild":
        if not os.path.exists(MSBUILD):
            print(f"  (MSBuild not found at {MSBUILD}; skipping build)"); return None, []
        r = subprocess.run([MSBUILD, b["solution"], "-t:Build", "-p:Configuration=Release",
                            "-m", "-v:minimal", "-nologo"], cwd=REPO, capture_output=True, text=True)
        errs = [l.strip() for l in r.stdout.splitlines() if ": error" in l.lower()]
        return r.returncode == 0, errs[:8]
    if t == "cmake":
        bdir = os.path.join(REPO, b.get("dir", "build"))
        cfgcmd = ["cmake", "-S", REPO, "-B", bdir]
        if b.get("generator"):
            cfgcmd += ["-G", b["generator"]]
        c = subprocess.run(cfgcmd, cwd=REPO, capture_output=True, text=True)
        if c.returncode != 0:
            return False, [l.strip() for l in (c.stdout + c.stderr).splitlines() if "error" in l.lower()][:8]
        r = subprocess.run(["cmake", "--build", bdir, "--clean-first", "--parallel"],
                           cwd=REPO, capture_output=True, text=True)
        errs = [l.strip() for l in (r.stdout + r.stderr).splitlines() if "error" in l.lower()]
        return r.returncode == 0, errs[:8]
    raise SystemExit(f"unknown build type {t!r}")


def main():
    global REPO, BASE, UNION
    ap = argparse.ArgumentParser(description="Compose a buildable branch from selected fork PRs.")
    ap.add_argument("target", nargs="?", help="name of the new branch to create (must not exist)")
    ap.add_argument("prs", nargs="*", type=int, help="PR numbers to include")
    ap.add_argument("--fork", default="iosender", help="which fork (see tools/forks.json)")
    ap.add_argument("--no-build", action="store_true", help="skip the verification build")
    ap.add_argument("--run", action="store_true", help="launch the built exe after a successful build")
    ap.add_argument("--force", action="store_true", help="replace the target branch if it exists")
    ap.add_argument("--check", action="store_true", help="report overlaps for the set and exit (no branch)")
    ap.add_argument("--list", action="store_true", help="list known PRs and exit")
    args = ap.parse_args()

    forks = json.load(open(FORKS_JSON, encoding="utf-8"))
    if args.fork not in forks or args.fork.startswith("_"):
        ap.error(f"unknown fork {args.fork!r}; known: " + ", ".join(k for k in forks if not k.startswith("_")))
    cfg = forks[args.fork]
    REPO = os.path.abspath(os.path.join(IO_ROOT, cfg["repo"]))
    BASE = cfg["base"]
    UNION = cfg.get("union", [])
    prs = load_manifest(os.path.join(REPO, cfg["manifest"]))

    if args.list:
        print(f"[{args.fork}]  {REPO}")
        for n in sorted(prs, key=int):
            d = prs[n]
            tag = "in master" if d["in_master"] else "composable"
            dep = f"  (requires {d['requires']})" if d["requires"] else ""
            print(f"  PR{int(n):>2}  [{tag:<10}] {d['branch']:<32} {d['title']}{dep}")
        return

    if args.check and args.target is not None:
        try:
            args.prs = [int(args.target)] + args.prs
        except ValueError:
            ap.error("--check takes PR numbers only")
        args.target = None
    if not args.prs or (not args.check and not args.target):
        ap.error("usage: apply-prs <new-branch> [--fork F] <pr> [<pr> ...]   (or --check, --list)")

    name = args.target
    baseline = [n for n in args.prs if str(n) in prs and prs[str(n)]["in_master"]]
    for n in baseline:
        print(f"  PR{n} ({prs[str(n)]['branch']}) is already in {BASE} - skipping (baseline).")
    selectable = [n for n in args.prs if not (str(n) in prs and prs[str(n)]["in_master"])]
    if not selectable:
        raise SystemExit(f"nothing to compose - all requested PRs are already in {BASE}.")

    order = closure(selectable, prs)
    added = [n for n in order if n not in args.prs]
    print(f"\nFork      : {args.fork}  ({REPO})")
    print(f"Requested : {sorted(args.prs)}")
    if added:
        print(f"+ deps    : {added}")
    print("Merge order:")
    for n in order:
        print(f"   PR{n:>2}  {prs[str(n)]['branch']}")

    overlaps = preflight(order, prs)
    if overlaps:
        print("\n!! these selected PRs overlap on the same lines (you'll resolve a hunk per file):")
        for (a, b), fs in sorted(overlaps.items()):
            print(f"   PR{a} x PR{b}: " + ", ".join(f.split('/')[-1] for f in fs))
    else:
        print("\nNo same-line overlaps in this set - composes cleanly.")
    if args.check:
        return
    print(f"\nBranch    : {name}  (off {BASE})\n")

    ensure_clean_tree()
    start = git("rev-parse", "--abbrev-ref", "HEAD").stdout.strip()
    if git("rev-parse", "--verify", "--quiet", name, check=False).returncode == 0:
        if not args.force:
            raise SystemExit(f"branch '{name}' already exists (pick another name or use --force).")
        git("branch", "-D", name)
    git("checkout", "-q", "-B", name, BASE)

    if UNION:
        ga = os.path.join(REPO, ".gitattributes")
        text = open(ga, encoding="utf-8").read() if os.path.exists(ga) else ""
        addlines = [f"{g} merge=union" for g in UNION if f"{g} merge=union" not in text]
        if addlines:
            with open(ga, "a", encoding="utf-8", newline="\n") as fh:
                if text and not text.endswith("\n"):
                    fh.write("\n")
                fh.write("\n".join(addlines) + "\n")
            git("add", ".gitattributes")
            git("commit", "-q", "-m", "apply-prs: union-merge append-only files")
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

    if cfg.get("submodules"):
        git("submodule", "update", "--init", "--recursive", check=False)

    print(f"\nComposed branch '{name}' is ready ({len(order)} PR(s) on top of {BASE}).")
    if args.no_build:
        print(f"  git checkout {name}   # then build")
        return
    print("Building ...")
    ok, errs = run_build(cfg)
    if ok is None:
        return
    if not ok:
        print("BUILD FAILED:")
        for e in errs:
            print("   " + e)
        print("\nA missing-symbol error usually means an undeclared dependency - add it to "
              "the requires[] of the offending PR in this fork's prs.json, then re-run.")
        raise SystemExit(1)
    exe = os.path.join(REPO, cfg["build"].get("exe", ""))
    print(f"BUILD OK -> {exe}")
    if args.run and exe and os.path.exists(exe):
        print("Launching ...")
        subprocess.Popen([exe], cwd=os.path.dirname(exe))


if __name__ == "__main__":
    main()
