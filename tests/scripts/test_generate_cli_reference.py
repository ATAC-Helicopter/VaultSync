import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "generate_cli_reference.py"
spec = importlib.util.spec_from_file_location("generate_cli_reference", MODULE_PATH)
generate_cli_reference = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
spec.loader.exec_module(generate_cli_reference)


class GenerateCliReferenceTests(unittest.TestCase):
    def test_inventory_contains_every_grouped_and_compatibility_route(self) -> None:
        commands = {" ".join(command) for command in generate_cli_reference.COMMANDS}
        expected = {
            "projects add", "projects list", "projects show", "projects discover",
            "projects set-path", "projects remove", "snapshots create", "snapshots list",
            "snapshots show", "snapshots diff", "snapshots prune", "recovery restore",
            "mirror", "verify", "watch", "doctor", "destinations", "self-test", "init",
            "config show", "config path", "config set-db", "presets list", "presets show",
            "version", "docs", "add-project", "remove-project", "list-projects",
            "set-path", "update-path", "snapshot", "sync", "restore", "history", "diff", "prune",
        }
        self.assertTrue(expected.issubset(commands))

    def test_render_is_deterministic_and_normalizes_executable_and_ansi(self) -> None:
        def fake_help(command):
            return "\x1b[31mUSAGE:\x1b[0m\n    VaultSync.CLI.dll " + " ".join(command) + "\n"

        first = generate_cli_reference.render_document(fake_help)
        second = generate_cli_reference.render_document(fake_help)
        self.assertEqual(first, second)
        self.assertNotIn("\x1b", first)
        self.assertNotIn("VaultSync.CLI.dll", first)
        self.assertIn("vaultsync snapshots show", first)
        self.assertIn("Do not edit manually", first)

    def test_generate_runs_help_for_every_registered_route(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            assembly = Path(directory) / "VaultSync.CLI.dll"
            assembly.touch()
            completed = SimpleNamespace(stdout="USAGE:\n    VaultSync.CLI.dll\n", stderr="")
            with mock.patch.object(generate_cli_reference.subprocess, "run", return_value=completed) as run:
                with mock.patch.object(generate_cli_reference, "verify_inventory"):
                    rendered = generate_cli_reference.generate(assembly)

        self.assertEqual(len(generate_cli_reference.COMMANDS), run.call_count)
        first_arguments = run.call_args_list[0].args[0]
        last_arguments = run.call_args_list[-1].args[0]
        self.assertEqual(["dotnet", str(assembly), "--help"], first_arguments)
        self.assertEqual(["dotnet", str(assembly), "prune", "--help"], last_arguments)
        self.assertEqual("120", run.call_args_list[0].kwargs["env"]["COLUMNS"])
        self.assertIn("## `vaultsync recovery restore`", rendered)

    def test_main_writes_and_checks_the_generated_reference(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            assembly = root / "VaultSync.CLI.dll"
            output = root / "CLI_COMMAND_REFERENCE.md"
            assembly.touch()
            arguments = [
                str(MODULE_PATH), "--assembly", str(assembly), "--output", str(output)
            ]
            with mock.patch.object(generate_cli_reference, "generate", return_value="generated\n"):
                with mock.patch.object(sys, "argv", arguments):
                    generate_cli_reference.main()
                self.assertEqual("generated\n", output.read_text(encoding="utf-8"))

                with mock.patch.object(sys, "argv", [*arguments, "--check"]):
                    generate_cli_reference.main()

                output.write_text("stale\n", encoding="utf-8")
                with mock.patch.object(sys, "argv", [*arguments, "--check"]):
                    with self.assertRaisesRegex(SystemExit, "reference is stale"):
                        generate_cli_reference.main()

    def test_generate_rejects_a_missing_assembly(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            missing = Path(directory) / "missing.dll"
            with self.assertRaisesRegex(FileNotFoundError, "CLI assembly not found"):
                generate_cli_reference.generate(missing)

    def test_inventory_verification_rejects_missing_registered_route(self) -> None:
        def help_for(command):
            if not command:
                return "COMMANDS:\n    projects  Manage projects\n    unexpected  New route\n"
            return "USAGE:\n    vaultsync " + " ".join(command)

        with self.assertRaisesRegex(ValueError, "CLI route inventory drift"):
            generate_cli_reference.verify_inventory(help_for)

    def test_command_children_reads_only_direct_subcommands(self) -> None:
        help_text = """USAGE:
    vaultsync projects [OPTIONS] <COMMAND>
OPTIONS:
    -h, --help  Prints help
COMMANDS:
    add <name> <path>    Register a source
                              continued description
    list                 List sources
"""
        self.assertEqual(("add", "list"), generate_cli_reference.command_children(help_text))


if __name__ == "__main__":
    unittest.main()
