#!/usr/bin/env bash
#
# sync-upstream.sh - keep the fork's pr/* branches current with upstream master.
#
# When upstream (terjeio/ioSender) releases a new version, the fork's master and
# every pr/* feature branch need to pick up those changes. Most PRs touch files
# upstream did NOT change and rebase cleanly; only the few whose files OVERLAP the
# upstream delta need hand attention. This script finds that out for you.
#
# Default (report only, non-destructive):
#   - fetches upstream + fork
#   - prints the upstream delta (new commits + changed files since the merge-base)
#   - classifies each pr/* branch as CLEAN (no file overlap -> rebases automatically)
#     or AFFECTED (shares files with the upstream delta -> resolve by hand)
#
# --apply : actually do it - merge upstream into master, then rebase each CLEAN,
#           non-stacked pr/* branch onto the updated master. AFFECTED and stacked
#           branches are left untouched with instructions to handle them manually.
# --push  : after --apply, push master and the rebased branches (force-with-lease).
#
# Env overrides: UPSTREAM_REMOTE (default origin), FORK_REMOTE (default fork),
#                MASTER (default master).
#
# Examples:
#   tools/sync-upstream.sh                 # dry-run report
#   tools/sync-upstream.sh --apply         # update master + rebase clean branches
#   tools/sync-upstream.sh --apply --push  # ...and push the results

set -euo pipefail

UPSTREAM_REMOTE=${UPSTREAM_REMOTE:-origin}
FORK_REMOTE=${FORK_REMOTE:-fork}
MASTER=${MASTER:-master}
APPLY=0
PUSH=0

for a in "$@"; do
  case "$a" in
    --apply) APPLY=1 ;;
    --push)  PUSH=1 ;;
    -h|--help)
      sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) echo "unknown option: $a (try --help)" >&2; exit 2 ;;
  esac
done

note() { printf '\n=== %s ===\n' "$*"; }

# Restore the caller's branch on exit (rebases/checkouts move HEAD around).
ORIG_BRANCH=$(git rev-parse --abbrev-ref HEAD)
restore() { git checkout -q "$ORIG_BRANCH" 2>/dev/null || true; }
trap restore EXIT

# A clean working tree is required for any branch switching.
if ! git diff --quiet || ! git diff --cached --quiet; then
  echo "Working tree is dirty - commit or stash first." >&2
  exit 1
fi

note "Fetching $UPSTREAM_REMOTE + $FORK_REMOTE"
git fetch --prune "$UPSTREAM_REMOTE"
git fetch --prune "$FORK_REMOTE" || true

# Upstream delta = commits/files upstream has that the fork's master does not yet.
# Computed from the MERGE-BASE (not 'master vs upstream/master'), so the fork's own
# commits on top of master are NOT mistaken for upstream changes.
MB=$(git merge-base "$MASTER" "$UPSTREAM_REMOTE/$MASTER")
DELTA_COUNT=$(git rev-list --count "$MB..$UPSTREAM_REMOTE/$MASTER")

if [ "$DELTA_COUNT" -eq 0 ]; then
  note "Up to date"
  echo "$MASTER already contains all of $UPSTREAM_REMOTE/$MASTER - nothing to sync."
  exit 0
fi

note "Upstream delta: $DELTA_COUNT new commit(s) on $UPSTREAM_REMOTE/$MASTER"
git --no-pager log --oneline "$MB..$UPSTREAM_REMOTE/$MASTER"

DELTA_FILES=$(git diff --name-only "$MB" "$UPSTREAM_REMOTE/$MASTER" | sort -u)
echo
echo "Files changed upstream:"
echo "$DELTA_FILES" | sed 's/^/  /'

PR_BRANCHES=$(git for-each-ref --format='%(refname:short)' 'refs/heads/pr/*')

# Detect a stacked PR's parent: the pr/* branch that is an ancestor of B and sits
# furthest from master (i.e. the closest/deepest parent). None -> master.
detect_parent() {
  local B=$1 best=$MASTER bestn=-1 P n
  for P in $PR_BRANCHES; do
    [ "$P" = "$B" ] && continue
    if git merge-base --is-ancestor "$P" "$B"; then
      n=$(git rev-list --count "$MASTER..$P")
      if [ "$n" -gt "$bestn" ]; then bestn=$n; best=$P; fi
    fi
  done
  echo "$best"
}

CLEAN_BRANCHES=()
AFFECTED_BRANCHES=()

note "Per-branch status"
for B in $PR_BRANCHES; do
  PARENT=$(detect_parent "$B")
  PR_FILES=$(git diff --name-only "$PARENT...$B" | sort -u)
  OVERLAP=$(comm -12 <(echo "$DELTA_FILES") <(echo "$PR_FILES") || true)
  STACK_NOTE=""
  [ "$PARENT" != "$MASTER" ] && STACK_NOTE=" (stacked on $PARENT)"
  if [ -n "$OVERLAP" ]; then
    AFFECTED_BRANCHES+=("$B")
    printf '  AFFECTED  %s%s\n' "$B" "$STACK_NOTE"
    echo "$OVERLAP" | sed 's/^/              + /'
  else
    CLEAN_BRANCHES+=("$B")
    printf '  clean     %s%s\n' "$B" "$STACK_NOTE"
  fi
done

note "Summary"
echo "  clean    : ${#CLEAN_BRANCHES[@]}"
echo "  affected : ${#AFFECTED_BRANCHES[@]}  ${AFFECTED_BRANCHES[*]:-}"

if [ "$APPLY" -eq 0 ]; then
  echo
  echo "Dry run. Re-run with --apply to merge upstream into $MASTER and rebase the clean branches."
  exit 0
fi

# --- apply -------------------------------------------------------------------

note "Merging $UPSTREAM_REMOTE/$MASTER into $MASTER"
git checkout -q "$MASTER"
if ! git merge --no-edit "$UPSTREAM_REMOTE/$MASTER"; then
  echo "Merge conflict updating $MASTER - resolve it, commit, then re-run with --apply." >&2
  exit 1
fi

REBASED=()
for B in "${CLEAN_BRANCHES[@]}"; do
  PARENT=$(detect_parent "$B")
  if [ "$PARENT" != "$MASTER" ]; then
    echo "  skip  $B (stacked on $PARENT - rebase its parent first, then: git rebase --onto $PARENT <old-parent> $B)"
    continue
  fi
  printf '  rebase %s onto %s ... ' "$B" "$MASTER"
  if git rebase -q "$MASTER" "$B" 2>/dev/null; then
    echo "ok"
    REBASED+=("$B")
  else
    git rebase --abort 2>/dev/null || true
    echo "CONFLICT (unexpected) - left as-is, resolve by hand"
  fi
done
git checkout -q "$MASTER"

if [ "${#AFFECTED_BRANCHES[@]}" -gt 0 ]; then
  note "Manual follow-up needed"
  echo "These overlap the upstream delta - rebase and resolve each by hand, then rebuild:"
  for B in "${AFFECTED_BRANCHES[@]}"; do echo "  git rebase $MASTER $B"; done
fi

echo
echo "Remember to rebuild-verify the rebased/affected branches, re-sync integration,"
echo "and refresh the tracker (ProposedPRs.html stats + ProposedPRs.pdf)."

if [ "$PUSH" -eq 1 ]; then
  note "Pushing $MASTER + rebased branches (force-with-lease)"
  git push "$FORK_REMOTE" "$MASTER"
  for B in "${REBASED[@]}"; do
    git push --force-with-lease "$FORK_REMOTE" "$B"
  done
fi
