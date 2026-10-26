"""Regression guards for the sandbox startup crash and private QA isolation."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import build_macos_qa_app as qa  # noqa: E402


class MacosQaPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / "published"
        self.source.mkdir()
        for name in qa.REQUIRED:
            (self.source / name).write_bytes(b"\xcf\xfa\xed\xfe")
        (self.source / "VaultSync.UI").chmod(0o755)
        self.runtime("10.0.13")
        self.output = self.root / "Recovery QA.app"

    def tearDown(self):
        self.temporary.cleanup()

    def runtime(self, version):
        (self.source / "VaultSync.UI.runtimeconfig.json").write_text(json.dumps({
            "runtimeOptions": {"includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": version}]}}))

    def validate(self, **overrides):
        args = {"publish_dir": self.source, "output": self.output,
                "identifier": "dev.vaultsync.qa.recovery", "version": "1.9.0"}
        args.update(overrides)
        return qa.validate(**args)

    def test_required_gui_registration_is_narrow_and_other_access_stays_denied(self):
        entitlements = qa.sandbox_entitlements()
        self.assertEqual(entitlements["com.apple.security.temporary-exception.mach-lookup.global-name"],
                         ["com.apple.coreservices.launchservicesd"])
        self.assertTrue(entitlements["com.apple.security.app-sandbox"])
        self.assertNotIn("com.apple.security.network.client", entitlements)
        self.assertNotIn("com.apple.security.network.server", entitlements)
        self.assertEqual(len(entitlements), 3)

    def test_production_identifiers_and_existing_artifacts_are_preserved(self):
        for name in ["com.vaultsync.app", "VaultSync.UI", "dev.vaultsync.qa...", "dev.vaultsync.qa./outside",
                     "--requirements=malicious", "dev.vaultsync.qa.x;echo injected",
                     "dev.vaultsync.qa.x\n--options=runtime"]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.validate(identifier=name)
        self.output.mkdir()
        sentinel = self.output / "keep.txt"
        sentinel.write_text("existing package")
        with self.assertRaises(ValueError):
            self.validate()
        self.assertEqual(sentinel.read_text(), "existing package")

    def test_incomplete_wrong_platform_old_and_framework_dependent_inputs_refuse(self):
        self.runtime("10.0.12")
        with self.assertRaises(ValueError):
            self.validate()
        self.runtime("10.0.13")
        (self.source / "VaultSync.UI").write_bytes(b"\x7fELF")
        with self.assertRaises(ValueError):
            self.validate()
        (self.source / "VaultSync.UI").write_bytes(b"\xcf\xfa\xed\xfe")
        (self.source / "libcoreclr.dylib").unlink()
        with self.assertRaises(ValueError):
            self.validate()

    def test_aliases_and_recursive_output_refuse_without_writing(self):
        (self.source / "outside").symlink_to(self.root)
        with self.assertRaises(ValueError):
            self.validate()
        (self.source / "outside").unlink()
        with self.assertRaises(ValueError):
            self.validate(output=self.source / "nested.app")
        self.assertFalse(self.output.exists())

    def test_failed_signing_removes_only_its_new_output(self):
        with patch.object(qa.sys, "platform", "darwin"), patch.object(qa.subprocess, "run", side_effect=OSError("signing unavailable")):
            with self.assertRaises(OSError):
                qa.build(self.source, self.output, "dev.vaultsync.qa.recovery", "1.9.0")
        self.assertFalse(self.output.exists())
        self.assertTrue((self.source / "VaultSync.UI").is_file())

    def test_bundle_uses_private_container_and_shared_version_mapping(self):
        import plistlib
        with patch.object(qa.sys, "platform", "darwin"), patch.object(qa.subprocess, "run") as signing:
            result = qa.build(self.source, self.output, "dev.vaultsync.qa.recovery", "1.9.0-beta.1")
        info = plistlib.loads((self.output / "Contents/Info.plist").read_bytes())
        self.assertEqual(info["CFBundleVersion"], "1.9.1")
        self.assertIn("/Containers/dev.vaultsync.qa.recovery/Data/", info["LSEnvironment"]["VAULTSYNC_CONFIG_DIR"])
        self.assertEqual(signing.call_count, 2)
        self.assertEqual(signing.call_args_list[0].args[0], [
            "/usr/bin/codesign", "--force", "--deep", "--sign", "-",
            "--entitlements", "Contents/Resources/qa-signing.plist", "."])
        self.assertEqual(signing.call_args_list[1].args[0], [
            "/usr/bin/codesign", "--verify", "--deep", "--strict", "."])
        for call in signing.call_args_list:
            self.assertEqual(call.kwargs["cwd"], self.output.resolve())
            self.assertNotIn("shell", call.kwargs)
        self.assertFalse(result["releaseAsset"])
        self.assertTrue(result["sandbox"])
