---
description: session handoff, regenerate with /handoff when a quest finishes
budget_tokens: 1000
---
# STATUS — ViviTune

> Read this FIRST when resuming. Last updated: 2026-10-18

## ✅ Done

- v1 built and **works in practice** (user-confirmed 2026-10-18): Discord (voice + stream, i.e. UDP) and a browser go through the router SOCKS5 `192.168.1.1:1818` (Podkop), other apps stay direct.
- Design: `docs/superpowers/specs/2026-10-04-vivitune-design.md`; plan: `docs/superpowers/plans/2026-10-04-vivitune.md`.
- Code in `src/ViviTune` (WinForms, net48, C# 7.3): `Settings.cs` (settings.json, validation, process names), `ConfigBuilder.cs` (sing-box 1.12.12 config: TUN v4+v6, `process_name` -> `proxy`, final `direct`, proxy-dns over **tcp**, log level `warn`), `ProxyProbe.cs` (SOCKS5 + UDP ASSOCIATE check, never throws, IPv6 ok), `Runner.cs` (sing-box in a Job Object, kill-on-close), `MainForm.cs`/`Program.cs` (UI, single-instance mutex, admin manifest).
- Tests: `src/ViviTune.Tests` (console `Check`, 22 ok) -> `dotnet run --project src/ViviTune.Tests`.
- Git initialised on 2026-10-04, branch `main`, 8 commits (d11201f..e084243). Reviewed per task + whole branch; two review fix rounds done.

## 🚀 Next quest

**Goal:** nothing required; v1 is complete and verified. Optional polish only.

Verified 2026-10-18 (elevated lifecycle harness on Runner/ConfigBuilder, no GUI): after `Stop()` and after a hard-killed parent, `sing-box.exe` is gone, TUN adapter gone, no `172.19.0.x` routes, internet works. Polish commit 688dcc9 fixed: log scroll jump, old `Process` disposal, `_stopping` on failed kill, ponytail trivia.

Remaining minors (static review; no leaks found, GUI not profiled):
- Win32 return values in `Runner` unchecked; `Exited` can race form close (`UI()` check-then-`BeginInvoke`).
- Fixed 96-DPI layout (no AutoScale).
- GUI itself (button flow, mutex, log trimming) only verified by the user's manual use, no automated UI test.

Open decisions for the user: none. Out of scope by design: tray, autostart, profiles, proxy login/password, domain rules, sing-box auto-update.

## Context

- Build: `dotnet build src/ViviTune -c Release` -> `src/ViviTune/bin/Release/net48/ViviTune.exe` (needs admin/UAC). `sing-box.exe` 1.12.12 is in git-ignored `src/ViviTune/third_party/` (re-download from the SagerNet v1.12.12 release; copied next to the exe on build).
- .NET SDK 8.0.425 was installed via winget for this; config check: `sing-box.exe check -c <config.json>`.
- Matching is by exe file name; already-open connections stay direct until the app is restarted. Rulings made on the user's behalf are in the final-review notes: warn log + 500-line cap, DNS over tcp (spec edited).
- Untracked tooling dirs (`.claude`, `.codex`, `.cursor`, `.opencode`, `.wolf`, `AGENTS.md`) are OpenWolf scaffolding, not part of the app.

## 📚 References

- `.wolf/cerebrum.md` — preferences + Do-Not-Repeat
- `.wolf/anatomy.md` — file index (query with `openwolf find`)
- `.wolf/buglog.json` — known bugs
