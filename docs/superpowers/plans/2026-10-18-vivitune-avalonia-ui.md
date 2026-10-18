# ViviTune 2 (Avalonia UI, этап 1: Windows) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** заменить WinForms-окно ViviTune на тёмное окно Avalonia «как Hiddify» (боковое меню, Главная с кнопкой и скоростью через прокси, Приложения со списком запущенных программ, Лог, Настройки), сохранив всё поведение v1.

**Architecture:** ядро без привязки к Windows (`Settings`, `ConfigBuilder`, `ProxyProbe`, новые `TrafficMath`/`TrafficMeter`/`Format`, оркестратор `Session` поверх интерфейса `ISingBox`). Windows-код (`Runner` с Job Object, `ProcessCatalog` с иконками) лежит в `src/ViviTune/Windows/`. Окно Avalonia читает и пишет только `ViewState`. `App` связывает `Session` с `ViewState`. Отдельный консольный проект `ViviTune.Shots` рендерит страницы в PNG через Avalonia.Headless, это проверка вида без UAC.

**Tech Stack:** .NET 10 (`net10.0-windows`), Avalonia 12.x (последняя стабильная, версия зафиксирована в csproj), `Avalonia.Fonts.Inter`, `System.Text.Json`, `System.Drawing.Common` (только `Windows/`), `Avalonia.Headless` + `Avalonia.Skia` (только `ViviTune.Shots`), sing-box 1.12.12.

**Spec:** `docs/superpowers/specs/2026-10-18-vivitune-avalonia-ui-design.md`. База поведения: `docs/superpowers/specs/2026-10-04-vivitune-design.md`. Макет: `docs/superpowers/specs/assets/2026-10-18-ui-mockup.html`.

## Global Constraints

- TFM всех проектов: `net10.0-windows` (Linux — этап 2). Сборка: **0 предупреждений, 0 ошибок**.
- **Перед установкой .NET SDK 10 спросить пользователя** (сейчас стоит только 8.0.426). Команда: `winget install --id Microsoft.DotNet.SDK.10 -e`.
- Зависимости только перечисленные в Tech Stack. Никаких MVVM-фреймворков (ReactiveUI, CommunityToolkit) и наборов контролов.
- Тесты: консольный проект `src/ViviTune.Tests` с `Check(bool, string)`, без тестовых фреймворков; запуск `dotnet run --project src/ViviTune.Tests`, ожидание: только строки `ok:`/`skip:`, код 0.
- Windows-специфичный код только в `src/ViviTune/Windows/` (`Runner.cs`, `ProcessCatalog.cs`), плюс `app.manifest` и мьютекс в `Program.cs`. Остальные файлы не используют Win32 и `System.Drawing`.
- sing-box 1.12.12; конфиг меняется только добавлением `experimental.clash_api` (Task 3). Всё остальное в конфиге как сейчас.
- Сохраняются: `requireAdministrator`, один экземпляр (Mutex `ViviTune.SingleInstance`), `settings.json`/`config.json` в `AppContext.BaseDirectory`, проверка прокси перед стартом, предупреждение без UDP, Job Object.
- Палитра (точно): фон `#0e1120`, меню `#0b0d1a`, карточка `#171b2e`, линия `#252a44`, текст `#e8eaf6`, вторичный `#8a90b4`, акцент 1 `#22d3ee`, акцент 2 `#8b5cf6`, успех `#34d399`. Скругления 10–14 px, шрифт Inter, только тёмная тема. Минимальный размер окна 760×520.
- Тексты интерфейса на русском, дословно из спецификации там, где она их даёт.
- Коммиты заканчиваются строкой `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; работа идёт в отдельной ветке `avalonia-ui`.

## Review Focus

1. Быстрые переключения приложений во время подключения → ровно один перезапуск sing-box, итоговый конфиг по последнему состоянию (тест в Task 5).
2. Выключено последнее включённое приложение во время подключения → отключение, конфиг «без приложений» не собирается (тест в Task 5).
3. `Exited` от намеренных Stop/перезапуска не показывается как ошибка «sing-box завершился» (тест в Task 5).
4. Clash API ещё не поднялся, вернул ошибку или мусор → скорость 0, без исключений и без спама в лог (тесты в Task 4).
5. Счётчики соединения меньше прошлого снимка (переиспользованный id) → скорость не отрицательная (тест в Task 4).

---

### Task 1: Тулчейн .NET 10, каркас Avalonia, перенос ядра на System.Text.Json

**Files:**
- Modify: `src/ViviTune/ViviTune.csproj`, `src/ViviTune.Tests/ViviTune.Tests.csproj`, `src/ViviTune/Settings.cs`, `src/ViviTune/ConfigBuilder.cs`, `src/ViviTune/Program.cs`, `src/ViviTune.Tests/Program.cs`
- Move: `src/ViviTune/Runner.cs` → `src/ViviTune/Windows/Runner.cs`
- Delete: `src/ViviTune/MainForm.cs`
- Create: `src/ViviTune/Ui/App.axaml(.cs)`, `src/ViviTune/Ui/MainWindow.axaml(.cs)` (пустое тёмное окно «ViviTune»)

**Interfaces:**
- Produces: `Settings` и `ConfigBuilder.Build(Settings)` с прежним публичным API и поведением, но на `System.Text.Json` (у `Settings` публичные свойства `Ip`, `Port`, `Apps` вместо полей). Namespace `ViviTune` для ядра, `ViviTune.Windows` для `Runner`, `ViviTune.Ui` для окна.

- [ ] **Step 1:** Спросить пользователя про установку .NET SDK 10. После согласия поставить, проверить `dotnet --list-sdks`: есть строка `10.`.
- [ ] **Step 2:** Перевести тестовый проект на `net10.0-windows` (Nullable enable, ImplicitUsings enable). Вместо `Compile Include … Link` — `ProjectReference` на `src/ViviTune/ViviTune.csproj` (так тесты видят все публичные типы, включая `Ui/` и `Windows/`, без ручных ссылок на файлы). Заменить `JavaScriptSerializer` в тестах на `System.Text.Json.Nodes.JsonNode`. Все 22 проверки сохранить по смыслу и названиям.
- [ ] **Step 3:** Запустить тесты: ожидается ошибка сборки (ядро ещё на `System.Web.Script.Serialization`, главный проект ещё WinForms/net48).
- [ ] **Step 4:** Перевести `Settings` и `ConfigBuilder` на `System.Text.Json` (у `Settings` свойства вместо полей). Поведение не меняется: битый или отсутствующий файл даёт значения по умолчанию, `null`-список приложений превращается в пустой.
- [ ] **Step 5:** `dotnet run --project src/ViviTune.Tests`: 22 `ok:` (IPv6 может быть `skip:`), код 0.
- [ ] **Step 6:** Главный проект: `net10.0-windows`, `OutputType WinExe`, Avalonia (Desktop, Themes.Fluent, Fonts.Inter) с зафиксированной версией 12.x, `ApplicationManifest app.manifest`, копирование `third_party\sing-box.exe` в выход как сейчас. `Program.Main`: мьютекс `ViviTune.SingleInstance` как сейчас (второй экземпляр → сообщение «ViviTune уже запущен» и выход), затем `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`. `App` в `RequestedThemeVariant="Dark"`, `MainWindow` с фоном `#0e1120`, `MinWidth=760`, `MinHeight=520`. Удалить `MainForm.cs`, `Runner.cs` перенести в `Windows/` (namespace `ViviTune.Windows`).
- [ ] **Step 7:** `dotnet build src/ViviTune -c Release`: 0 предупреждений, 0 ошибок; в `bin/Release/net10.0-windows/` есть `ViviTune.exe` и `sing-box.exe`.
- [ ] **Step 8:** Коммит «Port core to .NET 10 + System.Text.Json, Avalonia skeleton».

---

### Task 2: Settings v2 — приложения с флагом «включено», чтение формата v1

**Files:**
- Modify: `src/ViviTune/Settings.cs`, `src/ViviTune.Tests/Program.cs`

**Interfaces:**
- Produces:
  - `public sealed class AppEntry { public string Path { get; set; } = ""; public bool Enabled { get; set; } = true; }`
  - `Settings.Apps : List<AppEntry>`; `Settings.Load(string path)` читает и новый формат, и v1 (`"Apps": ["C:\\a\\Discord.exe"]` → `{Path, Enabled=true}`); `Save` пишет новый формат.
  - `Validate()` возвращает `"Добавьте хотя бы одно приложение"`, если нет **включённых**; `ProcessNames()` берёт только включённые (остальные правила прежние).

- [ ] **Step 1:** Тесты (переписать существующие под `AppEntry` и добавить):
  - `Load: формат v1` — файл `{"Ip":"10.0.0.1","Port":1080,"Apps":["C:\\a\\Discord.exe","D:\\b\\chrome.exe"]}` → `Ip=="10.0.0.1"`, `Port==1080`, `Apps.Count==2`, `Apps[0].Path==@"C:\a\Discord.exe"`, все `Enabled`.
  - `Save/Load v2: туда и обратно` — `Enabled=false` у второго приложения сохраняется.
  - `ProcessNames: только включённые` — `[{chrome.exe,on},{Game.exe,off}]` → `["chrome.exe"]`.
  - `Validate: все выключены` → `"Добавьте хотя бы одно приложение"`.
  - `Load: элемент Apps неизвестного вида` (`"Apps":[42]`) → значения по умолчанию или пропуск элемента, без исключения.
- [ ] **Step 2:** Запуск тестов: падение компиляции (`AppEntry` нет).
- [ ] **Step 3:** Реализовать. Чтение через `JsonNode`/`JsonDocument`: элемент-строка → v1, элемент-объект → v2.
- [ ] **Step 4:** Все тесты `ok:`, код 0.
- [ ] **Step 5:** Коммит.

---

### Task 3: ConfigBuilder — Clash API

**Files:**
- Modify: `src/ViviTune/ConfigBuilder.cs`, `src/ViviTune.Tests/Program.cs`

**Interfaces:**
- Consumes: `Settings` (Task 2).
- Produces: `ConfigBuilder.Build(Settings s, int apiPort, string apiSecret) : string`. Добавляет `"experimental": {"clash_api": {"external_controller": "127.0.0.1:<apiPort>", "secret": "<apiSecret>"}}`. Остальной конфиг без изменений. Перегрузка `Build(Settings)` удаляется.

- [ ] **Step 1:** Тест `clash_api с переданными портом и секретом`: `Build(s, 51234, "abc")` → `experimental.clash_api.external_controller == "127.0.0.1:51234"`, `secret == "abc"`. Существующие тесты конфига вызывают `Build(s, 9090, "t")`.
- [ ] **Step 2:** Тесты падают (нет перегрузки).
- [ ] **Step 3:** Реализовать.
- [ ] **Step 4:** Тесты проходят. Дополнительно: сгенерировать конфиг (временный консольный проект вне репозитория, в scratchpad) и выполнить `src\ViviTune\third_party\sing-box.exe check -c <config.json>` — пустой вывод, код 0.
- [ ] **Step 5:** Коммит.

---

### Task 4: Скорость через прокси — TrafficMath, Format, TrafficMeter

**Files:**
- Create: `src/ViviTune/TrafficMeter.cs` (в нём `ConnSample`, `Speed`, `TrafficMath`, `TrafficMeter`), `src/ViviTune/Format.cs`
- Modify: `src/ViviTune.Tests/Program.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct ConnSample(string Id, long Upload, long Download, bool ViaProxy);`
  - `public readonly record struct Speed(double UpBps, double DownBps);`
  - `TrafficMath.Parse(string json) : List<ConnSample>` — разбирает ответ `GET /connections` (`{"connections":[{"id":"…","upload":N,"download":N,"chains":["proxy"]…}]}`); `ViaProxy` = `chains` содержит `"proxy"`; любой мусор или `null` → пустой список.
  - `TrafficMath.Delta(IReadOnlyDictionary<string, ConnSample> prev, IReadOnlyList<ConnSample> now, double seconds) : Speed` — сумма приростов только по `ViaProxy`; новое id считается от 0; прирост < 0 считается как 0; `seconds <= 0` → `Speed(0,0)`.
  - `sealed class TrafficMeter : IDisposable` — `TrafficMeter(int port, string secret)`, `event Action<Speed>? Sample` (≈1 раз/с, фоновый поток), `Start()`, `Stop()`, `Dispose()`. Запрос `http://127.0.0.1:<port>/connections` с `Authorization: Bearer <secret>`, таймаут ≤ 1 с; ошибка → `Sample(Speed(0,0))`, без исключений наружу.
  - `Format.Speed(double bps) : string` — `< 1024` → `"512 Б/с"`; `< 1024²` → `"1.5 КБ/с"`; иначе `"8.4 МБ/с"` (один знак, точка, InvariantCulture). `Format.Uptime(TimeSpan) : string` → `"00:42:17"` (часы могут быть > 99).

- [ ] **Step 1:** Тесты:
  - `Parse: proxy и direct` — JSON с двумя соединениями (`chains:["proxy"]` и `["direct"]`) → 2 элемента, `ViaProxy` true/false, байты на месте.
  - `Parse: мусор` — `"not json"`, `""`, `"{}"` → пустые списки, без исключений.
  - `Delta: прирост только proxy` — prev `{a: 100/1000 proxy}`, now `[a: 300/5000 proxy, b: 50/50 direct]`, 1 с → `Speed(200, 4000)`.
  - `Delta: новое соединение от нуля` — prev пуст, now `[c: 10/20 proxy]`, 2 с → `Speed(5, 10)`.
  - `Delta: счётчик уменьшился` — prev `{a: 500/500}`, now `[a: 100/100 proxy]` → `Speed(0, 0)`.
  - `Format.Speed`: `0 → "0 Б/с"`, `1536 → "1.5 КБ/с"`, `8808038 → "8.4 МБ/с"`; `Format.Uptime(42 мин 17 с) → "00:42:17"`.
  - `TrafficMeter: закрытый порт` — `new TrafficMeter(<закрытый порт>, "s")`, `Start()`, ждать ≤ 3 с первый `Sample` → `Speed(0,0)`, исключений нет.
  - `TrafficMeter: фейковый Clash API` — TcpListener-сервер (как `FakeServer`) отвечает `HTTP/1.1 200` с JSON, на втором запросе `upload` больше на 1000; проверка: пришёл `Sample` с `UpBps > 0`, в запросе был заголовок `Authorization: Bearer s`.
- [ ] **Step 2:** Тесты падают (типов нет).
- [ ] **Step 3:** Реализовать (`HttpClient` с одним экземпляром на `TrafficMeter`, цикл на `PeriodicTimer` или `Task.Delay(1000)`).
- [ ] **Step 4:** Тесты проходят, ни один не висит дольше 5 с.
- [ ] **Step 5:** Коммит.

---

### Task 5: Session — состояние подключения, перезапуск, ошибки

**Files:**
- Create: `src/ViviTune/Session.cs`
- Modify: `src/ViviTune/Windows/Runner.cs` (реализует `ISingBox`; если `sing-box.exe` нет — `Start` бросает `FileNotFoundException("Не найден sing-box.exe рядом с программой")`), тесты.

**Interfaces:**
- Consumes: `Settings` (Task 2), `ConfigBuilder.Build(Settings,int,string)` (Task 3), `ProbeResult`, `TrafficMeter`, `Speed` (Task 4).
- Produces:
  - `public interface ISingBox { event Action<string>? Line; event Action? Exited; bool Running { get; } void Start(string configPath); void Stop(); }` (в `Session.cs`)
  - `public enum ConnState { Disconnected, Checking, Connected, Restarting }`
  - `public sealed class Session : IDisposable`:
    - `Session(Settings settings, ISingBox singBox, string configPath, Func<string, int, ProbeResult> probe, TimeSpan restartDelay)`
    - свойства `ConnState State`, `string? Error`, `bool? UdpOk`, `DateTime? ConnectedSince`
    - события `Action? Changed` (любое свойство изменилось), `Action<string>? Log` (сообщения ViviTune и строки sing-box), `Action<Speed>? Speed`
    - `Task ConnectAsync()` — `Validate()` → ошибка в `Error`; иначе `Checking`, probe в фоне; недоступен → `Disconnected` + `Error = "Прокси недоступен: <r.Error>"`; иначе свободный порт + случайный секрет (16 байт hex), `config.json`, `Start`, `TrafficMeter.Start`, `Connected`, `UdpOk = r.UdpSupported`, лог «Перезапустите выбранные приложения: уже открытые соединения остаются прямыми.». Исключение `Start` → `Disconnected` + `Error = "Не удалось запустить: <msg>"`.
    - `void Disconnect()` — намеренная остановка, `Error = null`.
    - `void AppsChanged()` — вызывается UI после изменения `Settings.Apps`. Если `Connected` или `Restarting`: нет включённых → `Disconnect()`; иначе перезапуск через `restartDelay` после **последнего** вызова (`Restarting` → Stop → новый конфиг → Start → `Connected`, без повторной проверки прокси, лог «Перезапуск sing-box: список приложений изменён»).
    - неожиданный `Exited` (не во время Stop/перезапуска) → `Disconnected`, `Error = "sing-box завершился, прокси не работает"`.

- [ ] **Step 1:** Тесты с `FakeSingBox : ISingBox` (счётчики `Starts`/`Stops`, метод `Crash()` вызывает `Exited`) и probe-заглушкой:
  - `Session: успешное подключение` → `Connected`, `Starts==1`, `UdpOk==true`, `config.json` существует.
  - `Session: прокси недоступен` → `Disconnected`, `Error` начинается с `"Прокси недоступен"`, `Starts==0`.
  - `Session: нет включённых приложений` → `Error=="Добавьте хотя бы одно приложение"`, `Starts==0`.
  - `Session: три быстрых изменения → один перезапуск` (`restartDelay` 50 мс; три `AppsChanged` подряд, ждать 300 мс) → `Starts==2`, `Stops==1`, `State==Connected`. *(Review Focus 1)*
  - `Session: выключено последнее приложение` → `Disconnected`, `Starts==1`, `Error==null`. *(Review Focus 2)*
  - `Session: Exited при перезапуске и Disconnect не ошибка` — фейк вызывает `Exited` внутри `Stop()`; после перезапуска `State==Connected`, после `Disconnect()` `Error==null`. *(Review Focus 3)*
  - `Session: неожиданный выход` — `Crash()` в `Connected` → `Disconnected`, `Error=="sing-box завершился, прокси не работает"`.
- [ ] **Step 2:** Тесты падают (нет `Session`).
- [ ] **Step 3:** Реализовать; все изменения состояния под одним `lock`, события вызываются вне `lock`.
- [ ] **Step 4:** Тесты проходят. `Runner` компилируется как `ISingBox` (`dotnet build src/ViviTune`, 0 предупреждений).
- [ ] **Step 5:** Коммит.

---

### Task 6: ProcessCatalog — запущенные программы и иконки (Windows)

**Files:**
- Create: `src/ViviTune/Windows/ProcessCatalog.cs`
- Modify: `src/ViviTune/ViviTune.csproj` (`System.Drawing.Common`), тесты.

**Interfaces:**
- Produces (namespace `ViviTune.Windows`):
  - `public sealed record RunningApp(string Path, string Name);`
  - `ProcessCatalog.Running(IEnumerable<string> excludePaths) : List<RunningApp>` — уникальные пути `.exe` запущенных процессов; пропуск процессов без доступного пути (ошибка доступа, процесс завершился); исключаются файлы в `%WINDIR%`, текущий процесс, любой `sing-box.exe` и `excludePaths` (без учёта регистра); сортировка по `Name`.
  - `ProcessCatalog.DisplayName(string exePath) : string` — `FileDescription`, если непустой, иначе имя файла без `.exe`.
  - `ProcessCatalog.IconPng(string exePath) : byte[]?` — PNG 32×32 через `Icon.ExtractAssociatedIcon`; любая ошибка → `null`.

- [ ] **Step 1:** Тесты:
  - `Running: нет файлов из WINDIR и sing-box` — для всех элементов путь не начинается с `%WINDIR%`, имя файла не `sing-box.exe`; вызов не бросает.
  - `Running: excludePaths работает` — путь текущего процесса тестов (`Environment.ProcessPath`) не в списке (исключён как «текущий»); путь из `excludePaths` в другом регистре тоже не в списке.
  - `IconPng: explorer.exe` → не `null`, начинается с сигнатуры PNG (`0x89 'P' 'N' 'G'`); `IconPng("C:\\нет\\такого.exe")` → `null`.
  - `DisplayName: без описания` — для временного файла `x.exe` (пустой) → `"x"`.
- [ ] **Step 2:** Тесты падают.
- [ ] **Step 3:** Реализовать.
- [ ] **Step 4:** Тесты проходят.
- [ ] **Step 5:** Коммит.

---

### Task 7: Оболочка окна — тема, ViewState, меню, Лог, Настройки, связка с Session, Shots

**Files:**
- Create: `src/ViviTune/Ui/Theme.axaml` (кисти палитры, стили карточек, кнопок, переключателей), `src/ViviTune/Ui/ViewState.cs`, `src/ViviTune/Ui/LogPage.axaml(.cs)`, `src/ViviTune/Ui/SettingsPage.axaml(.cs)`, заглушки `HomePage`/`AppsPage` (заголовок страницы), `src/ViviTune.Shots/ViviTune.Shots.csproj`, `src/ViviTune.Shots/Program.cs`
- Modify: `src/ViviTune/Ui/App.axaml(.cs)`, `src/ViviTune/Ui/MainWindow.axaml(.cs)`, `.gitignore` (`artifacts/`)

**Interfaces:**
- Consumes: `Session`, `ConnState`, `Settings`, `Format` (Tasks 2–5), `Runner` (Task 1/5).
- Produces:
  - `public sealed class ViewState : INotifyPropertyChanged` с свойствами: `ConnState State`, `string StatusText`, `string? ErrorText`, `string? WarningText`, `string DetailText`, `string ProxyLabel` (`"SOCKS5 · <ip>:<port>"`), `double UpBps`, `double DownBps`, `ObservableCollection<double> UpHistory`, `ObservableCollection<double> DownHistory` (не больше 60), `ObservableCollection<AppRow> Apps`, `ObservableCollection<string> LogLines` (не больше 500), `string Ip`, `int Port`, `string? SettingsError`, `bool SettingsEditable`.
  - `public sealed class AppRow : INotifyPropertyChanged { string Path; string Name; string FileName; Bitmap? Icon; bool Enabled; }`
  - Действия пользователя страницы передают через `ViewState`, а не напрямую в `Session`: методы `RequestToggle()`, `RequestAddApp(string path)`, `RequestRemoveApp(string path)`, `RequestSetAppEnabled(string path, bool enabled)`, `RequestSetProxy(string ip, int port)` поднимают одноимённые события (`ToggleRequested`, `AddAppRequested`, `RemoveAppRequested`, `SetAppEnabledRequested`, `SetProxyRequested`). `App` подписан на них: `ToggleRequested` → `ConnectAsync()` при `Disconnected`, иначе `Disconnect()`; `SetProxyRequested` → `Settings` → `Save`; события приложений подключаются в Task 9. В Shots никто не подписан, клики ничего не делают.
  - `MainWindow(ViewState state)` — боковое меню (Главная, Приложения, Лог, Настройки; активный пункт акцентом 1), справа текущая страница; каждая страница — `UserControl` с `DataContext = state`.
  - `App` при старте: `Settings.Load`, `Runner`, `Session(..., ProxyProbe.Probe, TimeSpan.FromSeconds(1))`, заполняет `ViewState`; события `Session` пробрасывает в `ViewState` через `Dispatcher.UIThread.Post`; закрытие окна → `Session.Dispose()` (останавливает sing-box). Изменения IP/порта в `ViewState` → `Settings` → `Save`, ошибки сохранения игнорируются.
  - `ViviTune.Shots` (консоль, `net10.0-windows`, ProjectReference на `ViviTune`, `Avalonia.Headless`, `Avalonia.Skia`): `dotnet run --project src/ViviTune.Shots -- <outDir>` рисует `MainWindow` 900×600 с демо-`ViewState` на каждой странице и сохраняет `home.png`, `apps.png`, `log.png`, `settings.png`.
- Страница **Лог**: моноширинный шрифт, прокрутка держится внизу при новых строках.
- Страница **Настройки**: поля IP и порт, ошибка под полями (только про IP/порт), неактивны при `State != Disconnected`, внизу `ViviTune <версия сборки> · sing-box 1.12.12`.

- [ ] **Step 1:** Тест (в `ViviTune.Tests`): `ViewState: лимиты` — 70 добавлений в `UpHistory` через метод `PushSpeed(Speed)` оставляют 60 значений; 600 `AddLog` оставляют 500 последних. (Имена методов: `void PushSpeed(Speed s)`, `void AddLog(string line)`.)
- [ ] **Step 2:** Тест падает.
- [ ] **Step 3:** Реализовать `ViewState`, тему, меню, страницы Лог и Настройки, связку в `App`, проект Shots.
- [ ] **Step 4:** Тесты проходят; `dotnet build` всего решения — 0 предупреждений; `dotnet run --project src/ViviTune.Shots -- artifacts/shots` создаёт 4 PNG; `log.png` и `settings.png` визуально соответствуют палитре и макету (приложить пути в отчёт).
- [ ] **Step 5:** Коммит.

---

### Task 8: Главная — кнопка, статус, скорость с графиками

**Files:**
- Create: `src/ViviTune/Ui/HomePage.axaml(.cs)` (заменяет заглушку), `src/ViviTune/Ui/Sparkline.cs` (свой `Control`: свойство `Values : IReadOnlyList<double>`, `Stroke : IBrush`, рисует линию и полупрозрачную заливку под ней, масштаб по максимуму, при пустых данных — ровная линия у низа)
- Modify: `src/ViviTune/Ui/ViewState.cs` при необходимости, `src/ViviTune.Shots/Program.cs` (демо-состояния кнопки)

**Interfaces:**
- Consumes: `ViewState` (Task 7; нажатие кнопки → `ViewState.RequestToggle()`), `Format` (Task 4).
- Вид по спецификации, раздел «Главная»: плашка `ProxyLabel`; круглая кнопка ~130 px со значком питания; состояния: **Отключено** (серое кольцо), **Проверка/Перезапуск** (кольцо крутится, кнопка неактивна), **Подключено** (кольцо-градиент `#22d3ee → #8b5cf6` со свечением); статус «Отключено» / «Проверяю прокси…» / «Перезапуск…» / «Подключено» (зелёным `#34d399`); `DetailText` при подключении — `"<Format.Uptime> · UDP ✓|UDP ✗ · <N> прилож."`, обновляется раз в секунду; `ErrorText` красным и `WarningText` (`"UDP через этот прокси не работает: игры и голосовая связь могут не заработать."`) под кнопкой; карточки «↑ Отдача через прокси» (акцент 2) и «↓ Загрузка через прокси» (акцент 1) с `Format.Speed` и `Sparkline` за 60 с.

- [ ] **Step 1:** Тест: `Format`/`ViewState` для `DetailText` — функция `ViewState.Detail(TimeSpan up, bool? udp, int apps) : string` → `Detail(42м17с, true, 2) == "00:42:17 · UDP ✓ · 2 прилож."`.
- [ ] **Step 2:** Тест падает.
- [ ] **Step 3:** Реализовать страницу и `Sparkline`; Shots рисует `home-disconnected.png`, `home-checking.png`, `home-connected.png` (с демо-историей скорости), `home-error.png`.
- [ ] **Step 4:** Тесты и сборка зелёные; PNG соответствуют макету `assets/2026-10-18-ui-mockup.html` (Главная): пути в отчёт.
- [ ] **Step 5:** Коммит.

---

### Task 9: Приложения — список, переключатели, добавление из запущенных

**Files:**
- Create: `src/ViviTune/Ui/AppsPage.axaml(.cs)` (заменяет заглушку)
- Modify: `src/ViviTune/Ui/App.axaml.cs` (операции со списком), `src/ViviTune.Shots/Program.cs`

**Interfaces:**
- Consumes: `ViewState.Apps`/`AppRow` (Task 7), `ProcessCatalog` (Task 6), `Session.AppsChanged()` (Task 5), `Settings.Apps` (Task 2).
- Вид по спецификации, раздел «Приложения»: заголовок «Приложения через прокси»; строка — иконка (или плашка с первой буквой), имя, имя файла мелко, переключатель, «✕»; пустой список — «Добавьте приложение, трафик которого пойдёт через прокси». Кнопка «＋ Добавить» открывает выпадающую панель (`Flyout`/`Popup`): поле поиска (фильтр по имени без учёта регистра), раздел «Запущены сейчас» (`ProcessCatalog.Running` с исключением уже добавленных; загрузка списка и иконок не в UI-потоке), пункт «Выбрать .exe вручную…» (`StorageProvider.OpenFilePickerAsync`, фильтр `*.exe`). Добавленное — сразу `Enabled=true`.
- Страница вызывает `ViewState.RequestAddApp/RequestRemoveApp/RequestSetAppEnabled` (Task 7). `App` на эти события: `AppListOps` над `Settings.Apps` → `Save` → обновить `ViewState.Apps` → `Session.AppsChanged()`.

- [ ] **Step 1:** Тест: `AppList: добавление без дублей` — функция `AppListOps.Add(List<AppEntry> apps, string path) : bool` (в `Ui/AppListOps.cs`, без зависимостей от Avalonia) не добавляет путь, который уже есть в другом регистре, и возвращает `false`; `AppListOps.Remove`/`SetEnabled` меняют список по пути без учёта регистра.
- [ ] **Step 2:** Тест падает.
- [ ] **Step 3:** Реализовать страницу и `AppListOps`; Shots рисует `apps.png` (2 приложения, одно выключено) и `apps-add.png` (открытая панель с демо-списком).
- [ ] **Step 4:** Тесты и сборка зелёные; PNG соответствуют макету (Приложения): пути в отчёт.
- [ ] **Step 5:** Коммит.

---

### Task 10: Публикация, проверка жизненного цикла, ручная проверка

**Files:**
- Modify: `src/ViviTune/ViviTune.csproj` (publish-профиль: `RuntimeIdentifier win-x64`, `SelfContained true`, `PublishSingleFile true`, `IncludeNativeLibrariesForSelfExtract true`), `.wolf/STATUS.md`

- [ ] **Step 1:** `dotnet publish src/ViviTune -c Release -o artifacts/publish`: в папке `ViviTune.exe` (один файл) и `sing-box.exe`; 0 предупреждений.
- [ ] **Step 2:** Проверка жизненного цикла тем же способом, что 2026-10-18: временный проект в scratchpad, ссылки на `Windows/Runner.cs`, `Settings.cs`, `ConfigBuilder.cs`, `requireAdministrator`; один запрос UAC (предупредить пользователя). Ожидание: после `Stop()` и после жёсткого убийства родителя `sing-box` нет, адаптера TUN нет, маршрутов `172.19.0.x` нет, интернет работает. Дополнительно в работающем состоянии `GET /connections` Clash API с секретом отвечает 200.
- [ ] **Step 3:** Ручная проверка пользователем по списку из спецификации (раздел «Проверка»): вид 4 страниц; Discord с голосом через прокси; скорость растёт от выбранного приложения и стоит на нуле от остальных; добавление из списка запущенных; выключение приложения во время работы перезапускает sing-box; старый `settings.json` подхватился.
- [ ] **Step 4:** Обновить `.wolf/STATUS.md` (что сделано, этап 2 Linux — следующий), коммит.
