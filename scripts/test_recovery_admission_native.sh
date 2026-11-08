#!/usr/bin/env bash
# Negative installed-closure admission only. No real boot, role, raw disk or
# admitted lease. Existing runtime/test inputs are bound read-only, never copied.
set -euo pipefail
umask 077
fail() { printf 'recovery-admission.native-refused: %s\n' "$1" >&2; exit 1; }
[[ $# == 1 ]] || fail 'one fresh results directory required'
[[ $(uname -s) == Linux && $(uname -m) == x86_64 && $(id -u) != 0 ]] || fail 'normal Ubuntu x64 coordinator required'
. /etc/os-release
[[ ${ID:-} == ubuntu && ${VERSION_ID:-} == 24.04 ]] || fail 'Ubuntu 24.04 required'
for program in dotnet dotnet-coverage sudo unshare mount timeout readlink stat python3 chroot; do
  command -v "$program" >/dev/null || fail 'required tool unavailable'
done
sudo -n true || fail 'passwordless sudo unavailable'
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
[[ -f "$repo_root/tests/VaultSync.Core.Tests/bin/Release/net10.0/VaultSync.Core.Tests.dll" ]] || fail 'Release tests must already be built'
dotnet_path=$(readlink -f -- "$(command -v dotnet)")
coverage_path=$(readlink -f -- "$(command -v dotnet-coverage)")
[[ -x "$dotnet_path" && -x "$coverage_path" ]] || fail 'resolved managed tools unavailable'
source_commit=$(git -C "$repo_root" rev-parse HEAD)
[[ $source_commit =~ ^[0-9a-f]{40}$ ]] || fail 'source revision unavailable'
[[ $1 == /* && $1 =~ ^[A-Za-z0-9_./-]+$ && $1 != */../* && $1 != */./* ]] || fail 'noncanonical results path'
result_parent=$(readlink -f -- "$(dirname -- "$1")")
[[ -d "$result_parent" && $result_parent != / && $1 == "$result_parent/$(basename -- "$1")" ]] || fail 'existing canonical results parent required'
# /tmp becomes a private bounded tmpfs, so it cannot contain inputs or reports.
for path in "$repo_root" "$dotnet_path" "$coverage_path" "$result_parent"; do
  [[ $path != /tmp && $path != /tmp/* ]] || fail 'inputs/results cannot be under private tmpfs'
done
results=$1
[[ ! -e "$results" && ! -L "$results" ]] || fail 'results must not exist'
mkdir -m 700 -- "$results"
results_stamp=$(stat -c '%d:%i' -- "$results")
scratch=$(mktemp -d "$result_parent/.recovery-admission.XXXXXXXX")
scratch_stamp=$(stat -c '%d:%i' -- "$scratch")
runner_uid=$(id -u); runner_gid=$(id -g)
cleanup() {
  local status=$?
  trap - EXIT
  # The synchronous namespace supervisor has exited. PID1 exit kills its owned
  # descendants; all fixture mounts disappeared before any scratch removal.
  if ! sudo -n /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin /bin/bash -c '
    set -euo pipefail
    [[ -d "$1" && ! -L "$1" && $(readlink -f -- "$1") == "$1" && $(stat -c "%d:%i" -- "$1") == "$2" ]]
    rm -rf --one-file-system -- "$1"
  ' -- "$scratch" "$scratch_stamp"; then
    printf 'recovery-admission.native-cleanup-unconfirmed\n' >&2
    status=1
  fi
  exit "$status"
}
trap cleanup EXIT
root_child=$(cat <<'ROOT_CHILD'
set -euo pipefail
umask 077
repo=$1; dotnet=$2; coverage=$3; results=$4; scratch=$5; runner_uid=$6; runner_gid=$7; results_stamp=$8; scratch_stamp=$9
[[ $(id -u) == 0 && $(id -g) == 0 && $$ == 1 ]]
[[ $(readlink -f -- "$scratch") == "$scratch" && $(stat -c '%d:%i' -- "$scratch") == "$scratch_stamp" ]]
[[ $(readlink -f -- "$results") == "$results" && $(stat -c '%d:%i' -- "$results") == "$results_stamp" ]]
[[ $(stat -c %u -- "$results") == "$runner_uid" && $(stat -c %a -- "$results") == 700 ]]
[[ -z $(find "$results" -mindepth 1 -maxdepth 1 -print -quit) ]]
outer_mount=$(readlink /proc/1/ns/mnt); outer_pid=$(readlink /proc/1/ns/pid)
[[ $(readlink /proc/self/ns/mnt) != "$outer_mount" && $(readlink /proc/self/ns/pid) != "$outer_pid" ]]
mount --make-rprivate /
chown 0:0 -- "$scratch"; chmod 700 -- "$scratch"
root="$scratch/root"
mkdir -m 755 -- "$root"
[[ $(stat -f -c %t -- "$root") == ef53 ]] # Real owned ext4 directory, no loop/raw device.
mkdir -m 700 -- "$scratch/outer-proc"
mount --bind /proc "$scratch/outer-proc"
mount -o remount,bind,ro "$scratch/outer-proc"
mount -t proc -o nosuid,nodev,noexec proc /proc
[[ $(readlink "$scratch/outer-proc/1/ns/mnt") == "$outer_mount" && $(readlink "$scratch/outer-proc/1/ns/pid") == "$outer_pid" ]]
restore_reports() {
  local status=$?
  trap - EXIT
  if [[ ! -d "$results" || -L "$results" || $(readlink -f -- "$results") != "$results" || $(stat -c '%d:%i' -- "$results") != "$results_stamp" ]]; then
    printf 'recovery-admission.native-cleanup-unconfirmed\n' >&2; exit 1
  fi
  if ! (cd -- "$results" && [[ $(pwd -P) == "$results" && $(stat -c '%d:%i' .) == "$results_stamp" ]] && chown -hR "$runner_uid:$runner_gid" -- .); then
    printf 'recovery-admission.native-cleanup-unconfirmed\n' >&2; exit 1
  fi
  exit "$status"
}
trap restore_reports EXIT
# All descendants and mutations are owned. In particular, never bind/remount the
# host / as the test root: only this newly created ext4 directory is remounted.
mount --bind "$root" "$root"
mkdir -m 755 -p "$root/fixture-control" "$root/results" "$root/usr/lib/vaultsync-recovery" \
  "$root/usr/lib/systemd/system" "$root/usr/lib64" "$root/etc" "$root/dev" \
  "$root/proc" "$root/outer-proc" "$root/run" "$root/srv" "$root/tmp"
mount --bind "$root" "$root/fixture-control" # Nonrecursive alias: synthetic fault injection only.
bind_ro() {
  local source=$1 target=$2
  if [[ -d "$source" ]]; then mkdir -p -- "$target"; else mkdir -p -- "$(dirname -- "$target")"; touch -- "$target"; fi
  mount --bind "$source" "$target"
  mount -o remount,bind,ro,nosuid,nodev "$target"
}
# Reuse only the dynamic-loader/runtime/test/tool inputs. No host /etc tree,
# root directory, home directory or writable repository enters the chroot.
bind_ro /usr/lib/x86_64-linux-gnu "$root/usr/lib/x86_64-linux-gnu"
bind_ro /usr/lib64 "$root/usr/lib64"
ln -s usr/lib "$root/lib"; ln -s usr/lib64 "$root/lib64"
for name in ld.so.cache os-release; do bind_ro "/etc/$name" "$root/etc/$name"; done
for name in null urandom random; do
  touch "$root/dev/$name"; mount --bind "/dev/$name" "$root/dev/$name"
done
bind_ro "$(dirname -- "$dotnet")" "$root$(dirname -- "$dotnet")"
bind_ro "$(dirname -- "$coverage")" "$root$(dirname -- "$coverage")"
bind_ro "$repo" "$root$repo"
bind_ro /proc "$root/proc"
bind_ro "$scratch/outer-proc" "$root/outer-proc"
mount --bind "$results" "$root/results"
mount -t tmpfs -o size=1m,mode=0755,nosuid,nodev,noexec tmpfs "$root/run"
mount -t tmpfs -o size=1m,mode=0755,nosuid,nodev,noexec tmpfs "$root/srv"
# Coverage IPC and dotnet temporary files share this private mount on both sides
# of chroot. Its bound includes runtime-generated files, not source/runtime binds.
mount -t tmpfs -o size=64m,mode=1777,nosuid,nodev tmpfs /tmp
mount --bind /tmp "$root/tmp"
mkdir -m 700 /tmp/cli-home /tmp/runtime
[[ $(stat -c '%u:%a' /tmp/runtime) == 0:700 ]]
mount -o remount,bind,ro "$root"
[[ $(stat -c '%d:%i' "$root") == $(stat -c '%d:%i' "$root/fixture-control") ]]
# Serviced .NET uses XDG_RUNTIME_DIR for named-mutex IPC. The clean chroot has
# no user profile fallback; keep IPC in the same owned tmpfs on both sides.
export DOTNET_CLI_HOME=/tmp/cli-home TMPDIR=/tmp XDG_RUNTIME_DIR=/tmp/runtime
"$coverage" collect -f xml -o "$results/coverage-admission.xml" \
  /usr/sbin/chroot "$root" "$dotnet" test "$repo/tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj" \
  --configuration Release --no-build --no-restore \
  --filter FullyQualifiedName~LinuxRecoveryAdmissionNativeTests \
  --logger 'trx;LogFileName=native-admission.trx' --results-directory /results
ROOT_CHILD
)
sudo -n /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin LANG=C.UTF-8 TZ=UTC \
  DOTNET_ROOT="$(dirname -- "$dotnet_path")" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true \
  VS_ADMISSION_NATIVE_REQUIRED=1 VS_ADMISSION_NATIVE_SCRATCH="$scratch" \
  VS_ADMISSION_NATIVE_RESULTS="$results" VS_ADMISSION_NATIVE_SOURCE="$source_commit" \
  /usr/bin/timeout --signal=TERM --kill-after=10s 240s \
  /usr/bin/unshare --mount --pid --fork --kill-child=KILL --propagation private \
  /bin/bash -c "$root_child" -- "$repo_root" "$dotnet_path" "$coverage_path" "$results" "$scratch" \
  "$runner_uid" "$runner_gid" "$results_stamp" "$scratch_stamp"
[[ $(stat -c '%d:%i' -- "$results") == "$results_stamp" && $(stat -c %u -- "$results") == "$runner_uid" ]] || fail 'results ownership changed'
python3 - "$results" "$source_commit" "$repo_root" <<'VERIFY'
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

results, commit, repo = Path(sys.argv[1]), sys.argv[2], Path(sys.argv[3])
evidence = results / 'native-admission-evidence.json'
if not 0 < evidence.stat().st_size <= 65536:
    raise SystemExit('native admission evidence bound')
proof = json.loads(evidence.read_text())
expected = ['actual-core-location', 'undeclared-file', 'symlink', 'payload-hash', 'hardlink', 'shared-write',
            'systemd-hash', 'unit-hash', 'pre-cancel', 'enumerated-cancel', 'repaired-closure-still-refuses-real-core']
if (proof.get('schemaVersion') != 1 or proof.get('scope') != 'negative-readonly-closure-admission-fixture'
        or any(proof.get(key) != commit for key in ('sourceCommit', 'compiledSourceCommit', 'checkoutRevision'))
        or proof.get('executedCaseNames') != expected or proof.get('count') != len(expected)
        or proof.get('readonlyExt4Root') is not True or proof.get('actualCoreLocationRefused') is not True
        or proof.get('runtimeAndStoreUnchanged') is not True or proof.get('heldInstallationFilesAfterRetry') != 0):
    raise SystemExit('native admission executed-case evidence missing')
ns = proof['namespaces']
if ns['outerMount'] == ns['currentMount'] or ns['outerPid'] == ns['currentPid']:
    raise SystemExit('native admission kernel namespaces not isolated')
trx = ET.parse(results / 'native-admission.trx').getroot()
counters = trx.find('.//{*}Counters')
if counters is None or int(counters.get('executed', '0')) != 1 or any(int(counters.get(key, '0')) for key in ('failed', 'error', 'timeout', 'aborted')):
    raise SystemExit('native admission test not executed successfully')
coverage = results / 'coverage-admission.xml'
if not 0 < coverage.stat().st_size <= 64 * 1024 * 1024:
    raise SystemExit('native admission coverage unavailable or over bound')
report = ET.parse(coverage).getroot()
if report.tag != 'results':
    raise SystemExit('Microsoft coverage required')
covered = set()
for module in report.findall('./modules/module'):
    if module.get('name', '').lower() != 'vaultsync.core.dll':
        continue
    ids = {item.get('id') for item in module.findall('./source_files/source_file')
           if item.get('path', '').replace('\\', '/').endswith('/src/VaultSync.Core/Services/LinuxRecoveryMediaInstallation.cs')}
    for span in module.findall('./functions/function/ranges/range'):
        if span.get('source_id') in ids and span.get('covered') in ('yes', 'partial'):
            covered.update(range(int(span.get('start_line', '0')), int(span.get('end_line', '0')) + 1))
source = (repo / 'src/VaultSync.Core/Services/LinuxRecoveryMediaInstallation.cs').read_text().splitlines()
opening = next(i + 1 for i, line in enumerate(source) if 'internal static async Task<LinuxRecoveryMediaInstallation> OpenAsync(' in line)
following = next(i + 1 for i, line in enumerate(source) if 'private static void ObserveControllerAdmission(' in line)
if len(covered.intersection(range(opening, following))) < 15:
    raise SystemExit('actual closure OpenAsync ranges not covered')
print('recovery-admission.native-passed', len(expected), 'negative closure/cancellation cases; no admitted boot/media role')
VERIFY
