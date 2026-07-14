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

        // ---- battery ----
        ["Health"]                  = "Состояние",
        ["Charge cycles"]           = "Циклы заряда",
        ["Charge mode"]             = "Режим зарядки",
        ["Charge limit (~80%)"]     = "Ограничение заряда (~80%)",
        ["Calibration (full cycle)"] = "Калибровка (полный цикл)",
        ["Charging"]                = "Зарядка",
        ["On battery"]              = "От батареи",
        ["Plugged in"]              = "Подключено",
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
        ["Downloading update…"]     = "Загрузка обновления…",
        ["Installing update…"]      = "Установка обновления…",
        ["Update failed"]           = "Не удалось обновить",
        ["Hardware access granted — restart to use the unlocked controls."]
            = "Доступ к оборудованию предоставлен — перезапустите приложение, чтобы использовать разблокированные функции.",
        ["Grant access failed"]     = "Не удалось предоставить доступ",

        // ---- device status messages ----
        ["Acer WMI unavailable — run as administrator."]
            = "Acer WMI недоступен — запустите от имени администратора.",
        ["Linuwu-Sense module not loaded — install/load it for Acer controls."]
            = "Модуль Linuwu-Sense не загружен — установите/загрузите его для управления функциями Acer.",
        ["Linuwu-Sense is loaded but its files aren't accessible — add your user to the module's group (or install the udev rule) and log in again."]
            = "Linuwu-Sense загружен, но его файлы недоступны — добавьте пользователя в группу модуля (или установите правило udev) и войдите снова.",
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

        // ---- language selector ----
        ["Language"]                = "Язык",
        ["System"]                  = "Системный",
    };
}
