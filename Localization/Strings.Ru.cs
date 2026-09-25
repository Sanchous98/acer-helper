namespace AcerHelper.Localization;

/// <summary>Compiled-in translation tables (see <see cref="Loc"/>). Keyed by the English source text.
/// Keep a key here byte-for-byte identical to the English at its call site — a mismatch just shows English,
/// it never throws. Format strings keep their <c>{0}</c>… placeholders.</summary>
internal static partial class Strings
{
    public static readonly IReadOnlyDictionary<string, string> Ru = new Dictionary<string, string>
    {
        // ---- navigation / buttons / dialogs ----
        ["Options"]                 = "Параметры",
        ["Lighting"]                = "Подсветка",
        ["Lighting…"]               = "Подсветка…",
        ["Grant hardware access…"]  = "Предоставить доступ к оборудованию…",
        ["Show"]                    = "Показать",
        ["Exit"]                    = "Выход",
        ["Cancel"]                  = "Отмена",
        ["Done"]                    = "Готово",
        ["Start"]                   = "Начать",

        // ---- notifications (the bell's drop-down) ----
        // The bell hides the messages that used to be banners, so the count on it and the words that open and
        // dismiss the list are the whole of how the user finds them.
        ["Notifications"]           = "Уведомления",
        ["No notifications."]       = "Уведомлений нет.",
        ["Ignore"]                  = "Проигнорировать",

        // ---- section headers ----
        ["PERFORMANCE"]             = "ПРОИЗВОДИТЕЛЬНОСТЬ",
        ["FANS"]                    = "ВЕНТИЛЯТОРЫ",
        ["BATTERY"]                 = "АККУМУЛЯТОР",

        // ---- performance profiles ----
        // Profile names render in the equal-width segment row (4-5 segments on the 468px flyout, ~10
        // chars per line); Avalonia wraps but can't hyphenate, so a single long word breaks mid-word.
        // Keep these one short word each (G-Helper's Russian uses the same convention).
        ["Eco"]                     = "Эко",
        ["Quiet"]                   = "Тихий",
        ["Balanced"]                = "Баланс",
        ["Performance"]             = "Мощный",
        ["Turbo"]                   = "Турбо",
        ["Turbo key toggles Turbo"] = "Клавиша Turbo включает Турбо",
        ["Otherwise the Turbo key cycles through profiles."] = "Иначе клавиша Turbo переключает профили по кругу.",
        ["Profile: {0}"]            = "Профиль: {0}",
        ["Failed to set {0}"]       = "Не удалось включить {0}",
        ["Turbo failed"]            = "Не удалось включить Турбо",

        // ---- fans ----
        ["Auto"]                    = "Авто",
        ["Max"]                     = "Макс.",
        ["Custom"]                  = "Пользовательский",
        ["Curve"]                   = "Кривая",
        ["Follow curve"]            = "Использовать кривую",
        ["CPU fan curve"]           = "Кривая вентилятора CPU",
        ["GPU fan curve"]           = "Кривая вентилятора GPU",
        ["{0} rpm"]                 = "{0} об/мин",

        // ---- overclocking & power tab (GPU clocks + CPU power + CPU undervolt) ----
        // "GPU" and "CPU" stay as-is (universal acronyms) -> no entries, fall back to the English keys.
        ["Overclocking and Power"]  = "Разгон и питание",
        ["Core"]                    = "Ядро",
        ["Memory"]                  = "Память",
        ["Reset"]                   = "Сброс",
        ["GPU overclock failed"]    = "Не удалось разогнать GPU",
        // CPU power mode (Windows power-mode overlay). "Balanced" and "Best performance" reuse the existing
        // keys above ("Баланс" / "Макс. мощность").
        ["Power mode"]              = "Режим питания",
        ["Best power efficiency"]   = "Энергоэффективность",
        ["Power mode failed"]       = "Не удалось изменить режим питания",
        // CPU undervolt (all-core Curve Optimizer). The value is a bare AVFS step count, so it carries no unit
        // string. "Reset" above is reused for the back-to-stock button.
        ["Undervolt"]               = "Андервольт",
        ["CPU undervolt failed"]    = "Не удалось изменить андервольт CPU",
        // Third-party driver offer. "{0}" is the driver name and "{1}" the feature — both substituted, so the
        // placeholders must survive translation. "CPU undervolt" doubles as the feature name in the prompt.
        ["CPU undervolt"]           = "андервольт CPU",
        ["Install {0}?"]           = "Установить {0}?",
        ["Install"]                = "Установить",
        ["{0} needs the {1} kernel driver from {2}. It is third-party software, shared with other tuning tools, and Acer Helper will never update or remove it. You can install it yourself instead."]
            = "Для «{0}» нужен драйвер ядра {1} с {2}. Это сторонняя программа, её используют и другие утилиты настройки, и Acer Helper никогда не будет её обновлять или удалять. Можно установить и вручную.",
        ["{0} installed — restart Acer Helper to use {1}"]
            = "{0} установлен — перезапустите Acer Helper, чтобы использовать {1}",
        ["Lower voltage at the same clocks, in AVFS steps. Too much is unstable — test for hours, and reset if anything crashes."]
            = "Меньше напряжения на тех же частотах, шагами AVFS. Перебор ведёт к нестабильности — проверяйте часами, при сбоях сбросьте.",

        // ---- battery ----
        ["Health"]                  = "Состояние",
        ["Charge cycles"]           = "Циклы заряда",
        ["Charge mode"]             = "Режим зарядки",
        ["Charge limit (~80%)"]     = "Ограничение заряда (~80%)",
        ["Calibration (full cycle)"] = "Калибровка (полный цикл)",
        ["Charging"]                = "Зарядка",
        ["On battery"]              = "От батареи",
        ["Plugged in"]              = "Подключено",
        // Live power row: the label flips with the sign of the reading (draw vs charge), and the unit follows the
        // number so Russian gets "Вт" and a comma decimal through the format specifier.
        ["Power draw"]              = "Потребление",
        ["Charging power"]          = "Мощность зарядки",
        ["{0:0.0} W"]               = "{0:0.0} Вт",
        ["Express charge"]          = "Быстрая зарядка",
        ["Standard"]                = "Стандартный",
        ["Adaptive"]                = "Адаптивный",
        ["Start battery calibration?"] = "Запустить калибровку аккумулятора?",
        ["This runs a full charge then a full discharge cycle and can take several hours. Keep the "
            + "laptop plugged in and don't depend on it meanwhile. Turn the switch back off to stop."]
            = "Будет выполнен полный цикл: сначала полная зарядка, затем полная разрядка — это может занять "
            + "несколько часов. Держите ноутбук подключённым к сети и не рассчитывайте на него в это время. "
            + "Чтобы остановить, выключите переключатель обратно.",

        // ---- hardware options ----
        ["LCD overdrive"]                = "Разгон матрицы",
        ["Keyboard backlight timeout"]   = "Тайм-аут подсветки клавиатуры",
        ["Keyboard backlight timeout:"]  = "Тайм-аут подсветки клавиатуры:",
        ["Backlight timeout"]            = "Тайм-аут подсветки",
        ["Fn lock"]                      = "Блокировка Fn",
        ["USB charging when off:"]       = "Зарядка по USB в выключенном состоянии:",
        ["USB charging"]                 = "Зарядка по USB",
        ["Battery limit"]                = "Ограничение заряда",
        ["Battery calibration"]          = "Калибровка аккумулятора",
        ["Blue-light filter:"]           = "Фильтр синего света:",
        ["Profile on AC power:"]         = "Профиль в сети:",
        ["Profile on battery:"]          = "Профиль на батарее:",
        ["Power-source profile"]         = "Профиль по источнику питания",
        ["{0} failed"]                   = "Ошибка: {0}",

        // ---- generic level / state labels (blue-light, USB, plain backlight) ----
        ["Off"]                     = "Выкл.",
        ["On"]                      = "Вкл.",
        ["Low"]                     = "Низкий",
        ["Medium"]                  = "Средний",
        ["High"]                    = "Высокий",
        ["Long-use"]                = "Долгое использование",
        ["Dim"]                     = "Тускло",
        ["Bright"]                  = "Ярко",

        // ---- lighting ----
        ["Brightness"]              = "Яркость",
        ["Speed"]                   = "Скорость",
        ["Direction"]               = "Направление",
        ["Colour"]                  = "Цвет",
        ["Normal"]                  = "Обычное",
        ["Reversed"]                = "Обратное",
        ["Lightbar follows performance profile"] = "Световая панель следует за профилем производительности",
        ["Zone {0}"]                = "Зона {0}",
        ["Keyboard"]                = "Клавиатура",
        ["Lightbar"]                = "Световая панель",
        // effect names
        ["Static"]                  = "Статичный",
        ["Breathing"]               = "Дыхание",
        ["Neon"]                    = "Неон",
        ["Wave"]                    = "Волна",
        ["Shifting"]                = "Сдвиг",
        ["Zoom"]                    = "Зум",
        ["Meteor"]                  = "Метеор",
        ["Twinkling"]               = "Мерцание",

        // ---- clamshell / autostart ----
        ["Stay awake when lid closed (docked, on AC)"] = "Не засыпать при закрытой крышке (в доке, от сети)",
        ["Start with Windows"]      = "Запускать вместе с Windows",
        ["Start at login"]          = "Запускать при входе",

        // ---- updates / access ----
        ["Update available: v{0}"]  = "Доступно обновление: v{0}",
        // The expanded update notice: the heading over the release notes. "Установить" for the Install button
        // reuses the existing ["Install"] row further up, so it is deliberately not repeated here.
        ["Changelog"]               = "Что нового",
        ["Downloading update…"]     = "Загрузка обновления…",
        ["Installing update…"]      = "Установка обновления…",
        // A portable Windows run cannot upgrade in place, so it downloads the MSI and opens the Windows Installer
        // (with its own UAC prompt) rather than the release page — see WindowsUpdater.LaunchInstaller.
        ["Launching installer…"]    = "Запуск установщика…",
        ["Update failed"]           = "Не удалось обновить",
        ["Hardware access granted — restart to use the unlocked controls."]
            = "Доступ к оборудованию предоставлен — перезапустите приложение, чтобы использовать разблокированные функции.",
        // The files were installed but acer_wmi could not be reloaded, so the module parameters are not live: the
        // app's own restart cannot load a module, and only a reboot can. Deliberately not the string above.
        ["Hardware access granted — the acer-wmi driver could not be reloaded, so the new module settings take effect after a reboot."]
            = "Доступ к оборудованию предоставлен — драйвер acer-wmi не удалось перезагрузить, поэтому новые параметры модуля вступят в силу после перезагрузки системы.",
        // The same case, in the NOTIFICATION behind the bell: that list is the only surface that survives the
        // refresh loop, so the reboot order lives here rather than in a status line that the next tick replaces
        // (see MainViewModel.SetHardwareAccessRebootPending).
        ["Restart your computer to finish enabling the unlocked controls (click to retry)."]
            = "Перезагрузите компьютер, чтобы включить разблокированные функции (нажмите, чтобы повторить попытку).",
        ["Grant access failed"]     = "Не удалось предоставить доступ",
        // ---- the informed-consent prompt (HardwareAccessConsent) ----
        // The frame around the prompt's rows: the heading and confirm button (the latter reuses "Install" from
        // the driver prompt above), the sentence that explains the pkexec request, the honest limits, and what
        // to do afterwards. The rows themselves are the INSTALLER's descriptions, listed below.
        ["Install hardware access?"]
            = "Установить доступ к оборудованию?",
        ["Acer Helper asks for administrator rights once (pkexec) to copy the following into /etc:"]
            = "Acer Helper один раз запросит права администратора (pkexec), чтобы скопировать в /etc следующее:",
        ["The files come from this app's own copy — nothing is downloaded, no other permission is added, and cancelling changes nothing."]
            = "Файлы берутся из собственной копии приложения — ничего не скачивается, никакие другие разрешения не добавляются, а отмена ничего не меняет.",
        ["Restart Acer Helper afterwards to use the new controls."]
            = "После этого перезапустите Acer Helper, чтобы воспользоваться новыми функциями.",
        // THE INSTALLER'S OWN DESCRIPTIONS (Infrastructure/HardwareAccess.cs, the `Files` table) — one key per
        // bundled file, byte-for-byte identical to the English there because the English text IS the lookup key.
        // "{0}" is the group the permissions are handed to (HardwareAccess.AccessGroup), substituted at render
        // time; the acer-wmi options entry grants a module parameter rather than a file permission and so has
        // none. The last entry arrived with the systemd unit that re-applies the SMU grant at every boot, and it
        // needs its key here for the same reason the others do: HardwareAccessConsentTests fails on a description
        // with no row in this table, so an untranslated permission cannot ship silently.
        ["Write access for the {0} group to the nodes the app drives: the performance/thermal profile, the keyboard-backlight brightness and timeout, the Acer fan-speed controls, the battery charge mode and thresholds, and the Dell BIOS attributes — plus session access to the two Acer HID interfaces (RGB keyboard/lightbar and the power-envelope channel) and to the keyboard that carries the Nitro key. It also covers the CPU's SMU mailbox attributes in /sys/kernel/ryzen_smu_drv (smu_args, mp1_smu_cmd, rsmu_cmd and smn), which is how the Curve Optimizer undervolt reaches the processor without the app ever running as root."]
            = "Доступ на запись для группы {0} к узлам, которыми управляет приложение: профиль производительности/охлаждения, яркость и тайм-аут подсветки клавиатуры, управление оборотами вентиляторов Acer, режим и пороги зарядки батареи, атрибуты BIOS Dell — плюс доступ в рамках сеанса к двум HID-интерфейсам Acer (RGB-клавиатура и световая полоса, канал конверта мощности) и к клавиатуре, несущей клавишу Nitro. Он же распространяется на атрибуты почтовых ящиков SMU процессора в /sys/kernel/ryzen_smu_drv (smu_args, mp1_smu_cmd, rsmu_cmd и smn) — именно так андервольт Curve Optimizer доходит до процессора, и приложению для этого не нужен root.",
        ["Write access for the {0} group to the CPU's SMU mailbox attributes in /sys/kernel/ryzen_smu_drv (smu_args, mp1_smu_cmd, rsmu_cmd and smn), re-applied at every boot by a systemd unit: the udev rule above can only grant them when the driver's bind event reaches a running udev, which is not what happens on a machine whose ryzen_smu module is loaded from the initramfs. This is what keeps the CPU undervolt working after a restart."]
            = "Доступ на запись для группы {0} к атрибутам почтового ящика SMU процессора в /sys/kernel/ryzen_smu_drv (smu_args, mp1_smu_cmd, rsmu_cmd и smn), заново выдаваемый при каждой загрузке юнитом systemd: правило udev выше может выдать их, только когда событие bind драйвера доходит до работающего udev, а на машине, где модуль ryzen_smu загружается из initramfs, этого не происходит. Именно это сохраняет андервольт CPU рабочим после перезагрузки.",
        ["The same file permissions re-applied at every boot by systemd-tmpfiles, for the nodes that exist at boot: the legacy platform-profile alias, the battery thresholds, the keyboard backlight and the Dell BIOS attributes."]
            = "Те же права на файлы, применяемые при каждой загрузке через systemd-tmpfiles, для узлов, которые существуют на момент загрузки: устаревший псевдоним профиля платформы, пороги батареи, подсветка клавиатуры и атрибуты BIOS Dell.",
        ["An options line for the acer-wmi kernel driver: it is loaded with predator_v4=1 and force_caps=7200, which is what enables the five Acer performance profiles, the fan telemetry and the fan-speed control (PWM) on a model the driver's own device table does not know. This changes how the driver behaves at load rather than granting a file permission, and if the module is in use it takes effect only after you restart the computer."]
            = "Строка options для драйвера ядра acer-wmi: он загружается с predator_v4=1 и force_caps=7200, и именно это включает пять профилей производительности Acer, телеметрию вентиляторов и управление их оборотами (PWM) на модели, которой нет в собственной таблице устройств драйвера. Это меняет поведение драйвера при загрузке, а не выдаёт права на файл, и если модуль используется, изменение вступит в силу только после перезагрузки компьютера.",

        // ---- discrete-GPU access (cardwire) ----
        // The Options row and the informed-consent prompt behind it (UI/CardwireGpuAccessConsent.cs). Every
        // sentence here is a whole paragraph of the prompt or the frame around a failure, so the placeholders —
        // there are none — stay none. "cardwire" and "D-Bus" keep their spelling: one is the daemon's own name
        // and the other is the transport the prompt names.
        ["Use the discrete GPU"]    = "Использовать дискретную видеокарту",
        ["GPU access failed"]       = "Не удалось получить доступ к GPU",
        ["Let Acer Helper use the discrete GPU?"]
            = "Разрешить Acer Helper использовать дискретную видеокарту?",
        ["Allow"]                   = "Разрешить",
        ["Acer Helper will ask cardwire — the daemon that hides the discrete GPU from programs it has not allowed to use it — to let THIS process see that GPU. Nothing is written to disk and no file permission changes: the request is a D-Bus call, made while the app is running."]
            = "Acer Helper попросит cardwire — службу, которая скрывает дискретную видеокарту от программ, которым это не разрешено, — показать её ЭТОМУ процессу. Ничего не записывается на диск и права на файлы не меняются: запрос идёт по D-Bus во время работы приложения.",
        ["The grant belongs to this one process and disappears the moment Acer Helper exits. cardwire has no way to take it back, so it cannot be revoked while the app runs — turning this option off stops the next start, not this one, and quitting the app is what ends it."]
            = "Разрешение выдаётся только этому процессу и исчезает, как только Acer Helper завершится. В cardwire нет способа его отозвать, поэтому во время работы приложения его отменить нельзя — выключение этой опции остановит следующий запуск, но не текущий; уже выданное разрешение прекращает только завершение приложения.",
        ["Every other application stays blocked. While this option stays on, Acer Helper asks again on each start."]
            = "Все остальные приложения остаются заблокированными. Пока эта опция включена, Acer Helper спрашивает заново при каждом запуске.",

        // ---- device status messages ----
        ["Acer WMI unavailable — run as administrator."]
            = "Acer WMI недоступен — запустите от имени администратора.",
        ["Linux: profiles, fans and temperatures come from the mainline acer-wmi driver (the installer's module parameters enable them) — it has no interface for LCD overdrive, the battery limiter/calibration, the keyboard-backlight timeout, USB charging or the keyboard-brightness read-back, so those stay unavailable."]
            = "Linux: профили, вентиляторы и температуры даёт штатный драйвер acer-wmi (включается параметрами модуля от установщика) — интерфейса для LCD overdrive, лимитера и калибровки батареи, таймаута подсветки, USB-зарядки и чтения яркости подсветки в нём нет, поэтому эти функции недоступны.",
        ["Linux: the five Acer profiles, fan control and the Acer temperature chip need the installer's module parameters — until they are in place the profile set is the generic one; LCD overdrive, the battery limiter/calibration, the keyboard-backlight timeout, USB charging and the keyboard-brightness read-back have no mainline interface at all."]
            = "Linux: пять профилей Acer, управление вентиляторами и чип температур Acer требуют параметров модуля от установщика — пока их нет, набор профилей берётся из generic-источника; интерфейса для LCD overdrive, лимитера и калибровки батареи, таймаута подсветки, USB-зарядки и чтения яркости подсветки в mainline нет вообще.",
        ["The fans are read-only — fan control needs the hardware-access grant and the driver's PWM interface; grant access from the app and restart."]
            = "Вентиляторы доступны только для чтения — для управления нужен доступ к оборудованию и интерфейс PWM драйвера; предоставьте доступ в приложении и перезапустите его.",
        ["Dell BIOS controls are locked by a BIOS admin password and were hidden."]
            = "Элементы управления Dell BIOS заблокированы паролем администратора BIOS и скрыты.",
        ["Some Dell controls are locked by the firmware (BIOS admin password, or the model rejects writes) and were hidden."]
            = "Некоторые элементы управления Dell заблокированы прошивкой (пароль администратора BIOS или модель отклоняет запись) и скрыты.",
        ["No power-profile interface found — limited controls."]
            = "Интерфейс профилей питания не найден — ограниченный набор функций.",

        // ---- other backends: generic OS power profiles (non-Acer) + Dell modes/durations ----
        // Same segment-row constraint as the profile names above: single words stay short, two-word
        // names are fine (they wrap at the space; MinHeight keeps the row even).
        ["Best efficiency"]         = "Экономичный",
        ["Best performance"]        = "Макс. мощность",
        ["Power saver"]             = "Энергосбережение",
        ["Low power"]               = "Мин. мощность",
        ["Balanced performance"]    = "Баланс+",
        ["Cool"]                    = "Прохладный",
        ["Optimized"]               = "Оптимальный",
        ["Primarily AC use"]        = "Преимущественно от сети",
        ["5 s"]                     = "5 с",
        ["10 s"]                    = "10 с",
        ["30 s"]                    = "30 с",
        ["1 min"]                   = "1 мин",
        ["5 min"]                   = "5 мин",
        ["15 min"]                  = "15 мин",
        ["1 h"]                     = "1 ч",

        // ---- ASUS / asusd: EC-risky writes, validation refusals and consent warnings (Phase 3) ----
        ["This setting is read-only on this machine."]
            = "Этот параметр доступен только для чтения.",
        ["This machine does not expose that setting."]
            = "Устройство не предоставляет этот параметр.",
        ["This machine does not report a range for that setting, so the app will not write it."]
            = "Устройство не сообщает диапазон для этого параметра, поэтому приложение не будет его записывать.",
        ["That value is outside the range this machine reports."]
            = "Значение выходит за диапазон, сообщённый устройством.",
        ["That value is not a multiple of the machine's step size."]
            = "Значение не кратно шагу, сообщённому устройством.",
        ["That value is not one of the values this machine accepts."]
            = "Значение не входит в список значений, принимаемых устройством.",
        ["The app does not know how this machine treats that setting, so it will not write it."]
            = "Приложение не знает, как устройство обрабатывает этот параметр, поэтому оно не будет его записывать.",
        ["Change cancelled."]
            = "Изменение отменено.",
        ["The change is queued and will be applied on the next restart."]
            = "Изменение поставлено в очередь и применится при следующей перезагрузке.",
        ["Changing the GPU mode is queued and only takes effect on the next restart. A wrong value can leave the screen black — if that happens, force a shutdown by holding the power button, then start the machine and change the mode back from a console or another display. See docs/asus-support.md."]
            = "Смена режима GPU ставится в очередь и вступит в силу только при следующей перезагрузке. Неверное значение может оставить экран чёрным — если это произошло, принудительно выключите машину удержанием кнопки питания, затем включите её и смените режим обратно из консоли или с другого дисплея. См. docs/asus-support.md.",
        ["This writes a power limit to the firmware. It takes effect only while this profile's custom tuning is enabled in asusd."]
            = "Запись лимита мощности в прошивку. Действует только пока для этого профиля включена пользовательская настройка в asusd.",
        ["This writes a value to the firmware."]
            = "Запись значения в прошивку.",
        ["This writes a fan curve to the firmware and replaces the current curve for this profile. It takes effect immediately."]
            = "Запись кривой вентилятора в прошивку; текущая кривая этого профиля будет заменена. Действует сразу.",
        ["This resets this profile's fan curves to the machine's firmware defaults."]
            = "Сброс кривых вентилятора этого профиля к заводским значениям прошивки.",
        ["That profile is not one of the machine's performance profiles."]
            = "Этот профиль не входит в набор профилей производительности устройства.",
        ["This fan is not one of the machine's fans."]
            = "Этот вентилятор не относится к вентиляторам устройства.",
        ["A fan curve must have exactly eight points."]
            = "Кривая вентилятора должна содержать ровно восемь точек.",
        ["A fan curve's temperatures must not decrease."]
            = "Температуры в кривой вентилятора не должны убывать.",
        ["A fan curve's fan speeds must be between 0% and 100%."]
            = "Скорости вентилятора в кривой должны быть в диапазоне от 0 до 100 %.",

        // ---- GPU mode (MUX): shared by the ASUS asusd/ATK paths and the recovered Acer gaming-WMI path ----
        ["GPU mode"]                = "Режим GPU",
        ["Change"]                  = "Изменить",
        ["Change the GPU mode?"]    = "Изменить режим GPU?",
        ["GPU mode: {0}"]           = "Режим GPU: {0}",
        ["GPU mode: unknown"]       = "Режим GPU: неизвестен",
        // A real queued change marks the value with a trailing '*' (see GpuMuxViewModel); this short note next to
        // the switcher explains the mark while the value differs from the original.
        ["* Will be applied after a reboot."]
            = "* Будет применено после перезагрузки.",
        ["GPU mode changed."]       = "Режим GPU изменён.",
        ["GPU mode change failed."] = "Не удалось изменить режим GPU.",
        ["This is already the current GPU mode."]
            = "Это уже текущий режим.",
        ["Hybrid"]                  = "Гибридный",
        ["Discrete"]                = "Дискретный",
        ["Auto (DDS)"]              = "Авто (DDS)",
        ["This machine's GPU mode cannot be switched from the app: the vendor's MUX/GPU-mode interface is not publicly documented, and writing an unknown value can leave the screen black. Use the vendor tool (NitroSense/PredatorSense) or the BIOS instead."]
            = "Режим GPU на этом устройстве нельзя переключить из приложения: интерфейс MUX/режима GPU производителя публично не документирован, а запись неизвестного значения может оставить экран чёрным. Используйте фирменную утилиту (NitroSense/PredatorSense) или BIOS.",
        ["That is not one of the GPU modes this machine offers."]
            = "Это не один из режимов GPU, доступных на этом устройстве.",
        ["Panel overdrive"]         = "Разгон панели",
        ["This machine does not expose a fan curve for that fan."]
            = "Устройство не предоставляет кривую вентилятора для этого вентилятора.",

        // ---- language selector ----
        ["Language"]                = "Язык",
        ["System"]                  = "Системный",
    };
}
