import importlib.util
import io
import json
import tempfile
import unittest
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("verify_store_upload", ROOT / "scripts/verify_store_upload.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class StoreUploadTests(unittest.TestCase):
    def package(self, directory, version="1.8.9.0", executable=True):
        nested = io.BytesIO()
        manifest = f'''<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
        <Identity Name="FlavioGiacchetti.480851279F98B" Publisher="CN=D0FF8AE9-15EE-487F-B2F3-0913EFDA0CED"
        Version="{version}" ProcessorArchitecture="x64"/>
        <Applications><Application Executable="VaultSync.UI\\VaultSync.UI.exe"/></Applications></Package>'''
        with zipfile.ZipFile(nested, "w") as package:
            package.writestr("AppxManifest.xml", manifest)
            if executable:
                package.writestr("VaultSync.UI/VaultSync.UI.exe", b"fixture")
            package.writestr("VaultSync.UI/VaultSync.UI.runtimeconfig.json", json.dumps(
                {"runtimeOptions": {"includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.12"}]}}))
        upload = Path(directory) / "test.msixupload"
        with zipfile.ZipFile(upload, "w") as outer:
            outer.writestr("test.msix", nested.getvalue())
        return upload

    def test_valid_upload(self):
        with tempfile.TemporaryDirectory() as directory:
            result = MODULE.verify(self.package(directory), "1.8.9", ROOT / "packaging/VaultSync.Store/Package.appxmanifest")
            self.assertEqual(result["identity"]["Version"], "1.8.9.0")

    def test_wrong_version_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, "Version"):
                MODULE.verify(self.package(directory, "1.8.8.0"), "1.8.9", ROOT / "packaging/VaultSync.Store/Package.appxmanifest")

    def test_missing_executable_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, "executable"):
                MODULE.verify(self.package(directory, executable=False), "1.8.9", ROOT / "packaging/VaultSync.Store/Package.appxmanifest")
