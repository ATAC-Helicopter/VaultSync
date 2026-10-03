# Active development: VaultSync 1.9.0

The active release is 1.9.0, unless the maintainer explicitly selects another
release. Read ROADMAP.md, release/1.9-family.json, CONTRIBUTING.md and
docs/RELEASE_1.9.0.md before implementation.

## Working copy and delivery

- The assembled 1.9.0 development branch is `work/1.9-roadmap-realignment`.
  Its sole release PR is #736, targeting `Dev`. Keep this workspace on that
  branch for work that must ship with 1.9.0, and add its commits to that PR.
- `release/1.9.0` is the protected release identity; it is not the current
  assembled preparation head. Do not switch to its older tree to start work,
  rewrite it, or create another 1.9.0 release PR merely because of its name.
- Before editing, inspect Git status and the PR head. Preserve local/untracked
  work and existing commits. Follow merge-only protection and required reviews.
- Use the family manifest to check ownership. Discovery/help/completion belong
  to 1.9.1; resource CLI automation belongs to 1.9.5. Work for later releases
  goes to their own preparation branches/PRs, not into the 1.9.0 binary.
- Reuse the owning issue and update the 1.9.0 changelog, relevant contracts,
  documentation and native GitHub Project tracking with each meaningful change.
- Keep #736 draft until its actual release gates pass. Do not merge or promote
  a release merely because compilation or mandatory CI checks pass.

When the maintainer changes the active release, update this working context and
the release contract together so the next session starts in the correct place.
