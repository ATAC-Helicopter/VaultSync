#!/usr/bin/env python3
"""Bind release provenance to every updater asset and its canonical manifest."""
from __future__ import annotations
import argparse
import json
import subprocess
from pathlib import Path
from release_manifest import MANIFEST_NAME, collect_assets, sha256_file, validate_manifest


def subjects(root: Path, repository: str, commit: str, source_ref: str) -> list[Path]:
    if root.is_symlink():
        raise ValueError('Asset root must not be a symlink')
    manifests = list(root.rglob(MANIFEST_NAME))
    if manifests != [root / MANIFEST_NAME]:
        raise ValueError('Expected one canonical manifest at the asset root')
    manifest_path = root / MANIFEST_NAME
    if manifest_path.is_symlink():
        raise ValueError('Manifest must not be a symlink')
    def unique_members(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f'Duplicate manifest member: {key}')
            result[key] = value
        return result
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'), object_pairs_hook=unique_members)
    validate_manifest(manifest, asset_root=root)
    identity = manifest['release']
    if identity['repository'] != repository or identity['commit'] != commit:
        raise ValueError('Manifest repository or commit differs from trusted source run')
    allowed = {'refs/heads/Dev'} if identity['channel'] == 'beta' else {
        'refs/heads/Stable', f"refs/heads/release/{identity['version']}"}
    if source_ref not in allowed:
        raise ValueError('Source ref is not authorized for this release channel')
    paths = collect_assets(root)
    if {p.name for p in paths} != {a['name'] for a in manifest['assets']}:
        raise ValueError('Asset inventory differs from canonical manifest')
    return [manifest_path, *paths]


def verify(paths: list[Path], repository: str, commit: str, source_ref: str,
           bundle: Path, trusted_root: Path, *, online: bool = True) -> None:
    for proof in (bundle, trusted_root):
        if not proof.is_file() or proof.is_symlink() or proof.stat().st_size == 0:
            raise ValueError(f'Missing retained signing proof: {proof}')
    for path in paths:
        command = ['gh', 'attestation', 'verify', str(path), '--repo', repository,
                   '--signer-workflow', f'{repository}/.github/workflows/release-assets.yml',
                   '--source-ref', source_ref, '--source-digest', commit,
                   '--deny-self-hosted-runners']
        if online:
            subprocess.run(command, check=True, timeout=120)
        subprocess.run(command + ['--bundle', str(bundle), '--custom-trusted-root',
                                 str(trusted_root)], check=True, timeout=120)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['subjects', 'verify'])
    parser.add_argument('--asset-root', type=Path, required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--source-ref', required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--bundle', type=Path)
    parser.add_argument('--trusted-root', type=Path)
    args = parser.parse_args()
    paths = subjects(args.asset_root, args.repository, args.commit, args.source_ref)
    if args.command == 'subjects':
        if args.output is None:
            parser.error('subjects requires --output')
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(''.join(f'{sha256_file(p)} *{p.as_posix()}\n' for p in paths), encoding='utf-8')
    else:
        if args.bundle is None or args.trusted_root is None:
            parser.error('verify requires --bundle and --trusted-root')
        verify(paths, args.repository, args.commit, args.source_ref, args.bundle, args.trusted_root)
        print(f'Verified online and offline provenance for {len(paths)} release subjects')


if __name__ == '__main__':
    main()
