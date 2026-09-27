using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskPair.Core.Config;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.ViewModels.Settings;

/// <summary>
/// One tab of the settings screen. Sections never touch application-wide state: the owner hands the current
/// settings in and takes the updated ones back, so each tab can be tested on its own. Settings save as the user
/// changes them, so a section reports every change and the owner decides when to write.
/// </summary>
public interface ISettingsSection
{
    /// <summary>Fills the controls from the stored settings; <paramref name="host"/> is null when the host service is not running.</summary>
    void Load(DesktopConfig desktop, HostConfig? host);

    /// <summary>Raised when something the user changed should be stored.</summary>
    event Action? Changed;

    DesktopConfig Apply(DesktopConfig config) => config;

    HostConfig Apply(HostConfig config) => config;
}

/// <summary>
/// Turns property changes into one <see cref="Changed"/> event, quietly while <see cref="Load"/> runs. Anything
/// that is only on screen (a notice, a derived flag, a password being typed) is excluded by name so it never
/// causes a save.
/// </summary>
public abstract partial class SettingsSectionBase : ObservableObject, ISettingsSection
{
    private int _loading;

    /// <summary>
    /// What just happened, shown for a few seconds and then gone.
    ///
    /// Here rather than in each section, because it is the same thing everywhere and because every copy
    /// of it had the same fault: set when something happened, cleared when the next thing happened, which
    /// on a page somebody sets once and leaves is never. A confirmation of something done a quarter of an
    /// hour ago reads as a description of the page's present state.
    /// </summary>
    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    public event Action? Changed;

    public abstract void Load(DesktopConfig desktop, HostConfig? host);

    public virtual DesktopConfig Apply(DesktopConfig config) => config;

    public virtual HostConfig Apply(HostConfig config) => config;

    /// <summary>
    /// Further properties that live only on screen and must not trigger a save.
    ///
    /// Notice and Problem are not in here and must not be added: they are handled below, before this is
    /// consulted. An override replaces this list wholesale, and four sections wrote one that did not
    /// mention Notice -- so on those, saying something to the user would have written config.json.
    /// </summary>
    protected virtual bool IsTransient(string propertyName) => false;

    /// <summary>Wraps a <see cref="Load"/> so filling the controls does not look like the user changing them.</summary>
    protected IDisposable Loading() => new LoadScope(this);

    /// <summary>For changes that do not come from a property, such as a number finishing being typed.</summary>
    protected void RaiseChanged()
    {
        if (_loading == 0)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Whether this section's notice is a complaint rather than a confirmation. Shown differently, and
    /// most sections only ever say that something worked.
    /// </summary>
    protected virtual bool NoticeIsProblem => false;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(Notice))
        {
            Toasts.Current.Show(Notice, NoticeIsProblem);
        }

        // Never a setting on any section, so not left to each one to remember.
        if (e.PropertyName is { } name && name is not ("Notice" or "Problem") && !IsTransient(name))
        {
            RaiseChanged();
        }
    }

    private sealed class LoadScope : IDisposable
    {
        private readonly SettingsSectionBase _owner;

        public LoadScope(SettingsSectionBase owner)
        {
            _owner = owner;
            _owner._loading++;
        }

        public void Dispose() => _owner._loading--;
    }
}
