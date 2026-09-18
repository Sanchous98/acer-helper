using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>What applying a DECLARED setting needs from whoever owns the option's declared set, the graph and the
/// transport — the contract for one of the things the owner's model applies, declared here and implemented by the
/// layer that owns all three (Infrastructure/Composition/LaptopService.Toggles.cs).
///
/// WHAT CROSSES. <see cref="SettingDeclaration"/> and the value's string: both are Domain (Domain/DeclaredSetting.cs
/// — the declaration is the CONTRACT half of the declared-settings model and the owner left it there), and the
/// value is the backend's own opaque id, which nothing above the port interprets. So this is the one member of the
/// family whose state already crossed the boundary before the use case existed, which is why the contract is about
/// the ORDER rather than about a conversion.
///
/// THREE MEMBERS, because the three things are genuinely different acts with different lock rules: the WRITE is an
/// EC/WMI transaction that must run OUTSIDE the graph lock (docs/domain-refactoring-plan.md §4) and refuses by
/// throwing; the REMEMBER is a write into a shared collection and takes the lock itself; the PERSIST is the store.
/// There is no fourth member asking whether this machine declares the option — that question is part of what the
/// write is, because it is answered against the set the model was constructed with, which is the model's and not
/// this layer's.</summary>
public interface IDeclaredSettingTarget
{
    /// <summary>Hand the value to the option's own contract, which is where the write happens: a refusal comes out
    /// as <see cref="SettingNotAppliedException"/>, carrying the key and the transport's own words, and an option
    /// this machine does not declare is refused the same way before any transport is touched. NO GRAPH LOCK IS
    /// HELD over this call — it is an EC/WMI write.</summary>
    void Write(SettingDeclaration setting, string value);

    /// <summary>Record what the hardware took, under the option's OWN key. Takes the graph lock itself: the
    /// collection it writes is shared with the background pass, and the lock is not held by the caller.</summary>
    void Remember(SettingDeclaration setting, string value);

    /// <summary>Write the graph out. Separate from <see cref="Remember"/> because that is what the service has
    /// always done — the record lands under one hold and the file is written under the next — and because a
    /// contract that folded the two would decide, silently, that a record is never observed before it is
    /// persisted.</summary>
    void Persist();
}

/// <summary>A declared setting as a use case: apply the value the user picked to the hardware, then record what
/// took.
///
/// WHAT IT DECIDES, and it is the order plus what it refuses to do first:
/// <list type="number">
/// <item><b>The hardware write comes FIRST and stands alone.</b> A refused switch throws out of
/// <see cref="IDeclaredSettingTarget.Write"/> before anything below runs, so NOTHING is remembered and nothing is
/// saved — the order IS the guarantee, not a check that follows it. Recording the attempt first and correcting it
/// afterwards is the shape this use case exists to make unrepresentable: a value the hardware refused must not be
/// in the file, or the next boot would show a setting this machine is not in.</item>
/// <item><b>Only what took is recorded</b>, and it is recorded under the option's own key, so the row that reads
/// it back is reading the same name the backend declared.</item>
/// <item><b>Then the graph is written out</b>, because the value is the user's choice rather than a session's
/// state.</item>
/// </list>
///
/// The refusal is an EXCEPTION and is deliberately not caught here. The reason it carries is information about
/// what happened — which setting, and the transport's words — and not a sentence: this layer knows the setting by
/// its opaque key and has no name to put in a message, so the row that called this composes what the user reads
/// from its own label (<c>OptionsAssembler.RunSet</c>).</summary>
public static class ApplyDeclaredSetting
{
    public static void Run(SettingDeclaration setting, string value, IDeclaredSettingTarget target)
    {
        target.Write(setting, value);
        target.Remember(setting, value);
        target.Persist();
    }
}
