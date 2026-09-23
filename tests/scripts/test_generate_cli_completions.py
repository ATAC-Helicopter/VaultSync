import importlib.util
import sys
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
        scripts = (
            generate.render_bash(children, options),
            generate.render_zsh(children, options),
            generate.render_powershell(children, options),
        )
        for script in scripts:
            self.assertIn("projects docs", script)
            self.assertIn("--db --output", script)
            self.assertIn("bash zsh powershell", script)


if __name__ == "__main__":
    unittest.main()
