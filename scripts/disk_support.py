#!/usr/bin/env python3
"""Check the 1.9.0 disk qualification plan; never inspect or operate on devices."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STAGES = ('capture', 'validate-image', 'boot', 'restore', 'validate-restored')
STAGE_CHECKS = {
    'capture': ('complete-coverage', 'durable-completion'),
    'validate-image': ('payload-integrity', 'independent-source-unavailable'),
    'boot': ('independent-media', 'device-discovery'),
    'restore': ('destination-identity', 'authorized-overwrite', 'durable-writes'),
    'validate-restored': ('target-byte-coverage', 'restored-os-boot', 'expected-files', 'desktop-interactive'),
}
DIGEST = re.compile(r'[0-9a-f]{64}')
MAX_ARTIFACT_BYTES = 16 * 1024 * 1024
MAX_JSON_DEPTH = 64


def check_json_depth(document: str) -> None:
    """Bound container nesting before parsing, ignoring escaped string content."""
    depth = 0
    in_string = False
    escaped = False
    for character in document:
        if in_string:
            if escaped:
                escaped = False
            elif character == '\\':
                escaped = True
            elif character == '"':
                in_string = False
        elif character == '"':
            in_string = True
        elif character in '{[':
            depth += 1
            if depth > MAX_JSON_DEPTH:
                raise ValueError('JSON nesting exceeds the 64-container inspection bound')
        elif character in '}]':
            depth -= 1


def load_json(content: bytes) -> object:
    def members(pairs: list[tuple[str, object]]) -> dict[str, object]:
        result: dict[str, object] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('duplicate JSON object member')
            result[key] = value
        return result

    def reject_constant(_value: str) -> object:
        raise ValueError('non-finite JSON constant')

    def finite_number(value: str) -> float:
        number = float(value)
        if not math.isfinite(number):
            raise ValueError('non-finite JSON number')
        return number

    try:
        document = content.decode('utf-8')
        check_json_depth(document)
        return json.loads(document, object_pairs_hook=members,
                          parse_constant=reject_constant, parse_float=finite_number)
    except RecursionError as exc:
        raise ValueError('JSON nesting exceeds the parser inspection bound') from exc


def read_bounded(path: Path) -> bytes:
    with path.open('rb') as stream:
        content = stream.read(MAX_ARTIFACT_BYTES + 1)
    if len(content) > MAX_ARTIFACT_BYTES:
        raise ValueError('artifact exceeds the 16 MiB inspection bound')
    return content


def text(value: object) -> bool:
    return isinstance(value, str) and bool(value.strip())


def artifact(root: Path, reference: object) -> bytes:
    if not isinstance(reference, dict) or not text(reference.get('path')):
        raise ValueError('artifact needs a repository-relative path and SHA-256')
    relative = Path(reference['path'])
    if relative.is_absolute() or '..' in relative.parts:
        raise ValueError('artifact path must stay inside the repository')
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()) or not path.is_file():
        raise ValueError('artifact must be an existing repository file without escape')
    if path.stat().st_size > MAX_ARTIFACT_BYTES:
        raise ValueError('artifact exceeds the 16 MiB inspection bound')
    content = read_bounded(path)
    digest = reference.get('sha256')
    if not isinstance(digest, str) or not DIGEST.fullmatch(digest):
        raise ValueError('artifact needs a lowercase SHA-256 digest')
    if hashlib.sha256(content).hexdigest() != digest:
        raise ValueError('artifact digest mismatch')
    return content


def timestamp(value: object, now: datetime) -> datetime:
    if not isinstance(value, str) or not re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z', value):
        raise ValueError('observation time must be UTC with a Z suffix')
    observed = datetime.fromisoformat(value[:-1] + '+00:00')
    if observed > now:
        raise ValueError('future observations cannot qualify a profile')
    return observed


def validate(root: Path, data: object, require_qualified: bool = False,
             now: datetime | None = None) -> list[str]:
    errors: list[str] = []
    now = now or datetime.now(timezone.utc)
    if not isinstance(data, dict) or type(data.get('schemaVersion')) is not int or data['schemaVersion'] != 1 or data.get('release') != '1.9.0':
        return ['Expected the versioned 1.9.0 disk qualification plan.']
    engine = data.get('engine')
    if not isinstance(engine, dict) or engine.get('proposal') != 'internal-raw-offline' or engine.get('decision') not in ('proposed', 'approved'):
        return ['Unknown engine proposal/decision; revise the reviewed contract first.']
    approval_time = None
    if engine['decision'] == 'approved':
        try:
            approval = engine.get('approval')
            if not isinstance(approval, dict) or not text(approval.get('by')):
                raise ValueError('approval needs a named reviewer and retained record')
            observed_approval = timestamp(approval.get('observedAt'), now)
            artifact(root, approval.get('artifact'))
            approval_time = observed_approval
        except (ValueError, OSError) as exc:
            errors.append(f'Engine approval: {exc}')
    elif engine.get('approval') is not None:
        errors.append('A proposed engine cannot carry an approval claim.')
    profiles = data.get('profiles')
    if not isinstance(profiles, list) or not 1 <= len(profiles) <= 100:
        return errors + ['Expected 1–100 explicit profiles.']
    seen: set[str] = set()
    qualified = 0
    for profile in profiles:
        try:
            if not isinstance(profile, dict):
                raise ValueError('profile must be an object')
            identity = profile.get('id')
            if not isinstance(identity, str) or not re.fullmatch(r'[a-z0-9-]{1,100}', identity) or identity in seen:
                raise ValueError('profile identity is missing, invalid or duplicated')
            seen.add(identity)
            expected = dict(recoveryEnvironment='linux-x64-offline', firmware='uefi', partitionTable='gpt', encryption='none', scope='disk')
            if any(profile.get(k) != v for k, v in expected.items()):
                raise ValueError('profile exceeds the proposed offline recovery scope')
            if profile.get('liveCapture') is not False or profile.get('secureBoot') is not False:
                raise ValueError('live capture and Secure Boot remain outside this proposal')
            if type(profile.get('logicalSectorBytes')) is not int or profile['logicalSectorBytes'] not in (512, 4096):
                raise ValueError('unsupported logical sector geometry')
            if (profile.get('sourceOS'), profile.get('filesystem')) not in (('linux', 'ext4'), ('windows', 'ntfs')):
                raise ValueError('unsupported source OS/filesystem pair')
            if not text(profile.get('owner')) or profile.get('method') != 'docs/adr/001-imaging-engine-1.9.md' or not (root / profile['method']).is_file():
                raise ValueError('profile needs an owner and the reviewed procedure')
            status = profile.get('status')
            evidence = profile.get('evidence')
            if status == 'candidate':
                if evidence != []:
                    raise ValueError('candidate has no qualification claim; retain incomplete runs in the work ledger')
                continue
            if status != 'qualified' or engine['decision'] != 'approved':
                raise ValueError('qualification requires the approved engine decision')
            if approval_time is None:
                raise ValueError('qualification requires valid retained engine approval')
            if not isinstance(evidence, list) or len(evidence) != len(STAGES):
                raise ValueError('qualification needs the complete five-stage measured loop')
            if not text(profile.get('environment')) or profile.get('hardwareKind') not in ('virtual', 'physical'):
                raise ValueError('qualification must name its actual environment and hardware kind')
            binding = None
            previous_time = None
            for stage, reference in zip(STAGES, evidence):
                report = load_json(artifact(root, reference))
                if not isinstance(report, dict) or report.get('stage') != stage or report.get('profileId') != identity:
                    raise ValueError('report stage/profile binding mismatch')
                if report.get('kind') != 'measured' or report.get('outcome') != 'passed':
                    raise ValueError('only passed measured execution can qualify this loop')
                checks = report.get('checks')
                if not isinstance(checks, dict) or any(checks.get(check) != 'passed' for check in STAGE_CHECKS[stage]):
                    raise ValueError('missing or unsuccessful required stage checks')
                if report.get('environment') != profile['environment'] or report.get('hardwareKind') != profile['hardwareKind']:
                    raise ValueError('report environment differs from the qualified scope')
                fields = ('runId', 'imageRoot', 'captureBuild', 'recoveryBuild')
                current = tuple(report.get(k) for k in fields)
                if not all(text(v) for v in current) or not DIGEST.fullmatch(current[1]):
                    raise ValueError('missing run/content/build binding')
                if binding is not None and current != binding:
                    raise ValueError('evidence belongs to different runs, images or builds')
                binding = current
                observed = timestamp(report.get('observedAt'), now)
                if observed < approval_time:
                    raise ValueError('qualification evidence predates engine approval')
                if previous_time is not None and observed < previous_time:
                    raise ValueError('evidence timestamps contradict the required stage order')
                previous_time = observed
            qualified += 1
        except (ValueError, OSError, TypeError, UnicodeError) as exc:
            errors.append(f'Profile {profile.get("id", "?") if isinstance(profile, dict) else "?"}: {exc}')
    exclusions = data.get('exclusions')
    if not isinstance(exclusions, list) or not exclusions:
        errors.append('Expected explicit unsupported-scope refusal records.')
    else:
        excluded_ids: set[str] = set()
        for exclusion in exclusions:
            if not isinstance(exclusion, dict) or not text(exclusion.get('id')) or not text(exclusion.get('reason')) or exclusion.get('behavior') != 'reject-before-device-write':
                errors.append('Every exclusion needs its identity, reason and refusal before device writes.')
            elif exclusion['id'] in excluded_ids or exclusion['id'] in seen:
                errors.append('Duplicate/conflicting exclusion identity.')
            else:
                excluded_ids.add(exclusion['id'])
    if require_qualified and not qualified:
        errors.append('No qualified disk profile; stable disk support remains gated.')
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('check',))
    parser.add_argument('--require-qualified', action='store_true')
    args = parser.parse_args()
    try:
        data = load_json(read_bounded(ROOT / 'release/disk-support-1.9.0.json'))
        errors = validate(ROOT, data, args.require_qualified)
    except (ValueError, OSError) as exc:
        errors = [str(exc)]
    if errors:
        print('\n'.join(errors))
        return 1
    qualified = sum(p['status'] == 'qualified' for p in data['profiles'])
    print(f'Disk qualification plan valid: {len(data["profiles"])} profiles, {qualified} qualified; structural checks do not prove recovery.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
