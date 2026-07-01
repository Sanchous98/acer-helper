namespace AcerHelper;

/// <summary>A hardware on/off option (checkbox) shown in the Options group.
/// If <paramref name="Confirm"/> is set, it is asked synchronously before turning the option ON;
/// returning false reverts the checkbox. <paramref name="ConfirmAsync"/> is the async equivalent
/// (e.g. a modal dialog): the switch shows ON immediately, then reverts if it resolves to false.
/// Only one of the two should be set.
/// <paramref name="Read"/>, when supplied, reads the option's CURRENT hardware state. The row uses it to
/// verify a write: after <paramref name="OnChange"/> it re-reads and snaps the switch to the real value, so
/// a WMI write that silently didn't take corrects itself instead of leaving the switch lying. Options whose
/// write can't be read back (or don't touch hardware) leave it null and apply inline on the UI thread.</summary>
public sealed record OptionToggle(string Label, bool Supported, bool Initial, Action<bool> OnChange,
                                  Func<bool>? Read = null,
                                  Func<bool>? Confirm = null, Func<Task<bool>>? ConfirmAsync = null);

/// <summary>A hardware multi-choice option (dropdown) shown in the Options group. <paramref name="Read"/>
/// is the readback equivalent of <see cref="OptionToggle.Read"/>: it returns the index (into
/// <paramref name="Options"/>) that the hardware is actually in, used to verify a pick.</summary>
public sealed record OptionChoice(string Label, bool Supported, IReadOnlyList<string> Options, int InitialIndex,
                                  Action<int> OnChange, Func<int>? Read = null);
