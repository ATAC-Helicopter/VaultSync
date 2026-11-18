from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "assemble_recovery_disk.py"


@unittest.skipUnless(sys.platform == "linux", "the disk assembler requires Linux")
class AssembleRecoveryDiskTests(unittest.TestCase):
    def test_bad_partition_digest_never_publishes_an_image(self):
        with tempfile.TemporaryDirectory() as directory:
            scratch = Path(directory)
            esp = scratch / "efi.fat"
            root = scratch / "root.ext4"
            output = scratch / "test.img"
            with esp.open("xb") as stream:
                stream.truncate(256 * 1024**2)
            root.write_bytes(b"\0" * 512)
            run = subprocess.run([
                sys.executable, str(SCRIPT), "--esp", str(esp), "--root", str(root),
                "--output", str(output), "--esp-sha256", "0" * 64, "--root-sha256", "0" * 64,
                "--disk-guid", "10000000-0000-0000-0000-000000000001",
                "--esp-guid", "10000000-0000-0000-0000-000000000002",
                "--root-guid", "10000000-0000-0000-0000-000000000003",
            ], capture_output=True, text=True)
            self.assertEqual(run.returncode, 1)
            self.assertIn("does not match its expected SHA-256", run.stderr)
            self.assertEqual(set(scratch.iterdir()), {esp, root})


if __name__ == "__main__":
    unittest.main()
