import re, io, json, subprocess
REPO = r"c:\github\ioSender"
h = io.open(r"c:\github\ioSender\ProposedPRs.html", encoding="utf-8").read()
def sh(a): return subprocess.run(["git"]+a, cwd=REPO, capture_output=True, text=True).stdout

toc = re.findall(r'<td class="n">(\d+)</td><td><a href="#pr\d+">([^<]+)</a></td><td class="mono">([^<]+)</td>', h)
base = {}
for m in re.finditer(r'id="pr(\d+)".*?compare/([^."]+)\.\.\.([^"]+)"', h, re.S):
    base[int(m.group(1))] = m.group(2).replace('%2F', '/')

branch_pr = {br: int(n) for n, t, br in toc}
master_tip = sh(["rev-parse", "master"]).strip()

def in_master(br):
    # pristine-gen PRs (1-8, 24) were merged into master already; their merge-base
    # with master is the old release point, not the master tip.
    mb = sh(["merge-base", "master", br]).strip()
    return mb != master_tip

# Hard dependencies among the COMPOSABLE (off-master) PRs that aren't derivable
# from branch base. (6->15 is moot: load-folder is already in master.)
SEMANTIC = {}

def clean_title(t):
    return (t.replace('&mdash;', '-').replace('&amp;', '&').replace('&rarr;', '->')
             .replace('&nbsp;', ' ').strip())

manifest = {"_note": "PR composer manifest. requires[] = hard deps (structural stacking + semantic/code). "
                     "Branches compose onto a fresh branch off master.",
            "prs": {}}
for n, title, br in toc:
    n = int(n)
    inm = in_master(br)
    req = set(SEMANTIC.get(n, []))
    b = base.get(n, "master")
    if b in branch_pr and not inm:  # stacked on another PR branch -> structural dep
        req.add(branch_pr[b])
    manifest["prs"][str(n)] = {
        "branch": br,
        "title": clean_title(title),
        "in_master": inm,           # True => already baked into master, not composable
        "requires": sorted(req),
    }
io.open(r"c:\github\ioSender\tools\prs.json", "w", encoding="utf-8", newline="\n").write(
    json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")
# print requires summary
print("requires (deps):")
for n, d in sorted(manifest["prs"].items(), key=lambda x: int(x[0])):
    if d["requires"]:
        print(f"  PR{n} ({d['branch']}) requires {d['requires']}")
print(f"total PRs: {len(manifest['prs'])}")
