from contextlib import redirect_stderr
import importlib.util
import io
from pathlib import Path
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "recovery_boot_config.py"
SPEC = importlib.util.spec_from_file_location("recovery_boot_config", SCRIPT)
config = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(config)

ROOT = "10000000-0000-0000-0000-000000000001"
SCRATCH = "10000000-0000-0000-0000-000000000002"
EXPORT = "10000000-0000-0000-0000-000000000003"
RESTORE = "10000000-0000-0000-0000-000000000004"
KERNEL = "6.12.117+deb13-amd64"


class RecoveryBootConfigTests(unittest.TestCase):
    def test_binds_only_explicit_filesystem_ids_and_keeps_root_read_only(self):
        fstab, grub = config.boot_config(ROOT, SCRATCH, EXPORT, KERNEL)
        self.assertEqual(fstab.splitlines()[:3], [
            f"UUID={ROOT} / ext4 ro 0 0",
            f"UUID={SCRATCH} /srv/vaultsync-recovery ext4 rw,nosuid,nodev,noexec 0 0",
            f"UUID={EXPORT} /srv/vaultsync-recovery-exports ext4 rw,nosuid,nodev,noexec 0 0",
        ])
        self.assertIn(f"root=UUID={ROOT} ro ", grub)
        self.assertIn(f"initrd /boot/initrd.img-{KERNEL}", grub)

    def test_rejects_collisions_invalid_uuids_and_boot_token_injection(self):
        for root, scratch, export, kernel in (
            (ROOT, ROOT, EXPORT, KERNEL), (ROOT, SCRATCH, SCRATCH, KERNEL),
            ("00000000-0000-0000-0000-000000000000", SCRATCH, EXPORT, KERNEL),
            ("LABEL=/dev/sda", SCRATCH, EXPORT, KERNEL),
            (ROOT, SCRATCH, EXPORT, "6.12 init=/bin/sh"),
            (ROOT, SCRATCH, EXPORT, "6.12/../../boot"),
        ):
            with self.subTest(root=root, scratch=scratch, export=export, kernel=kernel):
                with self.assertRaises(ValueError):
                    config.boot_config(root, scratch, export, kernel)

    def test_combined_menu_requires_a_choice_and_binds_each_immutable_root(self):
        fstab, grub = config.boot_config(ROOT, SCRATCH, EXPORT, KERNEL, RESTORE)
        self.assertIn("set timeout=-1", grub)
        capture, restore = grub.split("menuentry")[1:]
        self.assertIn("Create disk image", capture)
        self.assertIn(f"root=UUID={ROOT} ro", capture)
        self.assertIn("Restore disk image", restore)
        self.assertIn(f"root=UUID={RESTORE} ro", restore)
        self.assertNotIn(RESTORE, fstab)
        with self.assertRaises(ValueError):
            config.boot_config(ROOT, SCRATCH, EXPORT, KERNEL, EXPORT)

    def test_cli_never_replaces_existing_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "boot"
            args = ["--root-uuid", ROOT, "--scratch-uuid", SCRATCH,
                    "--export-uuid", EXPORT, "--kernel-release", KERNEL, "--output", str(output)]
            config.main(args)
            before = {path.name: path.read_bytes() for path in output.iterdir()}
            self.assertEqual(set(before), {"fstab", "grub.cfg"})
            with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as result:
                config.main(args)
            self.assertEqual(result.exception.code, 1)
            self.assertEqual(before, {path.name: path.read_bytes() for path in output.iterdir()})


if __name__ == "__main__":
    unittest.main()
