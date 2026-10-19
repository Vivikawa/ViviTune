---
description: session handoff, regenerate with /handoff when a quest finishes
budget_tokens: 1000
---
# STATUS — ViviTune

> Read this FIRST when resuming. Last updated: 2026-10-19

## ✅ Done

- v1 (WinForms, net48) on `main`: works in practice (Discord voice + stream through router SOCKS5 `192.168.1.1:1818`, Podkop).
- **ViviTune 2, stage 1 (Windows)** on branch `avalonia-ui`, user-confirmed working 2026-10-19:
  - .NET 10 + Avalonia 12.1.4, sidebar UI à la Hiddify: Главная (power button, `TCP + UDP`/`только TCP`, proxied speed + sparklines, per-connection traffic total «всего …»), Приложения (switches, add from running programs or .exe), Лог, Настройки.
  - Core: `Settings` (v1 + v2 format, JSON via source-gen `JsonCtx`), `ConfigBuilder` (+ `experimental.clash_api`), `TrafficMeter` (Clash API `/connections`, proxy chains only), `Session` (state machine, debounced restarts), `Windows/Runner` (Job Object), `Windows/ProcessCatalog`.
  - Publish: single-file, self-contained, trimmed + compressed: `ViviTune.exe` 23.3 MB + `sing-box.exe` 1.12.12 (37 MB) alongside; app icon `src/ViviTune/Assets/icon.ico`.
  - Tests: 104 ok, also under a trimmed publish of `ViviTune.Tests`. Elevated lifecycle harness passed on a545696 (Stop and hard kill leave no sing-box, no `172.19.0.x` routes).
- Spec `docs/superpowers/specs/2026-10-18-vivitune-avalonia-ui-design.md`, plan `docs/superpowers/plans/2026-10-18-vivitune-avalonia-ui.md`.

## 🚀 Next quest

**Goal:** stage 2 — Linux (Arch) build of ViviTune 2.
- Needs a brainstorm/spec first: Linux replacements for `Windows/` (Runner without Job Object, process list + icons, admin rights instead of the UAC manifest, TUN permissions), `Path.GetFileName` on Windows-style paths, packaging.
- Acceptance: same UI and Session on Arch; per-app proxy works for the user's apps.
- Open decisions for the user: how to get root (pkexec/sudo/capabilities), packaging (AUR/AppImage/tarball).

Parked minors (from reviews): «Перезапуск…» may flash ~1 s in a rare sequence; the app toggle switch has no click guard; picking an unrelated exe with the same file name replaces the row's path.

## Context

- Branch `avalonia-ui` awaits the merge decision into `main` (no git remote). v1 stays on `main` until then.
- Build/test: `dotnet run --project src/ViviTune.Tests`; screenshots `dotnet run --project src/ViviTune.Shots -- artifacts/shots`; publish `dotnet publish src/ViviTune -c Release -o artifacts/publish` (keeps `settings.json` there; v1 cannot read v2 settings).
- `sing-box.exe` 1.12.12 lives in git-ignored `src/ViviTune/third_party/`; staying on 1.12.12 on purpose (see cerebrum Decision Log). Every `JsonSerializer` call must go through `JsonCtx` (trimming).
- .NET SDK 10.0.402 installed; GUI runs need admin (UAC), so lifecycle checks use an elevated scratch harness.

## 📚 References

- `.wolf/cerebrum.md` — preferences + Do-Not-Repeat
- `.wolf/anatomy.md` — file index (query with `openwolf find`)
- `.wolf/buglog.json` — known bugs
