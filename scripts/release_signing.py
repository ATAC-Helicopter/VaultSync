#!/usr/bin/env python3
"""Bind release provenance to every updater asset and its canonical manifest."""
from __future__ import annotations
import argparse
import json
import re
import shutil
import subprocess
from pathlib import Path
from safe_artifact_output import write_text_exclusive
from release_manifest import MANIFEST_NAME, collect_assets, sha256_file, validate_manifest
from release_source import PREPARATION_REF, beta_source_allowed


def validate_source(repository: str, commit: str, source_ref: str) -> None:
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,38}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}', repository):
        raise ValueError('Invalid source repository')
    if not re.fullmatch(r'(?:[0-9a-f]{40}|[0-9a-f]{64})', commit):
        raise ValueError('Invalid source commit')
    if source_ref != PREPARATION_REF and not re.fullmatch(r'refs/heads/(?:Dev|Stable|release/[0-9]+\.[0-9]+\.[0-9]+(?:-beta\.[0-9]+)?)', source_ref):
        raise ValueError('Invalid source ref')


def subjects(root: Path, repository: str, commit: str, source_ref: str) -> list[Path]:
    validate_source(repository, commit, source_ref)
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
    allowed = (beta_source_allowed(identity['version'], source_ref) if identity['channel'] == 'beta' else
               source_ref in {'refs/heads/Stable', f"refs/heads/release/{identity['version']}"})
    if not allowed:
        raise ValueError('Source ref is not authorized for this release channel')
    paths = collect_assets(root)
    if {p.name for p in paths} != {a['name'] for a in manifest['assets']}:
        raise ValueError('Asset inventory differs from canonical manifest')
    return [manifest_path, *paths]


def verify(paths: list[Path], repository: str, commit: str, source_ref: str,
           bundle: Path, trusted_root: Path, *, online: bool = True) -> None:
    validate_source(repository, commit, source_ref)
    for proof in (bundle, trusted_root):
        if not proof.is_file() or proof.is_symlink() or proof.stat().st_size == 0:
            raise ValueError(f'Missing retained signing proof: {proof}')
    executable = shutil.which('gh')
    if executable is None:
        raise ValueError('GitHub CLI is required for provenance verification')
    executable = str(Path(executable).resolve(strict=True))
    bundle, trusted_root = bundle.resolve(strict=True), trusted_root.resolve(strict=True)
    for path in paths:
        path = path.resolve(strict=True)
        # Bind each option value to its name so it cannot become another option.
        command = [executable, 'attestation', 'verify', str(path), f'--repo={repository}',
                   f'--signer-workflow={repository}/.github/workflows/release-assets.yml',
                   f'--source-ref={source_ref}', f'--source-digest={commit}',
                   '--deny-self-hosted-runners']
        if online:
            subprocess.run(command, check=True, timeout=120)
        subprocess.run(command + [f'--bundle={bundle}', f'--custom-trusted-root={trusted_root}'], check=True, timeout=120)


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
        write_text_exclusive(args.output, ''.join(f'{sha256_file(p)} *{p.as_posix()}\n' for p in paths))
    else:
        if args.bundle is None or args.trusted_root is None:
            parser.error('verify requires --bundle and --trusted-root')
        verify(paths, args.repository, args.commit, args.source_ref, args.bundle, args.trusted_root)
        print(f'Verified online and offline provenance for {len(paths)} release subjects')


if __name__ == '__main__':
    main()
