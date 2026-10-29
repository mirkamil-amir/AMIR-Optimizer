#!/usr/bin/env bash
# Checks a pull request's title and branch against the naming rule (AGENTS.md "Branches and pull requests"):
#   title   <type>(AMIR-<N>): <Description>           e.g. chore(AMIR-1): Commit, branch naming and disk space rules
#   branch  <type>/amir_<n>_<short_description>        e.g. chore/amir_1_workflow_rules_free_space
# with the same type and number in both. Dependabot branches (dependabot/…) are exempt.
# Usage: ./scripts/check-pr-name.sh "<title>" "<branch>"      (CI: .github/workflows/pr-title.yml)
set -euo pipefail

title=${1:?usage: check-pr-name.sh "<title>" "<branch>"}
branch=${2:?usage: check-pr-name.sh "<title>" "<branch>"}
types='feat|fix|chore|docs|refactor|perf|test|ci'

if [[ "$branch" == dependabot/* ]]; then
  echo "Dependabot branch – naming rule not applied."
  exit 0
fi

errors=()
if [[ "$title" =~ ^($types)\(AMIR-([1-9][0-9]*)\):\ [A-Z0-9].*[^.\ ]$ ]]; then
  title_type=${BASH_REMATCH[1]}
  title_id=${BASH_REMATCH[2]}
else
  errors+=("Title \"$title\" must look like: chore(AMIR-1): Commit, branch naming and disk space rules
  – type: $types · ID: AMIR-<number> · description in sentence case (capital first letter), no full stop at the end")
fi

if [[ "$branch" =~ ^($types)/amir_([1-9][0-9]*)_[a-z0-9]+(_[a-z0-9]+)*$ ]]; then
  branch_type=${BASH_REMATCH[1]}
  branch_id=${BASH_REMATCH[2]}
else
  errors+=("Branch \"$branch\" must look like: chore/amir_1_workflow_rules_free_space
  – lowercase, words joined by underscores (never hyphens), type: $types")
fi

if [[ ${#errors[@]} -eq 0 ]]; then
  [[ "$title_type" == "$branch_type" ]] || errors+=("Type differs: title \"$title_type\", branch \"$branch_type\".")
  [[ "$title_id" == "$branch_id" ]] || errors+=("ID differs: title AMIR-$title_id, branch amir_$branch_id.")
fi

if [[ ${#errors[@]} -gt 0 ]]; then
  printf '%s\n' "${errors[@]}" >&2
  exit 1
fi
echo "OK: $title_type, AMIR-$title_id"
