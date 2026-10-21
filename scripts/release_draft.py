#!/usr/bin/env python3
"""Fail-closed source, inventory and existing draft checks; never creates/publishes releases."""
import argparse
import json
import re
import subprocess
from pathlib import Path
from urllib.parse import quote

from release_manifest import (MANIFEST_NAME, collect_assets, expected_asset_keys,
                              sha256_file, validate_manifest)
from release_source import beta_source_allowed


def load(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('Duplicate JSON member')
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding='utf-8'), object_pairs_hook=unique)


def source_ref(run, repository, version, channel, dispatch_ref):
    if not isinstance(run, dict) or not re.fullmatch(r'[0-9a-f]{40}', str(run.get('head_sha', ''))):
        raise ValueError('Source run has no exact commit')
    if (run.get('status') != 'completed' or run.get('conclusion') != 'success' or
            run.get('event') != 'workflow_dispatch' or run.get('path') != '.github/workflows/release-assets.yml' or
            run.get('repository', {}).get('full_name') != repository or
            run.get('head_repository', {}).get('full_name') != repository):
        raise ValueError('Source must be a successful asset workflow run in this repository')
    ref = 'refs/heads/' + str(run.get('head_branch', ''))
    if ref != dispatch_ref:
        raise ValueError('Draft assembly must use the source branch')
    allowed = (ref == 'refs/heads/Stable' and re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version)
               if channel == 'stable' else channel == 'beta' and beta_source_allowed(version, ref))
    if not allowed:
        raise ValueError('Unauthorized release source/channel')
    return ref


def inventory(root, run, repository, version, channel, windows=False, macos=False, linux=False):
    root = Path(root)
    manifest = load(root / MANIFEST_NAME)
    validate_manifest(manifest, asset_root=root)
    identity = manifest['release']
    if any(identity[key] != value for key, value in (
            ('version', version), ('channel', channel), ('commit', run['head_sha']), ('repository', repository))):
        raise ValueError('Manifest identity differs from the qualified source')
    # Preserve the existing full stable matrix. Beta has all direct packages,
    # optional explicitly selected patch pairs, and no Store upload.
    stable = channel == 'stable'
    expected = expected_asset_keys(include_windows_patches=stable or windows,
        include_macos_patches=stable or macos, include_linux_patches=stable or linux,
        include_store_upload=stable)
    actual = {(a['platform'], a['architecture'], a['packageKind']) for a in manifest['assets']}
    if actual != expected or len(actual) != len(manifest['assets']):
        raise ValueError('Draft asset roles differ from the requested complete matrix')
    paths = collect_assets(root)
    if (len(list(root.rglob(MANIFEST_NAME))) != 1 or (root / MANIFEST_NAME).is_symlink() or
            {p.name for p in paths} != {a['name'] for a in manifest['assets']}):
        raise ValueError('Unexpected, duplicated or missing draft files')
    return manifest


def github(endpoint):
    result = subprocess.run(['gh', 'api', endpoint], check=True, capture_output=True, text=True, timeout=30)
    return json.loads(result.stdout)


def verify_tag(repository, tag, commit, lookup=github):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
        raise ValueError('Invalid repository')
    if not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+-[A-Za-z0-9.-]+', tag):
        raise ValueError('Invalid beta tag')
    reference = lookup(f'repos/{repository}/git/ref/tags/{quote(tag, safe="")}')
    if reference.get('ref') != 'refs/tags/' + tag:
        raise ValueError('Exact beta tag is required before assembly')
    obj = reference.get('object', {})
    seen = set()
    for _ in range(8):
        sha = obj.get('sha')
        if not isinstance(sha, str) or not re.fullmatch(r'[0-9a-f]{40}', sha) or sha in seen:
            raise ValueError('Invalid/cyclic tag object')
        seen.add(sha)
        if obj.get('type') == 'commit':
            if sha != commit:
                raise ValueError('Existing beta tag points to another commit')
            return
        if obj.get('type') != 'tag':
            raise ValueError('Beta tag must resolve to a commit')
        annotated = lookup(f'repos/{repository}/git/tags/{sha}')
        if annotated.get('sha') != sha:
            raise ValueError('Tag object identity differs')
        obj = annotated.get('object', {})
    raise ValueError('Tag nesting limit exceeded')


def target(release, manifest, lookup=github):
    identity = manifest['release']
    beta = identity['channel'] == 'beta'
    expected_target = 'Dev' if beta else identity['commit']
    if (release.get('draft') is not True or release.get('prerelease') is not beta or
            release.get('tag_name') != identity['tag'] or release.get('target_commitish') != expected_target or
            type(release.get('id')) is not int or release['id'] <= 0):
        raise ValueError('Existing draft identity/channel/target differs')
    if beta:
        verify_tag(identity['repository'], identity['tag'], identity['commit'], lookup)


def remote_assets(root, pages, *, complete=False):
    root = Path(root).resolve(strict=True)
    if not isinstance(pages, list) or not all(isinstance(page, list) for page in pages):
        raise ValueError('Expected fully paginated GitHub asset arrays')
    expected = {p.name: p for p in collect_assets(root)}
    expected[MANIFEST_NAME] = Path(root) / MANIFEST_NAME
    seen = set()
    for asset in (item for page in pages for item in page):
        if not isinstance(asset, dict):
            raise ValueError('Invalid GitHub asset')
        name = asset.get('name')
        if name not in expected or name in seen:
            raise ValueError('Unexpected or duplicate remote draft asset')
        path = expected[name]
        if (asset.get('state') != 'uploaded' or type(asset.get('size')) is not int or
                asset['size'] != path.stat().st_size or asset.get('digest') != 'sha256:' + sha256_file(path)):
            raise ValueError('Remote asset differs; refusing overwrite')
        seen.add(name)
    if complete and seen != set(expected):
        raise ValueError('Uploaded draft inventory is incomplete')
    return [str(p) for name, p in sorted(expected.items()) if name not in seen]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subs = parser.add_subparsers(dest='command', required=True)
    source = subs.add_parser('source')
    for name in ('run', 'repository', 'version', 'channel', 'dispatch-ref'):
        source.add_argument('--' + name, required=True)
    inv = subs.add_parser('inventory')
    for name in ('root', 'run', 'repository', 'version', 'channel'):
        inv.add_argument('--' + name, required=True)
    for platform in ('windows', 'macos', 'linux'):
        inv.add_argument('--' + platform + '-patches', action='store_true')
    draft = subs.add_parser('target')
    draft.add_argument('--release', required=True)
    draft.add_argument('--manifest', required=True)
    assets = subs.add_parser('assets')
    assets.add_argument('--root', required=True)
    assets.add_argument('--pages', required=True)
    assets.add_argument('--complete', action='store_true')
    args = parser.parse_args()
    try:
        if args.command == 'source':
            print(source_ref(load(args.run), args.repository, args.version, args.channel, args.dispatch_ref))
        elif args.command == 'inventory':
            inventory(args.root, load(args.run), args.repository, args.version, args.channel,
                      args.windows_patches, args.macos_patches, args.linux_patches)
        elif args.command == 'target':
            manifest = load(args.manifest)
            validate_manifest(manifest)
            target(load(args.release), manifest)
        else:
            for path in remote_assets(args.root, load(args.pages), complete=args.complete):
                print(path)
    except (ValueError, KeyError, TypeError, AttributeError, OSError, subprocess.SubprocessError) as error:
        parser.exit(1, f'Draft validation refused: {error}\n')


if __name__ == '__main__':
    main()
