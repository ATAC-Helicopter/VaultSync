#!/usr/bin/env bash
# Ubuntu 24.04/x64 only. One NEW results directory; no build/restore or raw devices.
# The privileged command is fixed. Environment fields request reports, never prove
# authority/isolation. Kernel namespace/mount/inode checks run again inside C#.
set -euo pipefail
umask 077

fail() { printf 'recovery-storage.native-refused: %s\n' "$1" >&2; exit 1; }
[[ $# == 1 ]] || fail 'one fresh results directory required'
[[ $(uname -s) == Linux && $(uname -m) == x86_64 && $(id -u) != 0 ]] || fail 'normal Ubuntu x64 coordinator required'
# Trusted operating-system metadata, not a repository/image-supplied script.
. /etc/os-release
[[ ${ID:-} == ubuntu && ${VERSION_ID:-} == 24.04 ]] || fail 'Ubuntu 24.04 required'
for program in dotnet dotnet-coverage sudo unshare mount timeout readlink stat python3; do
  command -v "$program" >/dev/null || fail 'required tool unavailable'
done
sudo -n true || fail 'passwordless sudo unavailable'
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
project="$repo_root/tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj"
[[ -f "$repo_root/tests/VaultSync.Core.Tests/bin/Release/net10.0/VaultSync.Core.Tests.dll" ]] || fail 'Release tests must already be built'
dotnet_path=$(readlink -f -- "$(command -v dotnet)")
coverage_path=$(readlink -f -- "$(command -v dotnet-coverage)")
[[ -x "$dotnet_path" && -x "$coverage_path" ]] || fail 'resolved managed tools unavailable'
source_commit=$(git -C "$repo_root" rev-parse HEAD)
[[ $source_commit =~ ^[0-9a-f]{40}$ ]] || fail 'source revision unavailable'
[[ $1 == /* && $1 =~ ^[A-Za-z0-9_./-]+$ && $1 != */../* && $1 != */./* ]] || fail 'noncanonical results path'
result_parent=$(readlink -f -- "$(dirname -- "$1")")
[[ -d "$result_parent" && $result_parent != / && $1 == "$result_parent/$(basename -- "$1")" ]] || fail 'existing canonical results parent required'
results=$1
[[ ! -e "$results" && ! -L "$results" ]] || fail 'results must not exist'
mkdir -m 700 -- "$results"
results_stamp=$(stat -c '%d:%i' -- "$results")
scratch=$(mktemp -d "$result_parent/.recovery-storage.XXXXXXXX")
scratch_stamp=$(stat -c '%d:%i' -- "$scratch")
runner_uid=$(id -u); runner_gid=$(id -g)

cleanup() {
  local status=$?
  trap - EXIT
  # Called after the synchronous timeout/unshare supervisor has returned. Only
  # the exact fresh scratch inode is removed; completed reports remain retained.
  if ! sudo -n /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin /bin/bash -c '
    set -euo pipefail
    [[ $(readlink -f -- "$1") == "$1" && $(stat -c "%d:%i" -- "$1") == "$2" && -d "$1" && ! -L "$1" ]]
    rm -rf --one-file-system -- "$1"
  ' -- "$scratch" "$scratch_stamp"; then
    printf 'recovery-storage.native-cleanup-unconfirmed\n' >&2
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
[[ $(readlink -f -- "$results") == "$results" && $(stat -c '%d:%i' -- "$results") == "$results_stamp" ]]
[[ $(readlink -f -- "$scratch") == "$scratch" && $(stat -c '%d:%i' -- "$scratch") == "$scratch_stamp" ]]
[[ $(stat -c %u -- "$results") == "$runner_uid" && $(stat -c %a -- "$results") == 700 ]]
[[ -z $(find "$results" -mindepth 1 -maxdepth 1 -print -quit) ]]
outer_mount=$(readlink /proc/1/ns/mnt); outer_pid=$(readlink /proc/1/ns/pid)
private_mount=$(readlink /proc/self/ns/mnt); private_pid=$(readlink /proc/self/ns/pid)
[[ $private_mount != "$outer_mount" && $private_pid != "$outer_pid" ]]
chown 0:0 -- "$scratch"; chmod 700 -- "$scratch"
mkdir -m 700 -- "$scratch/store" "$scratch/outer-proc" "$scratch/cli-home" "$scratch/tmp"
[[ $(stat -f -c %t -- "$scratch/store") == ef53 ]] # Actual writable ext4, no loop device.
restore_reports() {
  local status=$?
  trap - EXIT
  if [[ ! -d "$results" || -L "$results" || $(readlink -f -- "$results") != "$results" || $(stat -c '%d:%i' -- "$results") != "$results_stamp" ]]; then
    printf 'recovery-storage.native-cleanup-unconfirmed\n' >&2
    exit 1
  fi
  if ! (
    cd -- "$results" &&
    [[ $(pwd -P) == "$results" && $(stat -c '%d:%i' .) == "$results_stamp" ]] &&
    chown -hR "$runner_uid:$runner_gid" -- .
  ); then
    printf 'recovery-storage.native-cleanup-unconfirmed\n' >&2
    exit 1
  fi
  exit "$status"
}
trap restore_reports EXIT
mount --make-rprivate /
# Preserve the actual OUTER proc instance before exposing this new PID namespace.
mount --bind /proc "$scratch/outer-proc"
mount -o remount,bind,ro "$scratch/outer-proc"
mount -t proc -o nosuid,nodev,noexec proc /proc
[[ $(readlink /proc/1/ns/mnt) == "$private_mount" && $(readlink /proc/1/ns/pid) == "$private_pid" ]]
[[ $(readlink "$scratch/outer-proc/1/ns/mnt") == "$outer_mount" && $(readlink "$scratch/outer-proc/1/ns/pid") == "$outer_pid" ]]
# /srv changes exist ONLY in the proved private mount namespace.
mount -t tmpfs -o size=16m,mode=0700,nosuid,nodev tmpfs /srv
mkdir -m 700 /srv/vaultsync-recovery
mount --bind "$scratch/store" /srv/vaultsync-recovery
[[ $(stat -c '%d:%i' -- "$scratch/store") == $(stat -c '%d:%i' /srv/vaultsync-recovery) ]]
cd -- "$repo"
"$coverage" collect -f xml -o "$results/coverage-native.xml" \
  "$dotnet" test "$repo/tests/VaultSync.Core.Tests/VaultSync.Core.Tests.csproj" \
  --configuration Release --no-build --no-restore \
  --filter FullyQualifiedName~LinuxRecoveryStorageNativeTests \
  --logger 'trx;LogFileName=native-storage.trx' --results-directory "$results"
ROOT_CHILD
)

# No inherited loader/profiler hooks or SONAR/GITHUB credentials enter root tests.
# unshare owns a fresh PID namespace: exit of its PID1 kills every descendant;
# timeout also kills the exact unshare child, never a process-name/PID sweep.
sudo -n /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin LANG=C.UTF-8 \
  DOTNET_ROOT="$(dirname -- "$dotnet_path")" DOTNET_CLI_HOME="$scratch/cli-home" \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
  DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true TMPDIR="$scratch/tmp" \
  VS_STORAGE_NATIVE_REQUIRED=1 VS_STORAGE_NATIVE_SCRATCH="$scratch" \
  VS_STORAGE_NATIVE_RESULTS="$results" VS_STORAGE_NATIVE_SOURCE="$source_commit" \
  /usr/bin/timeout --signal=TERM --kill-after=10s 240s \
  /usr/bin/unshare --mount --pid --fork --kill-child=KILL --propagation private \
  /bin/bash -c "$root_child" -- "$repo_root" "$dotnet_path" "$coverage_path" "$results" "$scratch" \
  "$runner_uid" "$runner_gid" "$results_stamp" "$scratch_stamp"

[[ $(stat -c '%d:%i' -- "$results") == "$results_stamp" && $(stat -c %u -- "$results") == "$runner_uid" ]] || fail 'results ownership changed'
# Unprivileged report validation only; this Python does not launch/root-isolate.
python3 - "$results" "$source_commit" "$repo_root" <<'VERIFY'
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

results, commit, repo = Path(sys.argv[1]), sys.argv[2], Path(sys.argv[3])
proof_path = results / 'native-storage-evidence.json'
if proof_path.stat().st_size > 65536:
    raise SystemExit('native proof bound')
proof = json.loads(proof_path.read_text())
cases = proof.get('executedCaseNames', [])
if (proof.get('schemaVersion') != 1 or proof.get('scope') != 'owned-filesystem-storage-fixture'
        or proof.get('sourceCommit') != commit or proof.get('compiledSourceCommit') != commit
        or proof.get('checkoutRevision') != commit or proof.get('count', 0) != len(cases)
        or not cases or len(set(cases)) != len(cases)):
    raise SystemExit('native proof missing actual executed cases')
ns = proof['namespaces']
if ns['outerMount'] == ns['currentMount'] or ns['outerPid'] == ns['currentPid']:
    raise SystemExit('native namespace proof invalid')
if proof['sourceInode'] != proof['storeInode'] or any(proof['sourceDevice' + key] != proof['storeDevice' + key] for key in ('Major', 'Minor')):
    raise SystemExit('native source/store identity differs')
trx = ET.parse(results / 'native-storage.trx').getroot()
counters = trx.find('.//{*}Counters')
if counters is None or int(counters.get('executed', '0')) <= 0 or any(int(counters.get(key, '0')) for key in ('failed', 'error', 'timeout', 'aborted')):
    raise SystemExit('native tests did not pass')
coverage = results / 'coverage-native.xml'
if not 0 < coverage.stat().st_size <= 64 * 1024 * 1024:
    raise SystemExit('native coverage unavailable or over bound')
report = ET.parse(coverage).getroot()
if report.tag != 'results':
    raise SystemExit('Microsoft coverage required')
covered = set()
for module in report.findall('./modules/module'):
    if module.get('name', '').lower() != 'vaultsync.core.dll':
        continue
    ids = {item.get('id') for item in module.findall('./source_files/source_file')
           if item.get('path', '').replace('\\', '/').endswith('/src/VaultSync.Core/Services/LinuxRecoveryStorageLease.cs')}
    for span in module.findall('./functions/function/ranges/range'):
        if span.get('source_id') in ids and span.get('covered') in ('yes', 'partial'):
            covered.update(range(int(span.get('start_line', '0')), int(span.get('end_line', '0')) + 1))
source = (repo / 'src/VaultSync.Core/Services/LinuxRecoveryStorageLease.cs').read_text().splitlines()
create = next(i + 1 for i, line in enumerate(source) if 'internal static LinuxRecoveryStorageLease Create(' in line)
complete = next(i + 1 for i, line in enumerate(source) if 'internal void Complete(' in line)
following = next(i + 1 for i, line in enumerate(source) if 'internal static void ValidateStagingFiles(' in line)
if len(covered.intersection(range(create, complete))) < 5 or len(covered.intersection(range(complete, following))) < 5:
    raise SystemExit('actual StorageLease Create/Complete source ranges not covered')
print('recovery-storage.native-passed', len(cases), 'owned filesystem cases; no Host/media qualification')
VERIFY
