---
description: learned preferences, project conventions, and Do-Not-Repeat rules
budget_tokens: 2000
---
# Cerebrum

> OpenWolf's learning memory. Updated automatically as the AI learns from interactions.
> Do not edit manually unless correcting an error.
> Last updated: 2026-10-04

## User Preferences

<!-- How the user likes things done. Code style, tools, patterns, communication. -->

## Key Learnings

- **Project:** ViviTune (WinForms net48 wrapper over sing-box 1.12.12: per-app SOCKS5 via TUN; see STATUS.md)
- User communicates in Russian; answers must be in Russian. Prefers lazy/minimal solutions (ponytail mode) and approves designs step by step.
- Build/test: `dotnet build src/ViviTune -c Release`, `dotnet run --project src/ViviTune.Tests`. No GUI automation possible; lifecycle checks use an elevated scratch harness (needs one UAC click).

## Do-Not-Repeat

<!-- Mistakes made and corrected. Each entry prevents the same mistake recurring. -->
<!-- Format: [YYYY-MM-DD] Description of what went wrong and what to do instead. -->

## Decision Log

- [2026-10-04] sing-box wrapper (TUN + process_name) chosen over DLL injection (anticheat) and own WinDivert engine (weeks of work).
- [2026-10-18] proxy-dns over tcp (works without UDP on the proxy); sing-box log level warn + 500-line log cap.

<!-- Significant technical decisions with rationale. Why X was chosen over Y. -->
