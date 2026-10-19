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
- [2026-10-19] sing-box 1.12.12 was pinned in the v1 spec from memory, without checking releases (a year old by then; latest stable was 1.14.3). Before pinning any external binary/package version, check its current releases (GitHub API) and note why that version was chosen.
- [2026-10-19] v2 publish is trimmed (PublishTrimmed): reflection JSON silently fails at runtime there (Settings.Load falls back to defaults). Every JsonSerializer call must use `JsonCtx` (Settings.cs); new serialized types go into its `[JsonSerializable]` list. Verify with a trimmed publish of ViviTune.Tests and ViviTune.Shots, not only `dotnet run`.

## Decision Log

- [2026-10-04] sing-box wrapper (TUN + process_name) chosen over DLL injection (anticheat) and own WinDivert engine (weeks of work).
- [2026-10-19] Stay on sing-box 1.12.12 on purpose: 1.12.13-1.12.24 are only "fixes and improvements" with nothing for TUN/process_name/socks; 1.14.3 passes `sing-box check` on our config unchanged but is 82 MB (vs 37) and changes Windows TUN DNS defaults. Revisit only if a concrete bug shows up (try 1.12.24 first).
- [2026-10-19] v2 exe: trimmed + compressed single file (105 -> 23 MB) over framework-dependent (needs runtime install) and UPX on sing-box.exe (antivirus false positives).
- [2026-10-18] proxy-dns over tcp (works without UDP on the proxy); sing-box log level warn + 500-line log cap.

<!-- Significant technical decisions with rationale. Why X was chosen over Y. -->
