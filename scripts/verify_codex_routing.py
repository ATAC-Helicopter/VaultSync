#!/usr/bin/env python3
"""Static validation of VaultSync's project-scoped Codex model routing.

This protects checked-in defaults; it cannot enforce limits against Codex CLI/UI
overrides, user-level configuration, or runtime selection outside these roles.
"""
from __future__ import annotations

from pathlib import Path
import sys
import tomllib

ROOT = Path(__file__).resolve().parents[1]
EXPECTED = {
    "vaultscan": ("gpt-6-luna", "low"),
    "vaultui": ("gpt-6.1-terra", "medium"),
    "vaultworker": ("gpt-6.1-terra", "high"),
    "vaultcritical": ("gpt-6.1-sol", "medium"),
    "vaultverify": ("gpt-6.1-terra", "medium"),
}
SAFE_EFFORTS = {
    "gpt-6-luna": {"none", "low", "medium", "high"},
    "gpt-6.1-terra": {"none", "low", "medium", "high"},
    "gpt-6.1-sol": {"low", "medium"},
}


def load(path: Path) -> dict:
    with path.open("rb") as stream:
        return tomllib.load(stream)


def verify() -> None:
    project = load(ROOT / ".codex/config.toml")
    assert (project["model"], project["model_reasoning_effort"]) == (
        "gpt-6.1-terra",
        "medium",
    ), "Root should be economical Terra Medium"

    agents = project["agents"]
    assert agents["enabled"] is True
    assert agents["max_concurrent_threads_per_session"] == 2
    assert (agents["default_subagent_model"], agents["default_subagent_reasoning_effort"]) == (
        "gpt-6.1-terra",
        "medium",
    )
    actual_names = {path.stem for path in (ROOT / ".codex/agents").glob("*.toml")}
    assert actual_names == EXPECTED.keys(), f"Unexpected/missing custom roles: {actual_names ^ EXPECTED.keys()}"

    for name, settings in EXPECTED.items():
        role = load(ROOT / ".codex/agents" / (name + ".toml"))
        model, effort = role["model"], role["model_reasoning_effort"]
        assert (model, effort) == settings, f"{name}: unexpected model/effort {model}/{effort}"
        assert effort in SAFE_EFFORTS[model], f"{name}: unsupported or prohibited tier"
        assert role["name"] == name and role["description"] and role["developer_instructions"]
        assert "spawn" in role["developer_instructions"].lower(), f"{name}: must forbid recursion"

    policy = (ROOT / "AGENTS.md").read_text(encoding="utf-8")
    assert "GPT-6.1 Sol / medium" in policy
    assert "zero subagents" in policy
    assert "vaultcritical" in policy
    print("PASS: VaultSync Codex roles, root defaults, concurrency and soft ceiling")


if __name__ == "__main__":
    try:
        verify()
    except (AssertionError, KeyError, ValueError, OSError, tomllib.TOMLDecodeError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        sys.exit(1)
