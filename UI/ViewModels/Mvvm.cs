using CommunityToolkit.Mvvm.ComponentModel;

namespace AcerHelper.UI.ViewModels;

/// <summary>A capability section shown in the dashboard. Concrete sections (Monitor, Profiles, …)
/// are mapped to their views by the DataTemplates in App.axaml — adding a feature/vendor section
/// means a new section view-model + a DataTemplate, and the shell stays untouched.</summary>
public abstract class SectionViewModel : ObservableObject
{
    /// <summary>Rough relative height of the section's card, used only to balance the two-column
    /// area of the dashboard (heavier sections are placed first into the shorter column). It needs
    /// no precision — it just keeps the columns from ending up lopsided.</summary>
    public virtual double LayoutWeight => 1;
}
