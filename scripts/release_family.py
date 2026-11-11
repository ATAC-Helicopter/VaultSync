#!/usr/bin/env python3
"""Check the immutable Recovery Horizon source, release ownership and branch identities."""
import hashlib
import json
import re
from pathlib import Path


def validate(root: Path, data: dict) -> list[str]:
    errors = []
    if data.get('schemaVersion') != 1:
        errors.append('Unsupported family schema')
    source = (root / data['source']['path']).resolve()
    if not source.is_relative_to(root.resolve()) or not source.is_file():
        errors.append('Planning source must be an existing repository file')
    elif hashlib.sha256(source.read_bytes()).hexdigest() != data['source']['sha256']:
        errors.append('Imported planning source digest changed')
    releases = data['releases']
    if [v['version'] for v in releases] != [f'1.9.{i}' for i in range(9)]:
        errors.append('Family sequence must remain 1.9.0 through 1.9.8')
    seen = set()
    for release in releases:
        v = release['version']
        if release['branch'] != f'release/{v}':
            errors.append(f'{v}: branch identity mismatch')
        for work in release['workIds']:
            if work in seen:
                errors.append(f'{work}: duplicate release ownership')
            seen.add(work)
        if not (root / f'docs/RELEASE_{v}.md').is_file():
            errors.append(f'{v}: missing release contract')
    roadmap = (root / data['canonicalRoadmap']).read_text(encoding='utf-8-sig')
    family = roadmap.split('# VaultSync 1.9 — Recovery Horizon', 1)[1]
    ids = set(re.findall(r'^- \[[ x]\] `((?:VS|BUG)-\d+)`', family, re.MULTILINE))
    expected = seen | set(data['conditionalWorkIds']) | set(data['deliveredFoundationWorkIds'])
    if ids != expected:
        errors.append(f'Roadmap ownership drift: unowned={sorted(ids-expected)}, absent={sorted(expected-ids)}')
    aliases = data['documentAliases']
    if len({a['documentId'] for a in aliases}) != len(aliases) or len({a['canonicalId'] for a in aliases}) != len(aliases) or len({a['issue'] for a in aliases}) != len(aliases):
        errors.append('Document aliases must be one-to-one')
    for alias in aliases:
        if alias['canonicalId'] not in seen or not 703 <= alias['issue'] <= 715:
            errors.append('BSC alias must point to an existing canonical BSC owner')
    return errors


def main():
    root = Path(__file__).resolve().parents[1]
    errors = validate(root, json.loads((root / 'release/1.9-family.json').read_text()))
    if errors:
        print('\n'.join(errors))
        return 1
    print('Recovery Horizon source, immutable ID ownership and release contracts agree.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
