namespace AcerHelper;

/// <summary>A hardware on/off option (checkbox) shown in the Options group.
/// If <paramref name="Confirm"/> is set, it is asked synchronously before turning the option ON;
/// returning false reverts the checkbox. <paramref name="ConfirmAsync"/> is the async equivalent
/// (e.g. a modal dialog): the switch shows ON immediately, then reverts if it resolves to false.
/// Only one of the two should be set.</summary>
public sealed record OptionToggle(string Label, bool Supported, bool Initial, Action<bool> OnChange,
                                  Func<bool>? Confirm = null, Func<Task<bool>>? ConfirmAsync = null);

/// <summary>A hardware multi-choice option (dropdown) shown in the Options group.</summary>
public sealed record OptionChoice(string Label, bool Supported, IReadOnlyList<string> Options, int InitialIndex, Action<int> OnChange);
