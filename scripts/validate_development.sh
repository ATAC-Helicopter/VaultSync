#!/usr/bin/env bash
# Local development checks with compact output; full logs remain in a temp directory.
set -euo pipefail

mode=${1:-contracts}
case "$mode" in
  contracts|release) ;;
  *) printf 'Usage: bash scripts/validate_development.sh [contracts|release]\n' >&2; exit 2 ;;
esac

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
cd -- "$repo_root"
validation_logs=$(mktemp -d "${TMPDIR:-/tmp}/vaultsync-validation.XXXXXX")
printf 'Validation: %s\nLogs: %s\n' "$mode" "$validation_logs"

run_check() {
  local check_name=$1
  shift
  if "$@" >"$validation_logs/$check_name.log" 2>&1; then
    printf 'PASS %s\n' "$check_name"
    tail -n 5 "$validation_logs/$check_name.log"
  else
    local check_exit=$?
    printf 'FAIL %s (exit %s)\n' "$check_name" "$check_exit" >&2
    tail -n 60 "$validation_logs/$check_name.log" >&2
    return "$check_exit"
  fi
}

run_check release-family python3 scripts/release_family.py
run_check release-metadata python3 scripts/public_release_metadata.py check
run_check changelog python3 scripts/changelog.py check
run_check script-tests python3 -m unittest discover -s tests/scripts -q

if [[ "$mode" == release ]]; then
  # Serialize MSBuild: Core can otherwise be evaluated twice for the UI's TFM override.
  # Do not run concurrently with an IDE build using the same output directories.
  run_check solution-build dotnet build VaultSync.sln --configuration Release \
    -warnaserror -p:UseSharedCompilation=false -m:1
  run_check core-tests dotnet test tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj \
    --configuration Release --no-build --logger 'trx;LogFileName=core-tests.trx' \
    --results-directory "$validation_logs/test-results"
fi

printf 'Development checks passed; product, platform and release approval gates remain separate.\n'
