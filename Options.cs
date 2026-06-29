namespace AcerHelper;

/// <summary>A hardware on/off option (checkbox) shown in the Options group.
/// If <paramref name="Confirm"/> is set, it is asked before turning the option ON;
/// returning false reverts the checkbox.</summary>
public sealed record OptionToggle(string Label, bool Supported, bool Initial, Action<bool> OnChange, Func<bool>? Confirm = null);

/// <summary>A hardware multi-choice option (dropdown) shown in the Options group.</summary>
public sealed record OptionChoice(string Label, bool Supported, IReadOnlyList<string> Options, int InitialIndex, Action<int> OnChange);
