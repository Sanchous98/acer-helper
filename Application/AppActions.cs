namespace AcerHelper.Application;

/// <summary>The moved ACTION use cases the UI invokes, grouped into ONE record so the controller and the options
/// assembler take the set as a whole rather than as a dozen more positional constructor arguments.
///
/// WHY A BUNDLE RATHER THAN INDIVIDUAL PARAMETERS. The existing applied-edit use cases are handed to the
/// controller one by one, and that is right for six of them: each is the one action of its section. This family is
/// different — it is the app-level shell (Turbo and the hotkey, preferences, the battery rows, the refresh pass's
/// two ticks) and it is a dozen members. Spreading them across the controller's constructor would make that
/// constructor a positional list long enough to transpose by accident; grouping them here keeps the constructor
/// short and lets the container build the set once. The type itself names nothing infrastructure-shaped: every
/// member is one of the use cases declared in this layer.
///
/// WHO TAKES IT. <c>AppController</c> (whose delegates and refresh pass call the members) and
/// <c>OptionsAssembler</c> (whose battery and power-source rows are the other callers). Both are UI; the record
/// could have lived there, but every type it holds is Application's, so it sits here beside them.</summary>
public sealed record AppActions(
    ApplyStartupState Startup,
    SetTurbo Turbo,
    TogglePerformance TogglePerformance,
    SetTurboToggles TurboToggles,
    SetClamshell Clamshell,
    SetAutostart Autostart,
    SetLanguage Language,
    SetKeyboardBrightness KeyboardBrightness,
    EvaluateClamshell EvaluateClamshell,
    SyncPowerSource SyncPowerSource,
    ApplyCustom ApplyCustom,
    SetSourceProfile SourceProfile,
    SetBatteryToggle BatteryToggle,
    SetBatteryChoice BatteryChoice,
    SetBlueLight BlueLight);
