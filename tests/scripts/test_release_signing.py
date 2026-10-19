"""Synthetic boundary tests; actual Sigstore evidence is recorded separately."""
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import release_manifest
import release_signing

NAMES = ['VaultSync-Setup-1.9.0.exe', 'VaultSync-1.9.0-macos-apple-silicon.dmg',
         'VaultSync-1.9.0-macos-intel.dmg', 'VaultSync-1.9.0-linux-x64.tar.gz',
         'VaultSync-1.9.0-linux-x64.deb', 'VaultSync-1.9.0-linux-x64.AppImage',
         'VaultSync-1.9.0-linux-arm64.tar.gz', 'VaultSync-1.9.0-linux-arm64.deb',
         'vaultsync-patch-windows.json', 'vaultsync-patch-windows.zip']
REPOSITORY = 'ATAC-Helicopter/VaultSync'
COMMIT = 'a' * 40
REF = 'refs/heads/Stable'


class SigningTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'assets'
        self.root.mkdir()
        for name in NAMES:
            (self.root / name).write_bytes(name.encode())
        self.manifest = release_manifest.build_manifest(
            self.root, version='1.9.0', channel='stable', commit=COMMIT,
            repository=REPOSITORY, predecessors=['1.8.9'], include_windows_patches=True)
        self.write_manifest()

    def write_manifest(self):
        (self.root / release_manifest.MANIFEST_NAME).write_text(json.dumps(self.manifest))

    def subjects(self, **kwargs):
        return release_signing.subjects(self.root, kwargs.get('repo', REPOSITORY),
                                       kwargs.get('commit', COMMIT), kwargs.get('ref', REF))

    def test_manifest_and_both_patch_files_are_signed_without_changing_updater_schema(self):
        before = (self.root / release_manifest.MANIFEST_NAME).read_bytes()
        self.assertEqual({p.name for p in self.subjects()}, set(NAMES) | {release_manifest.MANIFEST_NAME})
        self.assertEqual(before, (self.root / release_manifest.MANIFEST_NAME).read_bytes())
        release_manifest.validate_manifest(self.manifest, asset_root=self.root)

    def test_altered_missing_and_unlisted_assets_fail_before_verifier(self):
        for mode in ('altered', 'missing', 'unlisted'):
            with self.subTest(mode=mode):
                target = self.root / NAMES[0]
                target.write_bytes(NAMES[0].encode())
                extra = self.root / 'vaultsync-patch-linux-x64.zip'
                extra.unlink(missing_ok=True)
                if mode == 'altered':
                    target.write_bytes(b'changed')
                elif mode == 'missing':
                    target.unlink()
                else:
                    extra.write_bytes(b'extra')
                with self.assertRaises(ValueError):
                    self.subjects()

    def test_wrong_commit_repository_and_ref_are_rejected(self):
        for values in ({'commit': 'b'*40}, {'repo': 'other/VaultSync'}, {'ref': 'refs/heads/Dev'}):
            with self.assertRaises(ValueError):
                self.subjects(**values)
        self.assertTrue(self.subjects(ref='refs/heads/release/1.9.0'))

    def test_linked_assets_and_duplicate_manifest_are_rejected(self):
        linked = self.root / 'nested'
        linked.mkdir()
        (linked / release_manifest.MANIFEST_NAME).write_text('{}')
        with self.assertRaises(ValueError):
            self.subjects()
        (linked / release_manifest.MANIFEST_NAME).unlink()
        (linked / NAMES[0]).symlink_to(self.root / NAMES[0])
        with self.assertRaises(ValueError):
            self.subjects()

    def test_verification_binds_workflow_ref_commit_and_retained_bundle(self):
        bundle = Path(self.temp.name) / 'bundle.json'
        roots = Path(self.temp.name) / 'root.jsonl'
        bundle.write_text('proof')
        roots.write_text('root')
        paths = self.subjects()
        with patch('release_signing.subprocess.run') as run:
            release_signing.verify(paths, REPOSITORY, COMMIT, REF, bundle, roots)
        self.assertEqual(len(paths)*2, run.call_count)
        for call in run.call_args_list:
            argv = call.args[0]
            self.assertEqual(COMMIT, argv[argv.index('--source-digest')+1])
            self.assertEqual(REF, argv[argv.index('--source-ref')+1])
            self.assertIn('--deny-self-hosted-runners', argv)
        self.assertIn('--bundle', run.call_args.args[0])
        bundle.unlink()
        with patch('release_signing.subprocess.run') as run:
            with self.assertRaises(ValueError):
                release_signing.verify(paths, REPOSITORY, COMMIT, REF, bundle, roots)
            run.assert_not_called()

    def test_duplicate_json_members_are_rejected(self):
        p = self.root / release_manifest.MANIFEST_NAME
        text = p.read_text()
        p.write_text(text.replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1'))
        with self.assertRaises(ValueError):
            self.subjects()

    def test_beta_requires_dev_source(self):
        for path in list(self.root.iterdir()):
            path.unlink()
        for name in NAMES:
            (self.root / name.replace('1.9.0', '1.9.0-beta.1')).write_bytes(name.encode())
        self.manifest = release_manifest.build_manifest(
            self.root, version='1.9.0-beta.1', channel='beta', commit=COMMIT,
            repository=REPOSITORY, predecessors=['1.8.9'], include_windows_patches=True)
        self.write_manifest()
        self.assertTrue(self.subjects(ref='refs/heads/Dev'))
        with self.assertRaises(ValueError):
            self.subjects()

    def test_untrusted_source_arguments_fail_before_any_verifier_process(self):
        proof = Path(self.temp.name) / 'proof'
        proof.write_text('proof')
        for repo, commit, ref in [('--repo=other/private', COMMIT, REF),
                                  (REPOSITORY, '--help', REF),
                                  (REPOSITORY, COMMIT, 'refs/heads/Stable;touch x'),
                                  (REPOSITORY + '\nprivate', COMMIT, REF)]:
            with self.subTest(repo=repo, commit=commit, ref=ref), patch('release_signing.subprocess.run') as run:
                with self.assertRaises(ValueError):
                    release_signing.verify(self.subjects(), repo, commit, ref, proof, proof)
                run.assert_not_called()

    def test_verifier_uses_absolute_subject_and_proof_paths_and_requires_cli(self):
        proof = Path(self.temp.name) / 'proof'
        proof.write_text('proof')
        with patch('release_signing.shutil.which', return_value=None), patch('release_signing.subprocess.run') as run:
            with self.assertRaises(ValueError):
                release_signing.verify(self.subjects(), REPOSITORY, COMMIT, REF, proof, proof)
            run.assert_not_called()
        with patch('release_signing.shutil.which', return_value=sys.executable), patch('release_signing.subprocess.run') as run:
            release_signing.verify(self.subjects(), REPOSITORY, COMMIT, REF, proof, proof, online=False)
            self.assertEqual(len(self.subjects()), run.call_count)
            for call in run.call_args_list:
                command = call.args[0]
                self.assertTrue(Path(command[0]).is_absolute())
                self.assertTrue(Path(command[3]).is_absolute())
                self.assertTrue(Path(command[command.index('--bundle') + 1]).is_absolute())

    def test_cli_subject_inventory_is_complete_and_existing_output_is_preserved(self):
        output = Path(self.temp.name) / 'proof' / 'subjects.sha256'
        argv = ['release_signing.py', 'subjects', '--asset-root', str(self.root),
                '--repository', REPOSITORY, '--commit', COMMIT, '--source-ref', REF,
                '--output', str(output)]
        with patch.object(sys, 'argv', argv):
            release_signing.main()
        original = output.read_bytes()
        self.assertEqual(len(self.subjects()), len(original.decode().splitlines()))
        self.assertIn('vaultsync-patch-windows.json', original.decode())
        self.assertIn('vaultsync-patch-windows.zip', original.decode())
        with patch.object(sys, 'argv', argv), self.assertRaises(FileExistsError):
            release_signing.main()
        self.assertEqual(output.read_bytes(), original)

    def test_cli_verify_passes_exact_trusted_source_and_proof_to_the_verifier(self):
        proof = Path(self.temp.name) / 'proof'
        proof.write_text('proof')
        argv = ['release_signing.py', 'verify', '--asset-root', str(self.root),
                '--repository', REPOSITORY, '--commit', COMMIT, '--source-ref', REF,
                '--bundle', str(proof), '--trusted-root', str(proof)]
        with patch.object(sys, 'argv', argv), patch('release_signing.verify') as verify:
            release_signing.main()
        self.assertEqual(verify.call_args.args, (self.subjects(), REPOSITORY, COMMIT, REF, proof, proof))

    def test_invalid_signature_aborts_without_verifying_remaining_files(self):
        import subprocess
        proof = Path(self.temp.name) / 'proof'
        proof.write_text('proof')
        with patch('release_signing.subprocess.run', side_effect=subprocess.CalledProcessError(1, 'gh')) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                release_signing.verify(self.subjects(), REPOSITORY, COMMIT, REF, proof, proof)
            self.assertEqual(1, run.call_count)
