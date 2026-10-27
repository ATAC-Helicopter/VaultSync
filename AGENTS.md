# Agent instructions

## Risk-based, targeted verification (mandatory)
- Do **not** automatically run the full test suite, a solution-wide build, repository-wide lint/typecheck, or repeated test cycles for every edit. Minimize unnecessary tool calls, time, and token use.
- Before testing, assess the changed files, affected behavior, and realistic regression risk. For docs, text/copy, comments, formatting, and other clearly non-behavioral changes, **skip automated tests** unless a specific gate applies; inspect the diff instead.
- For behavior-changing code, use **only the smallest relevant existing test(s)**, focused test filter, package, assembly, or project. Run at a meaningful checkpoint, not after each trivial edit. Avoid redundant dependency installs/restores.
- If no relevant automated test exists, use a focused static/manual check when feasible; say what remains unverified. Do not run unrelated tests merely to claim coverage.
- Expand to integration tests, broader builds, or the full suite **only when justified** by shared/core changes, significant refactors, security/privacy changes, migrations, release gates, failure of focused checks, or explicit user instruction. Explain the reason and avoid unnecessary repeats.
- Respect genuinely mandatory CI/release/security gates. Interpret generic existing instructions such as "run tests before commit" as **scoped, risk-based validation**, not permission to test the entire repository on every small change.
- In the final report, state what was tested, what was deliberately skipped, and any remaining risk. Never claim skipped tests passed.
