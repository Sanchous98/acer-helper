# `IDevice` и вопрос об Application

Предложение к решению владельца. **Ничего не двигалось**: ни один продакшн-файл, ни один тест, ни
одна строка `settings.json` не тронуты. Документ — единственное, что этот заход создал.

Повод — две правки владельца: «про `IDevice` я тебе уже сказал не один раз: не надо этого
интерфейса» и «если Application полностью опустел, значит ты ошибся в архитектуре, перепроверь».
Проверялась гипотеза: прошлая волна (`e7513ac`) разложила `LaptopService` по **имени**, а не по
ответственности — имя «сервис над ноутбуком» звучит как инфраструктура, — и потому Application
опустел.

---

## 0. Ответ в одну страницу

**Гипотеза верна наполовину, и половина, которая неверна, — решающая.**

- **Верно:** члены `LaptopService` — это в основном use cases. Измерено ниже: 29 действий и 17
  запросов против 12 аксессоров состояния. Название класса действительно не описывает его
  содержимое, и решение `e7513ac` действительно было принято по названию — владелец сам назвал
  именно эту причину: «само название LaptopService говорит, что ему в application не место».
- **Неверно:** из этого не следует, что класс надо перенести. **То же размещение вынуждается
  независимо** — тремя решениями владельца и одним правилом в `ArchitectureMapTests`. Убери
  довод о названии — класс останется там же. То есть прошлая волна пришла к верному выводу
  неверным рассуждением, и это не одно и то же с ошибкой.
- **Application опустел не из-за этого переноса.** Он опустел бы и без него: 24 из 29 действий и
  12 из 17 запросов трогают персистентный граф, а граф — Infrastructure по решению владельца;
  ещё 9 из 14 портов, которые эти действия дёргают, — Infrastructure по классификации самой
  карты. Две стены, обе поставлены решениями, а не переездом. **Пустота Application —
  следствие, а не промах.**

**Но пустота — настоящая проблема, и она не лечится переездом.** У неё ровно два лекарства, и оба
— решения владельца, а не работа: либо контейнер настроек перестаёт быть Infrastructure, либо
порты, моделирующие технологию вендора, перестают быть Infrastructure. Пока оба решения стоят,
любой use case в Application назовёт Infrastructure и покрасит
`ApplicationPointsAtDomainNotInfrastructure` — и это проверено мутацией, а не выведено
(`docs/domain-layering-map.md`, блок «ЗАБЛОКИРОВАНО»).

**Про `IDevice`.** Его удаление — вещь дешёвая и правильная сама по себе, и она **не** двигает
Application ни на шаг: интерфейс уходит, а инвентарь возможностей остаётся — либо как модель
(форма 1), либо растворяется в дескрипторах (форма 2). Что именно имел в виду владелец —
«не надо интерфейса» или «не надо агрегата» — из кода не выводится; обе формы ниже, с ценой.

---

## 1. Классификация (задача 1)

### 1.1. Что лежит в `Application/` сегодня

Три файла, 172 строки:

| Тип | Что это | Классификация | Потребители |
|---|---|---|---|
| `AppArgs` | `internal static`, одна константа `--startup` | **контракт/константа** | `Autostart.Windows.cs`, `Autostart.Linux.cs`, `UI/App.axaml.cs` — 3, все Infrastructure/UI |
| `IDynamicLighting` | Контракт виртуальной LampArray-поверхности: `Enabled`, `LastError`, `Enable`, `Disable`, `HostOwnsLighting`, `OwnerChanged`, `Reassert` | **контракт** | `LampArrayBridge` (реализация), `LaptopService`, `LightingViewModel` |
| `IDynamicLightingFactory` | Фабрика поверхности над зонами устройства | **контракт** | `LampArrayBridgeFactory`, `DeviceFactory`, `LaptopService` |
| `ReapplyTrigger` | Триггер переприменения: `Startup` / `ModeChange` / `Resume` | **use case** (момент) | `LaptopService`, `HardwareReconciler`, `AppController`, `LightingCoordinator` |
| `ReapplyPlan` | `internal static`: какие оси какой триггер ведёт, в каком порядке, на каком потоке, отражает ли вызывающий | **use case** (план действий) | `HardwareReconciler` |

Ни один тип под `Application/` не назван из `Domain/`; `Application/` называет `AcerHelper.Domain`
и ничего больше. `Application/` называет ровно три доменных имени: `ModeAxis`, `ModeAxisTable` (в
`ReapplyPlan`) и `IRgbDevice`/`RgbZone` (в `IDynamicLightingFactory`).

### 1.2. Публичная поверхность `LaptopService`: 62 члена + конструктор

Партиалы: `LaptopService.cs` (6), `.Profiles.cs` (13), `.Fans.cs` (7), `.Lighting.cs` (5),
`.Tuning.cs` (15), `.Toggles.cs` (16). Плюс четыре `internal` члена.

**А. Use case — действие: 29.**

| Член | Что делает | Потребитель в продакшне |
|---|---|---|
| `ApplyProfile` | Сменить профиль и запомнить его за живым источником питания | `AppController.ApplyProfile` |
| `SetTurbo` | Turbo как переключатель над базой | `AppController.SetTurbo` |
| `TogglePerformance` | Хоткей: следующий профиль или Turbo | `AppController.OnTogglePerformance` |
| `SetSourceProfile` | Профиль, закреплённый за источником | `OptionsAssembler.PowerSourceProfiles` |
| `SyncPowerSource` | Переприменить запомненный режим на смене AC↔батарея | `AppController.BackgroundPass` |
| `SetFan` | Режим кулеров + фиксированные обороты на текущий режим | `AppController.SetFan` |
| `SetFanCurve` | Кривая одного кулера на текущий режим | `AppController.SetFanCurve` |
| `ApplyCustom` | Вести кулеры по кривой с дедбендом (каждый опрос) | `AppController.BackgroundPass` |
| `SetGpuOc` | Офсеты dGPU на текущий режим | `AppController.SetGpuOc` |
| `SetCpuPower` | Power-overlay ОС на текущий режим | `AppController.SetCpuPower` |
| `SetCo` | All-core смещение CO | внутренний маршрут через `SetCoValues` |
| `SetCoValues` | Вилка «как этот CPU берёт смещения» | `AppController.SetCo` |
| `SetCoDomains` | По-рельсовые смещения CO | внутренний маршрут через `SetCoValues` |
| `ApplySetting` | Применить объявленную настройку и запомнить | `OptionsAssembler.RunSet` |
| `SetBatteryToggle` | Свойство батареи (лимит, калибровка) | `OptionsAssembler` |
| `SetBatteryChoice` | Выбор батареи (режим заряда) | `OptionsAssembler` |
| `SetKeyboardBrightness` | Яркость обычной подсветки | `AppController.BuildUi` |
| `SetAutostart` | Автозапуск | `AppController.BuildUi` |
| `SetBlueLight` | Синий фильтр | `OptionsAssembler` |
| `SetClamshell` | Не спать при закрытой крышке | `AppController.BuildUi` |
| `SetTurboToggles` | Поведение клавиши Turbo | `AppController.SetTurboToggles` |
| `SetLanguage` | Язык | `AppController.SetLanguage` |
| `SetDynamicLighting` | Включить/выключить виртуальную LampArray | `OptionsAssembler.Toggles` |
| `ApplyStartupState` | Переприменить состояние, которое ОС не помнит | `AppController` (конструктор) |
| `EvaluateClamshell` | Пересчитать режим сна при крышке | `AppController.BackgroundPass` |
| `ApplyModeFan` | Ось `Fans` на смене режима | `HardwareReconciler.ReapplyAxis` |
| `ApplyModeGpuOc` | Ось `GpuOc` | `HardwareReconciler.ReapplyAxis` |
| `ApplyModeCpuPower` | Ось `CpuPower` | `HardwareReconciler.ReapplyAxis` |
| `ApplyModeCo` | Ось `Co` | `HardwareReconciler.ReapplyAxis` |

**Б. Use case — запрос: 17.**

`CurrentProfile`, `SelectableProfiles`, `IsTurboOn`, `BaseProfile()`, `BaseProfile(cur)`,
`SourceProfile`, `CurrentFan()`, `CurrentFan(cur)`, `CurrentGpuOc()`, `CurrentGpuOc(cur)`,
`CurrentCo()`, `CurrentCo(cur)`, `CurrentCoDomains()`, `CurrentCoDomains(cur)`, `CurrentCpuPower`,
`ReadSensors`, `ReadBatteryInfo`. Потребители — `AppController` (сборка UI, пасс опроса),
`OptionsAssembler`, `TrayController` — все через UI.

Из них **состояния не трогают пятеро**: `CurrentProfile`, `SelectableProfiles`, `IsTurboOn`,
`ReadSensors`, `ReadBatteryInfo` — это чистые пересылки в порт.

**В. Состояние: 12 аксессоров + 6 полей.**

| Член | Что за состояние | Классификация |
|---|---|---|
| `Settings` (internal) | Весь граф: пять словарей пресетов, два слота источников, скаляры, мешок флагов, объявленный набор | **состояние** — форма `settings.json` |
| `CurrentModeKey()`, `CurrentModeKey(cur)` | Ключ режима из `Settings.TurboToggles` + `ProfileMemory` живого источника | состояние + правило (`ModeKeyFor`) |
| `LightsForCurrentMode()` ×2 | **Живая ссылка** в `Settings.LightPresets[key].Zones` | состояние (записанное решение, open-decisions §4) |
| `EnsureLightZone` | Структурная вставка в тот же словарь под `_state` | состояние (примитив мутации, отданный наружу) |
| `GetDeviceFlag` / `SetDeviceFlag` | Мешок `Settings.DeviceSettings` | состояние |
| `PersistLighting` | `Save()` графа | состояние |
| `DeclaredSettings` | Runtime-половина модели: что объявил бэкенд этой машины | состояние (не персистится) |
| `TurboToggles`, `Language`, `Bluelight` | Три скаляра под `_state` | состояние |
| поля `_onAc`, `_fanCurve` | Последний увиденный источник; память дедбенда пары кулеров | состояние |
| поля `_lampArray`, `_lampArrayBuilt` | Ленивая поверхность LampArray | состояние (инфраструктурное) |
| поле `_state` | Замок графа | состояние |

**Г. Инфраструктурная забота: 3.**

| Член | Почему инфраструктура | Потребитель в продакшне |
|---|---|---|
| `LampArray` | Ленивая сборка моста через фабрику; ОС, драйвер, PnP-узел | `AppController.BuildUi`, `LightingCoordinator.Paint`, `OptionsAssembler.Toggles` |
| `Device` | **Инвентарь возможностей этой машины** (16 членов `IDevice`) | `AppController` ×3, `LightingCoordinator` ×7, `OptionsAssembler` ×5 |
| `ApplyFan` | Порядок записи в EC («сначала режим Custom, потом обороты»); вендорская деталь транспорта | нет вне класса — только `SetFan`, `ApplyModeFan`, `ApplyCustom` |

**Д. Прочее: 6.** `Dispose` (жизненный цикл), конструктор (композиция), `Reconciler` (internal —
исполнитель плана, `AppController` + `LightingCoordinator`), `StateHeld` (internal — тестовый шов),
`ModeKeyFor` (internal static — правило свитча Turbo, доменное по существу),
`Attempt` ×2 (private static — свёртка исхода записи).

**Е. Мёртвая поверхность: 9 публичных членов без продакшн-потребителя.**
`CurrentModeKey()`, `CurrentFan()`, `CurrentGpuOc()`, `CurrentCo()`, `IsTurboOn()`, `BaseProfile()`,
`ApplyFan`, `SetCo`, `SetCoDomains` — вызываются только изнутри класса или из тестов. Ни один из
них не удаляется этим предложением, но их существование — часть объяснения, почему класс читается
как «сервис»: у него широкая публичная поверхность, большая часть которой никому не нужна.

### 1.3. `IDevice`: 16 членов, 3 реализации, 25 сайтов

- **Объявлен:** `Domain/Ports.cs`, `public interface IDevice : IDisposable`.
- **Члены:** `VendorName`, `StatusMessage`, `Battery` (ненулевой доменный объект) и 13 nullable
  портов: `PowerProfiles`, `FanControl`, `Sensors`, `KeyboardBrightness`, `Lighting`, `Hotkeys`,
  `DisplayTint`, `GpuOverclock`, `CpuPower`, `CurveOptimizer`, `DriverSetup`, `Autostart`,
  `Clamshell`.
- **Реализации:** `GenericDevice` (прямая), `AcerDevice`/`DellDevice` (наследники), `FakeDevice`
  (тесты, единственная).
- **Создаётся:** ровно в одном месте — `DeviceFactory.Create()`; `new AcerDevice` / `new DellDevice`
  / `new GenericDevice` по DMI-производителю.
- **Потребители в продакшне:** `LaptopService` (владение), `DeviceFactory` (создание),
  `OptionsAssembler` (5 probe-and-hide), `AppController` (3 чтения `Device` + 8 чтений портов),
  `LightingCoordinator` (4), `MainViewModel` (8), `OptionsViewModel` (3), `TrayController` (2).
- **В `Domain/` и `Application/` — ноль обращений по коду.** Только `cref` в доккомментариях.
  Домен через `IDevice` **не рассуждает**. Это и есть измеренный ответ на вопрос карты §3.1:
  чтение (A) «доменный контракт» кодом не подтверждается, чтение (B) «инвентарь конкретной
  машины» — подтверждается.
- **Probe-and-hide сайтов — 17** (полный список ушёл в отчёт по замеру; сводка: `MainViewModel` 7,
  `OptionsAssembler` 5, `OptionsViewModel` 3, `AppController` 1, `TrayController` 1).

### 1.4. Остальное под `Infrastructure/Composition/`

`HardwareReconciler` + `ReapplyOutcome` — **исполнитель** плана Application (не use case и не
состояние: цикл). `ModeKey` — **состояние** (ключ словарей, форма хранения). `Settings` + пять
типов пресетов + `ProfileMemory` + `ISettingsStore` + `JsonSettingsStore` — **состояние и его
форма**. `DeviceFactory` — **композиция**. `OptionsAssembler` — **use case** (сборка строк) +
5 probe-and-hide. `LampArrayBridgeFactory` — **композиция**.

---

## 2. Исправленная картина (задача 2)

### 2.1. Как удаляется `IDevice`, по каждому сайту

Ключевое наблюдение: **у 17 probe-and-hide сайтов один и тот же вопрос — «есть ли у этой машины
X?»**, и у вопроса два разных продолжения:

- **дескриптор** — значения, которые UI показывает и по которым решает, быть ли секции
  (`GpuOverclock.Name/CoreRange/MemRange` → `GpuViewModel`; `CurveOptimizer.Name/Range/
  MillivoltsPerCount/Domains` → `CoViewModel`; `CpuPower.Modes` → `CpuViewModel`;
  `FanControl.Capability` → `FansViewModel`);
- **операция** — запись, которую дёргает use case (`Set`, `Apply`, `Enable`).

UI нужен **только дескриптор**: ни одна вью-модель не вызывает `Set` на порту — запись приходит
делегатом (`UiActions`). Это и есть рычаг для удаления агрегата без переименования.

| Сайт `IDevice` | Что берёт | Чем заменяется |
|---|---|---|
| `LaptopService` (поле, параметр, `Device`) | все 14 портов + `Battery` | Порты по одному в конструкторе (форма 1/2) либо инвентарь-модель |
| `LaptopService.Dispose` | `device.Dispose()` | То же — владение остаётся у сервиса |
| `DeviceFactory.Create` | создаёт и возвращает | Возвращает модель вместо интерфейса |
| `MainViewModel` ×8 | `VendorName`, `Sensors`, `PowerProfiles`, `FanControl`, `GpuOverclock`, `CpuPower`, `CurveOptimizer` | **Дескрипторы**: `DeviceName`, `FanInfo(Capability)`, `GpuTuning(Name, CoreRange, MemRange)`, `CpuPowerModes(Modes)`, `UndervoltInfo(Name, Range, mV, Domains)`; «есть ли датчики» — флаг телеметрии |
| `OptionsViewModel` ×3 | `Clamshell`, `PowerProfiles`, `Autostart` | `Label`/`Enabled` кламшелла, `All.Any(Turbo)`, `Label`/`IsEnabled` автозапуска — **значения**, не порты |
| `OptionsAssembler` ×5 | `DisplayTint.Levels`, `PowerProfiles.All`, `Battery.ChargeMode/ChargeLimit/Calibration` | `Levels > 0` уже значение; `All.Count > 0`; три свойства батареи — **уже** объявляют себя сами (образец, по которому идёт остальное) |
| `AppController` ×11 | `Hotkeys`, `Autostart`, `DriverSetup`, `Lighting`, `KeyboardBrightness`, `CpuPower`, `CurveOptimizer`, `Battery`, `StatusMessage` | `Hotkeys`/`DriverSetup`/`Lighting` — **операции и события**, остаются контрактами; `StatusMessage` — значение |
| `TrayController` ×2 | `PowerProfiles.All`, `Lighting`/`KeyboardBrightness` | Список профилей — значение; «есть ли подсветка» — `Zones.Count > 0 \|\| BacklightMaxLevel > 0` |
| `LightingCoordinator` ×4 | `Lighting.SetProfileFlash/Blank`, `Clamshell.Enabled` | Операция и значение; остаются |
| `App.axaml.cs` | получает `(device, declaredSettings)` | То же, тип меняется |

**Что заменяется «ничем»:** `VendorName` + `StatusMessage` — это два значения, они уезжают в
модель/дескриптор и агрегат больше не оправдывают. `Hotkeys` и `DriverSetup` — единственные два
порта, которым **нужен** носитель: событие и глагол установки не имеют «пустого» значения. Это
честная нижняя граница: **после удаления `IDevice` остаются ровно два порта, которым нужен общий
держатель, и ноль причин держать их вместе.**

### 2.2. Три формы, из которых владельцу выбирать

#### Форма 1 — инвентарь становится моделью (интерфейс уходит, агрегат остаётся)

- Удалить `IDevice` из `Domain/Ports.cs`.
- Завести `Device` — **класс**, не интерфейс, рядом с `GenericDevice`: те же 16 членов,
  `GenericDevice : Device` заполняет, `AcerDevice`/`DellDevice` наследуют.
- `FakeDevice` → `new Device { Lighting = … }`; **40 сайтов `f.Device.<Port> = …` в тестах не
  меняют форму** (члены остаются устанавливаемыми).
- 17 probe-and-hide сайтов не меняются; меняется имя типа и `using` в 13 продакшн-файлах.
- **Что это даёт:** инверсия снята — домен перестаёт объявлять контракт, который реализует
  инфраструктура; инвентарь становится тем, чем был по чтению (B) карты. **Вопрос «шага 6» карты
  (десять портов уезжают только вместе с `IDevice`) растворяется**: у портов больше нет списка, к
  которому они привязаны, и каждый судится сам по себе.
- **Чего это не даёт:** Application не двигается ни на шаг. Агрегат остался — если владелец имел в
  виду «не надо агрегата», это не тот ответ.

#### Форма 2 — инвентарь растворяется в дескрипторах (обе правки)

- Каждый порт расщепляется на **дескриптор** (доменная модель: значения) и **операцию**
  (контракт, объявленный Application, реализованный Infrastructure — как `IDynamicLighting`
  сегодня).
- Агрегат `IDevice` заменяется **доменной моделью машины** (набор дескрипторов + `Battery` +
  два контракта, которым нужен держатель), которую **создаёт и наполняет Infrastructure** — это
  буквально формулировка владельца: «инфраструктура сама создаст все модели и наполнит настройками
  согласно конфигурации».
- Тогда Application **может** называть инвентарь и дескрипторы (Domain) — и в него переезжают
  use cases, **которые не трогают персистентный граф**: 5 действий (`SetBatteryToggle`,
  `SetBatteryChoice`, `SetKeyboardBrightness`, `SetAutostart`, `EvaluateClamshell` — последний
  только если `Clamshell` станет Application-контрактом) и 5 запросов (`CurrentProfile`,
  `SelectableProfiles`, `IsTurboOn`, `ReadSensors`, `ReadBatteryInfo`).
- **Чего это не даёт:** остальные 24 действия и 12 запросов — за стеной состояния (ниже). То есть
  Application наполняется **на десятую часть**, и цена — переделка 13 портов и сигнатур вью-моделей
  (`FansViewModel(fc.Capability)` → `FanInfo`; `LightingViewModel(rgb, backlight)` →
  дескриптор + делегаты записи; `GpuViewModel`/`CpuViewModel`/`CoViewModel` — на дескрипторы).
  Это **не переезд, а redesign**, и он не окупается десятой частью.

#### Форма 3 — оставить как есть и назвать пустоту правильной

Ничего не двигать; признать, что `Application` = план + контракты + константа, и что это —
следствие решений, а не промаха. Тогда `IDevice` всё равно удаляется (форма 1), потому что это
отдельное решение и оно дешёвое.

### 2.3. Две стены, из-за которых Application пуст (и третья, мелкая)

**Стена 1 — состояние.** `Settings` (и пять типов пресетов, `ProfileMemory`, `ModeKey`,
`ISettingsStore`) — Infrastructure по решению владельца. **24 из 29 действий и 12 из 17 запросов
трогают этот граф.** Use case, который пишет настройку, не может не назвать её форму.

**Стена 2 — порты.** Девять из четырнадцати портов, которые дёргают эти действия, —
Infrastructure по классификации самой карты (`IKeyboardBrightness`, `IHotkeys`, `IDisplayTint`,
`IGpuOverclock`, `ICpuPower`, `ICurveOptimizer`, `IDriverSetup`, `IAutostart`, `IClamshell`), и
классифицированы они тестом владельца: «моделирует ли домен технологию вендора?».

**Стена 3 — `CoAxis` и `OffsetCounts`.** Ось CO трогает `CoAxis` шесть раз, а он —
Infrastructure (`Infrastructure/Vendors/Generic/`, волна 6, решение владельца). Даже если обе
большие стены падут, CO останется: `CoAxis` уезжает только в Domain или в Application, а
`CoPreset` держит `Domain/CoAxis.cs`... то есть **CO заблокирован тремя решениями сразу**, и это
самая связанная ось в дереве.

**Пока стоят все три, use case в Application назовёт Infrastructure и покрасит
`ApplicationPointsAtDomainNotInfrastructure`.** Это не рассуждение: мутация с пятью `using`
записана в `docs/domain-layering-map.md` и покрасила ровно это правило.

**Четыре выхода, с ценой:**

| Выход | Что меняется | Цена |
|---|---|---|
| **(а) Контейнер → Application** | `Settings` и восемь типов формы переезжают в `Application/`; `JsonSettingsStore` остаётся Infrastructure и реализует объявленный Application `ISettingsStore` — стрелка честная | Application начинает знать форму хранения. JSON-схема не меняется (персистятся имена **свойств**), но глоссарий и `README` переписываются в десяти строках. **Плюс стена 3 остаётся** — `CoAxis` придётся решать отдельно. Это единственный выход, дающий **все** use cases в Application |
| **(б) Девять портов → Domain** | Порты переезжают, домен снова знает NvAPI, AVFS, GUID оверлея | Отменяет ровно то, против чего написано правило и что владелец утвердил |
| **(в) Сузить правило** | Разрешить `Application` → `Infrastructure.Composition` | Стрелка перестаёт быть правдой; в том же namespace `DeviceFactory`, называющий вендоров |
| **(г) Оставить** | Ничего | Пустота Application остаётся, и её надо называть решением, а не недоделкой |

**Моя рекомендация: (а), и только если владелец хочет настоящие use cases в Application.**
Она сохраняет половину его же решения: то, что `Settings` **объявляет опции конкретно этого
ноутбука**, — свойство модели, а не слоя, и оно сохраняется (набор по-прежнему приходит от
бэкенда в конструктор). Второй довод — форма хранения — тоже сохраняется частично: файл
по-прежнему принадлежит `JsonSettingsStore`, а граф становится состоянием приложения.
Одновременно это **единственный** выход, который не ломает ни один из пяти инвариантов §3.

### 2.4. Что `Application` содержит после каждой формы — поимённо

**Форма 1:** `AppArgs`, `IDynamicLighting`, `IDynamicLightingFactory`, `ReapplyTrigger`,
`ReapplyPlan` — **ровно сегодняшние пять, ни одним больше.**

**Форма 2:** те же пять + объявленные контракты-операции (≈9: запись офсетов GPU, андервольт,
power-overlay, яркость клавиатуры, синий фильтр, автозапуск, кламшелл, хоткеи, установка драйвера)
+ **10 use cases** (5 действий, 5 запросов из §2.2).

**Форма 3(а):** `Settings` и восемь типов формы + все шесть партиалов `LaptopService` +
`OptionsAssembler` + `HardwareReconciler`/`ReapplyOutcome` + `ModeKey` + сегодняшние пять = **все
29 действий и 17 запросов в Application**, `Infrastructure/Composition/` остаётся с
`JsonSettingsStore`, `DeviceFactory`, `LampArrayBridgeFactory`, `CoAxis`/`OffsetCounts` — то есть
с композицией, хранилищем и двумя вендорскими типами.

---

## 3. Инварианты: что задевает каждая форма

| Инвариант | Где записан | Форма 1 | Форма 2 | Форма 3(а) |
|---|---|---|---|---|
| **Световая дверь: `LightsForCurrentMode` ×2 и `EnsureLightZone` отдают живую ссылку в граф** | `docs/open-decisions.md` §4, `Assert.Same` в `LaptopServicePresetTests`/`LightsForCurrentModeTests` | Не тронут | Не тронут (состояние не двигается) | Не тронут: тип переезжает целиком, аксессоры остаются живыми. **Ломает** выход «нейтрализовать форму хранения в доменный тип» — его брать нельзя |
| **Три места под `_state`** (`ApplyCustom`, `ApplyModeCpuPower`, `CurrentModeKey()` в вызывающих) | `docs/open-decisions.md` §3 | Не тронут: тела не двигаются | Не тронут | Не тронут, **пока `_state` остаётся один**. Если use cases разрезать на классы по осям, замков станет несколько, и дедбенд `Step → ApplyFan → Commit` перестанет быть атомарным — это назвать до работы |
| **Глухие оси** (`ApplyFan`, `ApplyStoredMode` из опроса, `ApplyModeCo` — без читателя ошибки) | `LaptopService.cs` (доккомментарий `Attempt`) | Не тронут | Риск: каждая новая граница — соблазн завести канал | Не тронут |
| **`_state` → `Gate`** (порядок замков, обратного вызова нет) | `LaptopService.cs` (`_state`) | Не тронут | Не тронут | Не тронут, пока WMI-слой не зовёт обратно |
| **Никакого замка через блокирующий вызов** | `ApplyStartupState`, `SetGpuOc`, `SetCpuPower`, `SetCo*`, `ApplySetting` | Не тронут: порядок строк внутри тел | Не тронут | Не тронут |
| **Один `Settings` не отдаётся наружу** (держится грепом) | `docs/open-decisions.md` §4 | Не тронут | Не тронут | Не тронут |

**Ни одна из трёх форм не задевает ни один инвариант** — потому что все три двигают **типы**, а
не тела методов и не области замков. Единственная работа, которая их задевает, — разрезание
`LaptopService` на несколько классов, и её в этом предложении нет.

---

## 4. Цена (задача 3)

### 4.1. Форма 1 — измерено

- **Продакшн-файлов с `IDevice`: 13**, из них 4 — только в комментариях/`cref`
  (`Domain/Battery.cs`, `Domain/Rgb.cs`, `LaptopService.Toggles.cs`, `OverlayCpuPower.Windows.cs`).
  Реально правятся 9: `Domain/Ports.cs` (удаление), `GenericDevice.cs`, `AcerDevice.cs`?,
  `DellDevice.cs`?, `DeviceFactory.cs`, `LaptopService.cs`, `App.axaml.cs`, `AppController.cs`,
  `TrayController.cs`, `MainViewModel.cs`, `OptionsViewModel.cs`, `LightingCoordinator.cs` — 12
  файлов, из них 8 — смена имени типа и `using`.
- **Тестовых файлов с `IDevice`: 4** (`Fakes/FakeDevice.cs`, `BatteryTests`, `DeclaredSettingTests`,
  `RgbDeviceTests`; в трёх — `cref`). `FakeDevice` переписывается; **40 сайтов
  `f.Device.<Port> = …` в 19 файлах не меняются** — при условии, что члены новой модели
  устанавливаемые.
- **Контракты:** `IDevice` (удаляется), `DeviceFactory.Create` (тип возврата), `FakeDevice`
  (перестаёт быть реализацией).
- **Персистентные имена: ноль.** JSON хранит имена **свойств** `Settings`, не имена типов;
  `ModeKey.None` = `"default"` не трогается. Оба стража схемы
  (`AFullSettingsFileFromAShippedVersionStillLoads`, `EveryDeclaredPropertyStillReachesTheDisk`)
  остаются зелёными без правок.
- **Глоссарий:** строка `IDevice` в `docs/context-map.md` **обязана** уйти —
  `GlossaryTests.EveryTypeTheGlossaryNamesStillExists` покраснеет на неразрешимом имени (в
  глоссарии 28 типовых строк, порог невокзальности 15 — удаление одной строки его не задевает). Плюс
  `README.md` (раздел `Domain/`, где `IDevice` назван агрегатом, и раздел
  `Infrastructure/Composition/`, где сказано «assembles an IDevice») и `docs/domain-layering-map.md`
  §3.1.
- **Ни один тест не меняет утверждения.** Меняются `using` и тип в объявлениях.

### 4.2. Форма 2 — оценка

13 портов расщепляются (дескриптор + операция), 9 операций становятся контрактами Application,
вью-модели получают дескрипторы: `GpuViewModel`, `CpuViewModel`, `CoViewModel`, `FansViewModel`,
`LightingViewModel`, `MainViewModel`, `TrayController`, `OptionsViewModel`, `OptionsAssembler`.
**Тесты меняют утверждения**: `OptionsPrimeTests`, `OptionsRowClickTests`, `LightingDrawerTests`
строят вью-модели от `FakeDevice` — сигнатуры фабрик изменятся, и это уже не `using`.

### 4.3. Форма 3(а) — оценка

Переезжают: `Settings.cs` (306 строк, включая пять типов пресетов и `ISettingsStore`), шесть
партиалов `LaptopService` (1132 строки), `OptionsAssembler.cs`, `HardwareReconciler.cs`,
`ModeKey.cs` — **10 файлов**. Остаются в Infrastructure: `JsonSettingsStore`, `DeviceFactory`,
`LampArrayBridgeFactory`, `CoAxis`, `OffsetCounts`. Глоссарий: колонка слоя у 10 строк. README:
разделы `Application/` и `Infrastructure/Composition/`. **Стена 3 не снята**: `CoAxis` придётся
решать четвёртым решением.

---

## 5. Что в решениях владельца расходится с кодом — прямо

1. **«Само название LaptopService говорит, что ему в application не место».** Содержимое класса
   говорит обратное: 46 из 62 публичных членов — use cases (действия и запросы), а не транспорт.
   Довод о названии кодом не подтверждается. **Но вывод от этого не меняется** — размещение
   вынуждается стенами 1–3, а не названием. Это надо назвать прямо: решение было верным по
   результату и неверным по рассуждению.
2. **«В application должны быть use cases» + «Settings — Infrastructure» + «девять портов
   моделируют вендора» — эти три решения несовместны**, пока стоит
   `ApplicationPointsAtDomainNotInfrastructure`. Совместны они ровно при выходе (а) или (б) из
   §2.3. Сегодняшняя форма — это выбор «оставить стену», и он не назван выбором.
3. **Комментарий `Domain/Ports.cs` в шапке файла:** «Infrastructure implements them; the
   Application/UI depend only on these» — про Application неверно: Application не называет ни
   одного порта, кроме `IRgbDevice`/`RgbZone` (в `DynamicLighting.cs`). Application зависит от
   **двух** портов из пятнадцати.
4. **`README.md`: «UI/ — binds to Application and Domain, never to a vendor»** — неверно
   сегодня: `UI/ViewModels/UiActions.cs` импортирует `AcerHelper.Infrastructure.Composition` ради
   `FanPreset`/`GpuOcPreset`, и пять файлов UI называют `LaptopService`. UI смотрит в
   Infrastructure по делу, но README это отрицает.
5. **Асимметрия видимости.** `LaptopService.Settings` — `internal` с доккомментарием, который
   называет это «вывеской, а не забором»; `LaptopService.Device` — `public IDevice`, и через него
   UI получает наружу **живые транспорты** (`d.Lighting`, `d.Clamshell`). Одна и та же забота о
   границе решена двумя противоположными способами в одном файле.
6. **Глоссарий ставит `IDevice` в Domain**, а карта §3.1 сама признаёт, что домен через него не
   рассуждает. Строка устарела при любом исходе — и при удалении, и при сохранении.

---

## 6. Что предлагается решить

1. **Удалять ли `IDevice` — формой 1 или формой 2** (или не удалять, но тогда решение владельца
   отменяется). Форма 1 дешева и безопасна; форма 2 отвечает на обе правки, но это redesign.
2. **Если Application должен держать use cases — какой выход берётся:** (а) контейнер →
   Application, (б) девять портов → Domain, (в) сузить правило, (г) признать форму 3.
   Рекомендация — (а), с отдельным решением по `CoAxis` (стена 3).
3. **Разрезать ли `LaptopService` на классы.** Рекомендация — **нет**: это единственная работа,
   которая ломает инварианты §3 (один `_state`, дедбенд под ним), и она не требуется ни одной из
   форм.

До решения владельца по пункту 1 и 2 **не двигается ничего**.
