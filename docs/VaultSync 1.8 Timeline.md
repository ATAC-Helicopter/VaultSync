# VaultSync 1.8 Roadmap
## Project History & Recovery

Status: 1.8.0 Released
Version: 1.8.x Series
Release Date: June 20, 2026
Last Updated: June 20, 2026

---

# Executive Summary

VaultSync 1.8 is intended to be the largest release in the project's history.

The goal of this release is not to become a source control system, an enterprise backup suite, or a cloud platform.

The goal is to evolve VaultSync from a backup utility into a platform that helps users:

- Protect projects
- Understand project history
- Preserve important milestones
- Measure recoverability
- Improve disaster recovery readiness

Version 1.8 introduces two major concepts to VaultSync:

## History

Understanding how a project evolved over time.

## Recovery

Understanding whether a project can be recovered if disaster occurs.

---

# Product Positioning

VaultSync is not intended to replace Git.

Git answers:

- What changed in code?
- Who changed it?
- How do teams collaborate?

VaultSync answers:

- What changed in the entire project?
- Which version should I restore?
- Which version should I protect?
- Can I recover today?
- Is my disaster recovery strategy adequate?

VaultSync protects:

- Source code
- Assets
- Builds
- Databases
- Documents
- Photos
- Audio
- Video
- Project configurations
- Entire working directories

---

# Core Product Pillars

VaultSync 1.8 is built around three user goals.

## Protect

Create, verify, and maintain backups.

Primary area:

text Backups

---

## Understand

Explore historical project states and changes.

Primary area:

text History

---

## Recover

Validate recoverability and disaster readiness.

Primary area:

text Recovery

---

# Navigation Structure

Current:

text Dashboard Projects Backups Settings

VaultSync 1.8:

text Dashboard Projects Backups History Recovery Settings

---

# Page Responsibilities

## Dashboard

Purpose:

> What needs my attention?

Dashboard becomes the global health and awareness center.

### Widgets

- Recovery Readiness Overview
- Projects Requiring Attention
- Verification Warnings
- Recovery Coverage Summary
- Recent Milestones
- Recent Activity
- Quick Actions

---

## Projects

Purpose:

> What am I protecting?

Projects remains the management center.

### Responsibilities

- Project Management
- Destinations
- Backup Policies
- Retention Policies
- Verification Policies
- Disaster Recovery Policies
- Project Groups

### Project Card Enhancements

Potential additions:

- Recovery Readiness
- Protected Versions
- Known Good Version
- Last Verification

---

## Backups

Purpose:

> Protect and restore data.

Backups remains the operational center.

### Responsibilities

- Create Backup
- Restore
- Verification
- Cleanup
- Retention
- Snapshot Management
- Restore Preview

### Important

Historical analysis features should not live here.

Backups remains focused on operational workflows.

---

## History

Purpose:

> What happened over time?

History becomes the project exploration center.

### Responsibilities

- Timeline
- Versions
- Milestones
- Snapshot Explorer
- Search
- Compare
- Change Intelligence

---

## Recovery

Purpose:

> Can I recover if disaster occurs?

Recovery becomes the recoverability and resilience center.

### Responsibilities

- Recovery Readiness
- Recovery Coverage
- Recovery Drills
- Recovery Reports
- Protected Recovery Points
- 3-2-1 Advisor
- Recovery Recommendations

---

## Settings

Purpose:

> Configure VaultSync.

### Responsibilities

- Application Settings
- Themes
- Diagnostics
- Global Defaults
- Update Settings

---

# Phase 0 — Foundations

## Goal

Build the metadata foundation required for all 1.8 functionality.

---

## Database & Metadata

### Snapshot Tags

Examples:

- v1.0
- v1.8
- Release Candidate

---

### Snapshot Notes

Examples:

- Production deployment
- Final review
- Customer delivery

---

### Protected Snapshots

Snapshots ignored by retention cleanup.

---

### Known Good Versions

Trusted recovery points.

---

### Milestones

Examples:

- First Release
- Production Launch
- Final Submission

---

### Recovery Models

- Recovery Readiness
- Recovery Coverage
- Recovery Reports
- Recovery Drill Results

---

### Project Groups

Support grouping related projects.

---

## Internal Services

### Timeline Service

### Version Service

### Recovery Service

### Milestone Service

### Group Service

---

## Deliverable

No major user-facing features.

Foundation completed.

---

# VaultSync 1.8.0 — Project History

## Release Theme

> Understand how your projects evolve.

---

## New Navigation

text Dashboard Projects Backups History Recovery Settings

---

## Dashboard Refresh

### Recovery Readiness Widget

### Projects Requiring Attention

### Recent Milestones

### Recent Activity

---

## History Page v1

### Timeline

Displays:

- Snapshots
- Version Tags
- Milestones

Timeline v1 intentionally remains simple.

No advanced visualizations.

---

### Version Tags

Examples:

- v1.0
- v1.8
- Beta
- RC
- Client Delivery

---

### Snapshot Notes

Attach contextual information to snapshots.

---

### Protected Snapshots

Retention ignores protected snapshots.

Current implementation:

- Snapshot metadata can mark a snapshot as protected.
- Backup retention and retention simulation honor protected snapshot metadata.
- Orphan snapshot retention skips snapshots protected by metadata.

---

### Known Good Versions

Users can identify trusted recovery points.

---

## Success Criteria

Users can:

- Tag important versions
- Preserve important snapshots
- Add context to history
- View project evolution

---

# VaultSync 1.8.1 — Recovery Intelligence

## Release Theme

> Know whether you can recover before disaster strikes.

---

## Recovery Page v1

### Recovery Readiness

First scoring model.

Potential inputs:

- Backup Recency
- Verification Status
- Destination Health
- Recovery Coverage
- Protected Versions

Current implementation:

- Recovery readiness uses backup recency, verification policy, destination reachability, and index health.
- When snapshot metadata is available, missing protected and known-good recovery points lower readiness and explain the gap.

---

### Recovery Coverage

Visibility into recovery gaps.

Example:

text 24 Hours   ✓ 7 Days     ✓ 30 Days    ✓ 90 Days    ⚠

---

### Recovery Recommendations

Examples:

- Verification overdue
- Missing secondary destination
- No protected versions

---

### Recovery Reports

Basic export functionality.

---

## Success Criteria

Users can measure recoverability.

---

# VaultSync 1.8.2 — Snapshot Explorer

## Release Theme

> Browse backups without restoring them.

---

## Snapshot Explorer

### Folder Navigation

### File Listing

### Search

### Metadata

---

## Preview Support

### TXT

### Markdown

### JSON

### XML

### YAML

### LOG

---

## Restore Actions

Restore:

- File
- Folder

Directly from explorer.

---

## Success Criteria

Users can locate files without restoring entire snapshots.

---

# VaultSync 1.8.3 — Compare & Change Intelligence

## Release Theme

> Understand exactly what changed.

---

## Snapshot Compare

Compare any two snapshots.

Display:

- Added
- Modified
- Deleted

---

## Change Explorer

Visual overview of changes.

Examples:

- Storage growth
- Folder changes
- Significant deletions

---

## Text Diff Viewer

Supported:

- TXT
- MD
- JSON
- XML
- YAML
- Code files

---

## Large Change Detection

Highlight:

- Mass deletions
- Significant growth
- Unusual activity

---

## Success Criteria

Users can understand project changes over time.

---

# VaultSync 1.8.4 — Disaster Recovery

## Release Theme

> Protect the versions that matter.

---

## Recovery Drill

Simulate recovery without restoring.

---

## 3-2-1 Advisor

Evaluate:

- Copy count
- Media diversity
- Offsite protection

---

## Protected Recovery Points

Examples:

- Production Release
- Customer Delivery
- Final Submission

---

## Trigger-Based Protection

Potential triggers:

- Version tag created
- Large deletion detected
- Significant project changes
- Before cleanup operations

---

## Success Criteria

Users can measure disaster recovery readiness.

---

# VaultSync 1.8.5 — Project Groups

## Release Theme

> Manage related projects together.

---

## Project Groups

Examples:

text VaultSync ├─ UI ├─ CLI ├─ Documentation └─ Website

text Game Project ├─ Client ├─ Assets ├─ Audio └─ Documentation

---

## Group Health

Combined:

- Recovery Readiness
- Storage
- Activity

---

## Group Reporting

Combined visibility across projects.

---

## Success Criteria

Users can manage project ecosystems efficiently.

---

# Data Model Additions

## Snapshot Extensions

text IsProtected IsKnownGood IsFavorite VersionTag

---

## Snapshot Tag

text Id SnapshotExternalId Name CreatedUtc

---

## Snapshot Note

text Id SnapshotExternalId Content CreatedUtc

---

## Milestone

text Id ProjectId Name Description SnapshotExternalId CreatedUtc

---

## Recovery Drill

text Id ProjectId Result CreatedUtc

---

## Recovery Report

text Id ProjectId GeneratedUtc

---

## Project Group

text Id Name Description

---

# MVP Definition

VaultSync 1.8.0 is considered successful when the following exist:

### Navigation

- History
- Recovery

### Dashboard

- Recovery Readiness
- Project Attention Widgets

### History

- Timeline
- Version Tags
- Snapshot Notes
- Protected Snapshots
- Known Good Versions

### Recovery

- Recovery Readiness
- Recovery Coverage

Everything else can ship later in the 1.8.x cycle.

---

# Explicitly Out of Scope

The following are intentionally excluded:

- Git replacement
- Branching
- Merge systems
- Pull Requests
- Collaboration Platforms
- User Permissions
- Enterprise Administration
- Compliance Systems
- High Availability
- Failover Orchestration
- Cloud Synchronization Platforms
- Backup Engine Rewrites

---

# Long-Term 1.9 Candidates

Potential future features:

- Milestone Replication
- Import Lineage
- Snapshot Integrity Chains
- Image Diffing
- Content Indexing
- Advanced Search
- Historical Analytics
- Additional Storage Integrations

---

# Success Criteria

A successful VaultSync 1.8 release allows users to:

## Protect

Create and maintain reliable backups.

## Understand

Browse project history and understand project evolution.

## Version

Preserve important project states.

## Recover

Measure recoverability before disaster occurs.

## Improve Resilience

Identify weaknesses in their protection strategy.

---

# Release Identity

VaultSync 1.8 should be remembered as:

> The release that introduced Project History and Recovery Intelligence.

Not a backup utility.

Not a source control replacement.

A platform that helps users understand, protect, version, and recover their projects with confidence.
