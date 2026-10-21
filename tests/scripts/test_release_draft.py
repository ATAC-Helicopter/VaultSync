import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
import release_draft as draft
from release_manifest import MANIFEST_NAME, build_manifest, sha256_file
from release_source import PREPARATION_REF

REPO = 'ATAC-Helicopter/VaultSync'
SHA = 'a' * 40
VERSION = '1.9.0-beta.1'
PACKAGES = ['VaultSync-Setup-{}.exe', 'VaultSync-{}-macos-apple-silicon.dmg',
            'VaultSync-{}-macos-intel.dmg', 'VaultSync-{}-linux-x64.tar.gz',
            'VaultSync-{}-linux-x64.deb', 'VaultSync-{}-linux-x64.AppImage',
            'VaultSync-{}-linux-arm64.tar.gz', 'VaultSync-{}-linux-arm64.deb']
PATCHES = [f'vaultsync-patch-{platform}.{extension}'
           for platform in ('windows', 'macos-apple-silicon', 'macos-intel', 'linux-x64', 'linux-arm64')
           for extension in ('json', 'zip')]


def run():
    return {'status': 'completed', 'conclusion': 'success', 'event': 'workflow_dispatch',
            'path': '.github/workflows/release-assets.yml', 'head_sha': SHA,
            'head_branch': PREPARATION_REF.removeprefix('refs/heads/'),
            'repository': {'full_name': REPO}, 'head_repository': {'full_name': REPO}}


def fixture(root, stable=False, patches=False, store=False):
    version = '1.9.0' if stable else VERSION
    names = [name.format(version) for name in PACKAGES]
    if patches:
        names += PATCHES
    if store:
        names += [f'VaultSync-Store-{version}-x64.msixupload']
    for name in names:
        (root / name).write_bytes(('fixture:' + name).encode())
    manifest = build_manifest(root, version=version, channel='stable' if stable else 'beta',
        commit=SHA, repository=REPO, predecessors=['1.8.9'], include_windows_patches=patches,
        include_macos_patches=patches, include_linux_patches=patches, include_store_upload=store)
    (root / MANIFEST_NAME).write_text(json.dumps(manifest))
    return manifest


def release(manifest):
    beta = manifest['release']['channel'] == 'beta'
    return {'id': 123, 'draft': True, 'prerelease': beta, 'tag_name': manifest['release']['tag'],
            'target_commitish': 'Dev' if beta else SHA}


class DraftTests(unittest.TestCase):
    def test_source_accepts_only_qualified_same_repository_and_branch(self):
        self.assertEqual(PREPARATION_REF, draft.source_ref(run(), REPO, VERSION, 'beta', PREPARATION_REF))
        for key, value in [('status', 'in_progress'), ('conclusion', 'failure'), ('event', 'pull_request'),
                           ('path', '.github/workflows/other.yml'), ('head_sha', 'a' * 39),
                           ('repository', {'full_name': 'other/repo'}),
                           ('head_repository', {'full_name': 'other/repo'}), ('head_branch', 'Dev')]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                source = run(); source[key] = value
                draft.source_ref(source, REPO, VERSION, 'beta', PREPARATION_REF)
        for version, channel in [('1.9.0', 'beta'), ('1.9.1-beta.1', 'beta'), (VERSION, 'stable')]:
            with self.subTest(version=version, channel=channel), self.assertRaises(ValueError):
                draft.source_ref(run(), REPO, version, channel, PREPARATION_REF)

    def test_stable_source_remains_stable_only(self):
        source = run(); source['head_branch'] = 'Stable'
        self.assertEqual('refs/heads/Stable', draft.source_ref(source, REPO, '1.9.0', 'stable', 'refs/heads/Stable'))
        with self.assertRaises(ValueError):
            draft.source_ref(source, REPO, '1.9.0', 'stable', PREPARATION_REF)

    def test_beta_inventory_requires_all_eight_direct_packages_without_store(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); manifest = fixture(root)
            self.assertEqual(manifest, draft.inventory(root, run(), REPO, VERSION, 'beta'))
            (root / PACKAGES[0].format(VERSION)).unlink()
            with self.assertRaises(ValueError):
                draft.inventory(root, run(), REPO, VERSION, 'beta')

    def test_beta_explicit_patch_matrix_and_stable_full_store_matrix(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); fixture(root, patches=True)
            draft.inventory(root, run(), REPO, VERSION, 'beta', True, True, True)
            with self.assertRaises(ValueError):
                draft.inventory(root, run(), REPO, VERSION, 'beta')
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); fixture(root, store=True)
            with self.assertRaises(ValueError):
                draft.inventory(root, run(), REPO, VERSION, 'beta')
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); manifest = fixture(root, stable=True, patches=True, store=True)
            self.assertEqual(19, len(manifest['assets']))
            draft.inventory(root, run(), REPO, '1.9.0', 'stable')

    def test_inventory_refuses_wrong_source_channel_extra_links_and_changed_bytes(self):
        for fault in ('commit', 'channel', 'extra', 'link', 'changed'):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); fixture(root); source = run(); channel = 'beta'
                if fault == 'commit': source['head_sha'] = 'b' * 40
                if fault == 'channel': channel = 'stable'
                if fault == 'extra': (root / 'unexpected.iso').write_bytes(b'extra')
                if fault == 'link': (root / 'linked.deb').symlink_to(root / PACKAGES[4].format(VERSION))
                if fault == 'changed': (root / PACKAGES[0].format(VERSION)).write_bytes(b'changed')
                with self.assertRaises(ValueError):
                    draft.inventory(root, source, REPO, VERSION, channel)

    def test_beta_tag_resolves_lightweight_or_annotated_exact_commit(self):
        tag = 'v' + VERSION
        draft.verify_tag(REPO, tag, SHA, lambda _: {'ref': 'refs/tags/' + tag, 'object': {'type': 'commit', 'sha': SHA}})
        calls = []
        def lookup(endpoint):
            calls.append(endpoint)
            if len(calls) == 1:
                return {'ref': 'refs/tags/' + tag, 'object': {'type': 'tag', 'sha': 'b' * 40}}
            return {'sha': 'b' * 40, 'object': {'type': 'commit', 'sha': SHA}}
        draft.verify_tag(REPO, tag, SHA, lookup)
        self.assertEqual(f'repos/{REPO}/git/tags/' + 'b' * 40, calls[1])

    def test_tag_refuses_wrong_commit_ref_type_cycle_or_excessive_nesting(self):
        tag = 'v' + VERSION
        for fault in ('commit', 'ref', 'type', 'cycle', 'deep', 'object-sha'):
            count = 0
            def lookup(endpoint):
                nonlocal count
                count += 1
                if count == 1:
                    return {'ref': 'refs/tags/' + (tag if fault != 'ref' else 'other'), 'object': {
                        'type': 'tree' if fault == 'type' else 'commit' if fault in ('commit', 'ref') else 'tag',
                        'sha': 'b' * 40}}
                return {'sha': endpoint.rsplit('/', 1)[-1] if fault != 'object-sha' else 'c' * 40,
                        'object': {'type': 'tag', 'sha': 'b' * 40 if fault != 'deep' else format(count, '040x')}}
            with self.subTest(fault=fault), self.assertRaises(ValueError):
                draft.verify_tag(REPO, tag, SHA, lookup)

    def test_beta_target_requires_existing_private_prerelease_and_dev_marker(self):
        with tempfile.TemporaryDirectory() as tmp:
            manifest = fixture(Path(tmp)); valid = release(manifest)
            lookup = lambda _: {'ref': 'refs/tags/v' + VERSION, 'object': {'type': 'commit', 'sha': SHA}}
            draft.target(valid, manifest, lookup)
            for key, value in [('draft', False), ('prerelease', False), ('target_commitish', SHA),
                               ('tag_name', 'v1.9.0-beta.2'), ('id', True)]:
                with self.subTest(key=key), self.assertRaises(ValueError):
                    changed = copy.deepcopy(valid); changed[key] = value
                    draft.target(changed, manifest, lookup)
            with self.assertRaises(ValueError):
                draft.target(valid, manifest, lambda _: {'ref': 'refs/tags/v' + VERSION,
                    'object': {'type': 'commit', 'sha': 'b' * 40}})

    def test_stable_target_preserves_exact_sha_and_never_queries_beta_tags(self):
        with tempfile.TemporaryDirectory() as tmp:
            manifest = fixture(Path(tmp), stable=True, patches=True, store=True)
            draft.target(release(manifest), manifest, lambda _: self.fail('Unexpected beta tag lookup'))
            wrong = release(manifest); wrong['target_commitish'] = 'Stable'
            with self.assertRaises(ValueError): draft.target(wrong, manifest)

    def test_complete_paginated_remote_inventory_and_safe_missing_upload_list(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); fixture(root)
            paths = sorted(root.iterdir())
            assets = [{'name': p.name, 'state': 'uploaded', 'size': p.stat().st_size,
                       'digest': 'sha256:' + sha256_file(p)} for p in paths]
            self.assertEqual([], draft.remote_assets(root, [assets[:4], assets[4:]], complete=True))
            missing = draft.remote_assets(root, [assets[:4]])
            self.assertEqual(sorted(str(p.resolve()) for p in paths[4:]), missing)
            with self.assertRaises(ValueError): draft.remote_assets(root, [assets[:4]], complete=True)
            for fault in ('size', 'digest', 'state', 'duplicate', 'extra', 'unpaginated'):
                changed = copy.deepcopy(assets)
                if fault == 'duplicate': changed += [changed[0]]
                elif fault == 'extra': changed[0]['name'] = 'unlisted.iso'
                elif fault != 'unpaginated': changed[0][fault] = 'wrong'
                with self.subTest(fault=fault), self.assertRaises(ValueError):
                    draft.remote_assets(root, changed if fault == 'unpaginated' else [changed], complete=True)

    def test_duplicate_json_members_refuse(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'duplicate.json'; path.write_text('{"draft":true,"draft":false}')
            with self.assertRaises(ValueError): draft.load(path)

    def test_actual_workflow_draft_dispatch_guard(self):
        workflow = (ROOT / '.github/workflows/release-assets.yml').read_text()
        block = workflow[workflow.index('          if [[ -n "$ASSEMBLE_DRAFT" ]]; then'):workflow.index('          trim() {')]
        for channel, ref, version, store, candidate, accepted in (
            ('beta', PREPARATION_REF, VERSION, 'false', 'false', True),
            ('beta', PREPARATION_REF, VERSION, 'true', 'false', False),
            ('beta', 'refs/heads/work/other', VERSION, 'false', 'false', False),
            ('beta', PREPARATION_REF, VERSION, 'false', 'true', False),
            ('stable', 'refs/heads/Stable', '1.9.0', 'true', 'false', True),
            ('stable', PREPARATION_REF, '1.9.0', 'false', 'false', False)):
            with self.subTest(channel=channel, ref=ref, store=store, candidate=candidate):
                result = subprocess.run(['bash', '-c', 'set -euo pipefail\n' + block], cwd=ROOT,
                    env={**os.environ, 'ASSEMBLE_DRAFT': '123', 'channel': channel, 'target': version,
                         'branch': ref.removeprefix('refs/heads/'), 'GITHUB_REF': ref,
                         'INCLUDE_STORE_UPLOAD': store, 'release_candidate': candidate}, capture_output=True, text=True)
                self.assertEqual(accepted, result.returncode == 0, result.stderr)

    def test_actual_upload_shell_resumes_exact_beta_inventory_and_refuses_unsafe_drafts(self):
        workflow = (ROOT / '.github/workflows/release-assets.yml').read_text()
        step = workflow.split('      - name: Verify assets and upload only to the existing draft\n', 1)[1]
        shell = textwrap.dedent(step.split('        run: |\n', 1)[1])
        fake_gh = '''import hashlib,json,os,sys
from pathlib import Path
p=Path(os.environ['MOCK_STATE']); state=json.loads(p.read_text()); args=sys.argv[1:]
with Path(os.environ['MOCK_CALLS']).open('a') as log: log.write(json.dumps(args)+'\\n')
if args[:2]==['release','upload']:
 for name in args[3:]:
  f=Path(name); state['assets'].append({'name':f.name,'state':'uploaded','size':f.stat().st_size,'digest':'sha256:'+hashlib.sha256(f.read_bytes()).hexdigest()})
 p.write_text(json.dumps(state))
elif args[0]=='api':
 endpoint=args[-1]
 if '/git/ref/tags/' in endpoint: value=state['tag']
 elif endpoint.endswith('/assets'): value=[state['assets']]
 elif '/releases/tags/' in endpoint: value=state['release']
 else: sys.exit(99)
 print(json.dumps(value))
else: sys.exit(98)
'''
        for fault in ('none', 'complete', 'patches', 'tag', 'existing-bytes', 'published'):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); assets = root / 'release-assets'; assets.mkdir()
                manifest = fixture(assets, patches=fault == 'patches')
                (root / 'source-run.json').write_text(json.dumps(run()))
                (root / 'scripts').symlink_to(ROOT / 'scripts', target_is_directory=True)
                executable = root / 'gh'
                executable.write_text('#!' + sys.executable + '\n' + fake_gh); executable.chmod(0o700)
                first = sorted(assets.iterdir())[0]
                state = {'release': release(manifest), 'tag': {'ref': 'refs/tags/v' + VERSION,
                    'object': {'type': 'commit', 'sha': SHA}}, 'assets': [{'name': first.name,
                    'state': 'uploaded', 'size': first.stat().st_size, 'digest': 'sha256:' + sha256_file(first)}]}
                if fault == 'complete':
                    state['assets'] = [{'name': p.name, 'state': 'uploaded', 'size': p.stat().st_size, 'digest': 'sha256:' + sha256_file(p)} for p in sorted(assets.iterdir())]
                if fault == 'tag': state['tag']['object']['sha'] = 'b' * 40
                if fault == 'existing-bytes': state['assets'][0]['digest'] = 'sha256:' + '0' * 64
                if fault == 'published': state['release']['draft'] = False
                state_path = root / 'state.json'; state_path.write_text(json.dumps(state))
                calls = root / 'calls.jsonl'
                result = subprocess.run(['bash', '-c', shell], cwd=root, capture_output=True, text=True,
                    env={**os.environ, 'PATH': str(root) + os.pathsep + os.environ['PATH'],
                         'MOCK_STATE': str(state_path), 'MOCK_CALLS': str(calls), 'GITHUB_REPOSITORY': REPO,
                         'TARGET_VERSION': VERSION, 'RELEASE_CHANNEL': 'beta',
                         **{p + '_PATCHES': 'true' if fault == 'patches' else 'false'
                            for p in ('WINDOWS', 'MACOS', 'LINUX')}})
                self.assertTrue(calls.exists(), result.stderr)
                commands = [json.loads(line) for line in calls.read_text().splitlines()]
                uploads = [c for c in commands if c[:2] == ['release', 'upload']]
                if fault in ('none', 'complete', 'patches'):
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertEqual(0 if fault == 'complete' else 1, len(uploads))
                    if uploads:
                        self.assertEqual(len(manifest['assets']), len(uploads[0][3:]))
                        self.assertNotIn(first.name, [Path(p).name for p in uploads[0][3:]])
                    self.assertTrue(json.loads(state_path.read_text())['release']['draft'])
                else:
                    self.assertNotEqual(0, result.returncode)
                    self.assertEqual([], uploads)
                self.assertFalse(any(c[:2] in (['release', 'create'], ['release', 'edit'], ['release', 'delete']) for c in commands))


if __name__ == '__main__':
    unittest.main()
