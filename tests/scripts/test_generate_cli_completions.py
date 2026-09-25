import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = REPO_ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS))
MODULE_PATH = SCRIPTS / "generate_cli_completions.py"
spec = importlib.util.spec_from_file_location("generate_cli_completions", MODULE_PATH)
generate = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
spec.loader.exec_module(generate)


class GenerateCliCompletionsTests(unittest.TestCase):
    def test_task_topics_follow_the_bundled_guides(self) -> None:
        self.assertEqual("setup inspect mirror restore automate migrate", generate.task_topics())

    def test_options_include_long_and_short_names(self) -> None:
        help_text = """OPTIONS:
    -h, --help               Prints help information
        --db <PATH>
        --output <FORMAT>
COMMANDS:
    list    List projects
"""
        self.assertEqual(
            ("-h", "--help", "--db", "--output"),
            generate.command_options(help_text),
        )

    def test_all_shells_include_routes_and_options(self) -> None:
        children = {"": "projects docs", "projects": "list", "completion": "bash zsh powershell"}
        options = {"": "-h --help", "projects list": "--db --output"}
        values = {"projects list --output": "text json", "docs --task": "setup inspect"}
        scripts = (
            generate.render_bash(children, options, values),
            generate.render_zsh(children, options, values),
            generate.render_powershell(children, options, values),
        )
        for script in scripts:
            self.assertIn("projects docs", script)
            self.assertIn("--db --output", script)
            self.assertIn("bash zsh powershell", script)
            self.assertIn("setup inspect", script)
            self.assertIn("text json", script)

    def test_bash_completes_task_and_output_values(self) -> None:
        children = {"": "projects docs", "projects": "list"}
        options = {"projects list": "--output", "docs": "--task"}
        values = {"projects list --output": "text json", "docs --task": "setup inspect"}
        with tempfile.TemporaryDirectory() as directory:
            script = Path(directory) / "vaultsync.bash"
            script.write_text(generate.render_bash(children, options, values), encoding="utf-8")
            probe = '''source "$1"
COMP_WORDS=(vaultsync docs --task in)
COMP_CWORD=3
_vaultsync_complete
printf '%s\\n' "${COMPREPLY[@]}"
COMP_WORDS=(vaultsync projects list --output j)
COMP_CWORD=4
_vaultsync_complete
printf '%s\\n' "${COMPREPLY[@]}"'''
            result = subprocess.run(["bash", "-c", probe, "bash", str(script)],
                                    check=True, capture_output=True, text=True)
        self.assertEqual(["inspect", "json"], result.stdout.splitlines())


if __name__ == "__main__":
    unittest.main()
