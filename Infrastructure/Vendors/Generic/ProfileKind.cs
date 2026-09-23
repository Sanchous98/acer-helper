using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Generic;

/// <summary>Coarse class of a performance profile — what the app branches on when it needs to know "is this the
/// Turbo profile / the Balanced one" without naming any vendor profile. It drives the generic "toggle
/// performance" hotkey semantics and the per-profile presentation the vendor tables carry.
///
/// IT LIVES HERE AND NOT IN <c>Domain/</c> SINCE 2026-09-22, by owner decision, and the measurement behind the
/// move is worth keeping: the enum was declared beside <see cref="PerformanceProfile"/> and travelled ON that
/// record, which made the Domain carry a CLASSIFICATION IT NEVER READ — no file under <c>Domain/</c> or
/// <c>Application/</c> read a member of it, while every branch on it lived here (the Turbo switch in
/// <c>LaptopService.Profiles</c>, the EC usage-mode byte in <c>AcerEcHidController.ModeFor</c>, the vendor
/// tables) or in the UI (the segment colours, the Turbo row). The owner's criterion for the Domain is "только
/// значения, важные для логики" — only values the logic depends on — and a value the Domain merely CARRIES is
/// a value the Domain should not know.
///
/// WHAT DID NOT MOVE WITH IT is as deliberate as what did: <see cref="AccentColor"/> is still a Domain type.
/// It is the RGB payload of a lighting write (<c>Domain/Rgb.cs</c>), so the lighting chain really does reason
/// in it, and the move would have rippled through that chain for no architectural gain. A colour is not a
/// Domain concept; the payload of a write the Domain models is.
///
/// The type keeps its name and its members on purpose: the decision was the PLACEMENT, and bundling a rename
/// into a ~20-site diff would make the placement unreviewable. What changed is who may name it — see
/// <see cref="ProfileTraits"/> for the lookup the layers above use, and the guard in
/// <c>tests/AcerHelper.Tests/DomainNeutralityTests</c> that keeps the Domain from naming it again — the rule
/// there is the pin, and returning any of this is a decision to argue rather than a line to restore.</summary>
public enum ProfileKind { Quiet, Eco, Balanced, Performance, Turbo, Other }

/// <summary>What the backend that offers a profile knows about it BEYOND the profile itself: the coarse class
/// the app's logic branches on, and the two colours the UI paints with — <see cref="Accent"/> (the tray icon and
/// the profile segment) and <see cref="FlashColor"/> (the fixed colour the firmware paints its "operating mode"
/// indicator, lightbar + keyboard flash, for that profile).
///
/// IT IS A LOOKUP AND NOT A FIELD, which is the shape the owner's criterion forces. The values are PER-BACKEND
/// DATA — they always lived in the vendor tables (<c>AcerProfiles</c>, <c>DellDevice.Windows</c>,
/// <c>PowerProfiles.*</c>, <c>OverlayPowerProfiles</c>), and the profile record was only a courier for them.
/// Now the table that OFFERS the profiles also answers about them (<see cref="IProfileTraits"/>), so the list the
/// UI shows and the colours it paints cannot come from two different places.
///
/// <see cref="Unknown"/> rather than <c>default</c>: this is a struct, and <c>default(ProfileTraits)</c> would
/// read as <see cref="ProfileKind.Quiet"/> because Quiet is the enum's first member. A profile nobody classifies
/// is <see cref="ProfileKind.Other"/> with no colours — the same thing an unrecognised vendor byte has always
/// been shown as — and that reading must be asked for by name.</summary>
public readonly record struct ProfileTraits(ProfileKind Kind, AccentColor? Accent = null, AccentColor? FlashColor = null)
{
    /// <summary>What is known about a profile no table classifies. The colours are null and NOT grey: grey is
    /// what the painters already fall back to (<c>TrayController.ProfileIcon</c>, <c>ProfilesViewModel</c>), so
    /// a lookup that invented one here would put the same pixel on screen through a second path.</summary>
    public static readonly ProfileTraits Unknown = new(ProfileKind.Other);

    /// <summary>The traits of <paramref name="profile"/> as the port that offers it declares them, or
    /// <see cref="Unknown"/> when that port has no such knowledge (a fake machine, or a port that offers profiles
    /// without classifying them). Null-tolerant so a caller holding an optional port — or no profile at all —
    /// needs no branch of its own.</summary>
    public static ProfileTraits Of(IPowerProfiles? profiles, PerformanceProfile profile)
        => profiles is IProfileTraits traits ? traits.Traits(profile) : Unknown;
}

/// <summary>A machine's own reading of the profiles it offers: which class each of its modes belongs to, and the
/// colours the app paints it with. Implemented by the PORT THAT OFFERS THEM — <see cref="ProfilesPort"/>, the
/// generic power-profiles ports, and the Acer decorators — and deliberately a SECOND interface rather than a
/// member of <see cref="IPowerProfiles"/>: that port is declared in <c>Domain/Ports.cs</c>, and a Domain
/// declaration may not name <see cref="ProfileKind"/> any more.
///
/// The pairing is the point. A backend's profile table is one table, and both the list it hands out and the
/// classification of that list come off it, so "the UI paints a profile the table does not know" is not a state
/// that can be reached by setting two things in the wrong order. Callers reach it through
/// <see cref="ProfileTraits.Of"/>, which answers <see cref="ProfileTraits.Unknown"/> for a port that does not
/// implement this — "the app knows no class for this mode", which is exactly what an unrecognised vendor mode
/// is.</summary>
public interface IProfileTraits
{
    /// <summary>The class and colours of one of this port's profiles. A profile this port does not offer — a
    /// foreign id, or one it has no entry for — is <see cref="ProfileTraits.Unknown"/>, never a nearby mode.</summary>
    ProfileTraits Traits(PerformanceProfile profile);
}

/// <summary>An <see cref="IProfileTraits"/> over a plain lookup function — the shape a vendor table takes, since
/// what a table has to say about a profile is exactly "read my row for it". Exists so a table can be handed over
/// as a method group (<c>new ProfileTraitsLookup(AcerProfiles.TraitsOf)</c>) instead of growing a class whose
/// only content is one forwarding call.</summary>
public sealed class ProfileTraitsLookup(Func<PerformanceProfile, ProfileTraits> lookup) : IProfileTraits
{
    public ProfileTraits Traits(PerformanceProfile profile) => lookup(profile);
}
