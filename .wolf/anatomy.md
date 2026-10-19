# anatomy.md

> Auto-maintained by OpenWolf. Last scanned: 2026-10-18T18:12:19.504Z
> Files: 51 tracked | Anatomy hits: 0 | Misses: 0

> Project structure index. Auto-maintained by OpenWolf hooks and daemon.
> Run `openwolf scan` to generate, or wait for the first Claude Code session.
> Status: Pending initial scan

## ./

- `AGENTS.md` — OpenWolf (~75 tok)
- `CLAUDE.md` — OpenWolf (~99 tok)

## .superpowers/brainstorm/1397-1792328603/content/

- `apps.html` (~1492 tok)
- `layout.html` (~2015 tok)
- `playback-1.html` (~1756 tok)
- `waiting.html` (~43 tok)

## .superpowers/sdd/2026-10-04-vivitune/

- `task-1-report.md` — Task 1 Report: Каркас, тестовый проект и Settings (~1208 tok)
- `task-2-report.md` — Task 2: ConfigBuilder - Implementation Report (~998 tok)
- `task-3-report.md` — Task 3: ProxyProbe - Implementation Report (~1224 tok)
- `task-4-report.md` — Task 4: Runner — Build Report (~1054 tok)

## .superpowers/sdd/2026-10-18-vivitune-avalonia-ui/

- `final-fix-report.md` — Final whole-branch review: fix report (~2762 tok)
- `task-1-report.md` — Task 1 report (~688 tok)
- `task-5-report.md` — Task 5 report: Session (connection state, restart, errors) (~2409 tok)
- `task-7-report.md` — Task 7 report: window shell (theme, ViewState, sidebar, Log, Settings, Session wiring, Shots) (~2339 tok)
- `task-8-report.md` — Task 8 report: Главная — кнопка, статус, скорость с графиками (~2325 tok)
- `task-9-report.md` — Task 9 report: Приложения — список, переключатели, добавление из запущенных (~3101 tok)

## docs/superpowers/plans/

- `2026-10-04-vivitune.md` — ViviTune Implementation Plan (~8590 tok)
- `2026-10-18-vivitune-avalonia-ui.md` — ViviTune 2 (Avalonia UI, этап 1: Windows) Implementation Plan (~6516 tok)

## docs/superpowers/specs/

- `2026-10-04-vivitune-design.md` — ViviTune — дизайн (~1082 tok)
- `2026-10-18-vivitune-avalonia-ui-design.md` — ViviTune 2 — новый интерфейс на Avalonia (этап 1: Windows) (~2885 tok)

## src/ViviTune.Shots/

- `Program.cs` — Class: Program (~1770 tok)
- `ViviTune.Shots.csproj` (~135 tok)

## src/ViviTune.Tests/

- `Program.cs` — Class: Program (~10579 tok)
- `ViviTune.Tests.csproj` (~132 tok)

## src/ViviTune/

- `ConfigBuilder.cs` — Class: ConfigBuilder (~628 tok)
- `MainForm.cs` — Class: MainForm (~1832 tok)
- `ProxyProbe.cs` — Class: ProbeResult (~536 tok)
- `Runner.cs` — Class: Runner (~1170 tok)
- `Session.cs` — Class: Session (~3205 tok)
- `Settings.cs` — Class: AppEntry (~1095 tok)
- `TrafficMeter.cs` — Class: TrafficMath (~1138 tok)
- `ViviTune.csproj` (~488 tok)

## src/ViviTune/Ui/

- `App.axaml` (~141 tok)
- `App.axaml.cs` — Class: App (~1702 tok)
- `AppListOps.cs` — Список приложений из Settings: пути сравниваются без учёта регистра. Без Avalonia (тестируется отдел (~490 tok)
- `AppsPage.axaml` (~2288 tok)
- `AppsPage.axaml.cs` — Class: AppsPage (~1471 tok)
- `ClickGuard.cs` — Повторное нажатие в течение 500 мс после принятого не принимается: двойной клик не попадает (~125 tok)
- `HomePage.axaml` (~1732 tok)
- `HomePage.axaml.cs` — Class: HomePage (~158 tok)
- `LogPage.axaml` (~320 tok)
- `LogPage.axaml.cs` — Class: LogPage (~235 tok)
- `MainWindow.axaml` (~689 tok)
- `MainWindow.axaml.cs` — Class: MainWindow (~197 tok)
- `SettingsPage.axaml` (~463 tok)
- `SettingsPage.axaml.cs` — Class: SettingsPage (~349 tok)
- `Sparkline.cs` — Class: Sparkline (~1174 tok)
- `Theme.axaml` (~2143 tok)
- `ViewState.cs` — Class: ViewState (~2434 tok)

## src/ViviTune/Windows/

- `ProcessCatalog.cs` — Class: ProcessCatalog (~747 tok)
- `Runner.cs` — Class: Runner (~1233 tok)
