from contextlib import chdir, redirect_stderr
import hashlib
import io
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import assemble_recovery_disk as disk


@unittest.skipUnless(sys.platform == "linux", "the disk assembler requires Linux")
class AssembleRecoveryDiskTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.enterContext(chdir(directory.name))
        self.esp = Path("esp.fat")
        with self.esp.open("xb") as stream:
            stream.write(b"test EFI")
            stream.truncate(disk.ESP_BYTES)
        self.root = Path("root.ext4")
        self.root.write_bytes(b"test root".ljust(disk.SECTOR, b"\0"))
        self.output = Path("recovery.raw")
        self.args = ["--esp-sha256", self.digest(self.esp), "--root-sha256", self.digest(self.root),
                     "--disk-guid", "10000000-0000-0000-0000-000000000001",
                     "--esp-guid", "10000000-0000-0000-0000-000000000002",
                     "--root-guid", "10000000-0000-0000-0000-000000000003"]

    @staticmethod
    def digest(path):
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest()

    def test_bad_digest_or_aliased_input_never_publishes_an_image(self):
        args = self.args.copy()
        args[1] = "0" * 64
        with redirect_stderr(io.StringIO()) as stderr, self.assertRaises(SystemExit) as failure:
            disk.main(args)
        self.assertEqual(failure.exception.code, 1)
        self.assertIn("does not match its expected SHA-256", stderr.getvalue())
        self.assertEqual(set(Path.cwd().iterdir()), {self.esp.absolute(), self.root.absolute()})
        original = self.esp.rename("original.fat")
        self.esp.symlink_to(original)
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            disk.main(self.args)
        self.assertFalse(self.output.exists())

    def test_verified_payloads_are_placed_and_output_cannot_be_replaced(self):
        def write_partition_table(argv, *, cwd, check):
            self.assertEqual((argv[0], argv[-1]), ("sgdisk", "media.raw"))
            self.assertTrue(check)
            with (Path(cwd) / argv[-1]).open("rb") as image:
                image.seek(disk.ESP_START * disk.SECTOR)
                self.assertEqual(image.read(8), b"test EFI")
                image.seek(disk.ROOT_START * disk.SECTOR)
                self.assertEqual(image.read(9), b"test root")

        if shutil.which("sgdisk"):
            disk.main(self.args)
            with self.output.open("rb") as image:
                image.seek(disk.SECTOR)
                self.assertEqual(image.read(8), b"EFI PART")
        else:
            with patch.object(disk.subprocess, "run", side_effect=write_partition_table):
                disk.main(self.args)
        with self.output.open("rb") as image:
            image.seek(disk.ROOT_START * disk.SECTOR)
            self.assertEqual(image.read(9), b"test root")
        with redirect_stderr(io.StringIO()) as stderr, self.assertRaises(SystemExit):
            disk.main(self.args)
        self.assertIn("output already exists", stderr.getvalue())

    def test_combined_media_verifies_and_places_the_restore_partition(self):
        restore = Path("restore.ext4")
        payload = b"test restore".ljust(disk.SECTOR, b"\0")
        restore.write_bytes(payload)
        args = self.args + ["--restore-guid", "10000000-0000-0000-0000-000000000004"]
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            disk.main(args + ["--restore-sha256", "0" * 64])
        self.assertFalse(self.output.exists())
        with patch.object(disk.subprocess, "run") as sgdisk:
            disk.main(args + ["--restore-sha256", self.digest(restore)])
        command = sgdisk.call_args.args[0]
        start = disk.ROOT_START + 1
        self.assertIn(f"--new=3:{start}:{start}", command)
        with self.output.open("rb") as image:
            image.seek((disk.ROOT_START + 1) * disk.SECTOR)
            self.assertEqual(image.read(disk.SECTOR), payload)


if __name__ == "__main__":
    unittest.main()
