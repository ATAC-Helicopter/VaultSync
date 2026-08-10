import importlib.util
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "build_ico.py"

spec = importlib.util.spec_from_file_location("build_ico", MODULE_PATH)
build_ico = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
spec.loader.exec_module(build_ico)


class BuildIcoTests(unittest.TestCase):
    def test_main_requires_output_and_multiple_frames(self) -> None:
        with patch.object(sys, "argv", ["build_ico.py"]):
            self.assertEqual(2, build_ico.main())

    def test_main_builds_an_ico_with_ordered_frame_entries(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as output_tmp:
            with tempfile.TemporaryDirectory() as frame_tmp:
                output = Path(output_tmp) / "vaultsync.ico"
                first = Path(frame_tmp) / "16.png"
                second = Path(frame_tmp) / "256.png"
                first.write_bytes(b"small-png")
                second.write_bytes(b"large-png")

                with patch.object(
                    sys,
                    "argv",
                    ["build_ico.py", str(output), f"16:{first}", f"256:{second}"],
                ):
                    self.assertEqual(0, build_ico.main())

                payload = output.read_bytes()
                self.assertEqual((0, 1, 2), struct.unpack("<HHH", payload[:6]))
                first_entry = struct.unpack("<BBBBHHII", payload[6:22])
                second_entry = struct.unpack("<BBBBHHII", payload[22:38])
                self.assertEqual((16, 16), first_entry[:2])
                self.assertEqual((0, 0), second_entry[:2])
                self.assertEqual(38, first_entry[-1])
                self.assertEqual(38 + len(b"small-png"), second_entry[-1])
                self.assertTrue(payload.endswith(b"small-pnglarge-png"))

    def test_main_rejects_invalid_frame_size(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as output_tmp:
            output = Path(output_tmp) / "vaultsync.ico"
            with patch.object(
                sys,
                "argv",
                ["build_ico.py", str(output), "0:frame.png", "16:frame.png"],
            ):
                with self.assertRaisesRegex(ValueError, "invalid ICO frame size"):
                    build_ico.main()

    def test_main_rejects_non_ico_output(self) -> None:
        with tempfile.TemporaryDirectory(dir=REPO_ROOT) as output_tmp:
            output = Path(output_tmp) / "vaultsync.bin"
            with patch.object(
                sys,
                "argv",
                ["build_ico.py", str(output), "16:frame.png", "32:frame.png"],
            ):
                with self.assertRaisesRegex(ValueError, r"\.ico extension"):
                    build_ico.main()

    def test_resolve_restricted_path_rejects_escape(self) -> None:
        with self.assertRaisesRegex(ValueError, "allowed root"):
            build_ico.resolve_restricted_path(
                str(REPO_ROOT.parent / "outside.ico"),
                allowed_roots=(REPO_ROOT,),
                must_exist=False,
            )


if __name__ == "__main__":
    unittest.main()
