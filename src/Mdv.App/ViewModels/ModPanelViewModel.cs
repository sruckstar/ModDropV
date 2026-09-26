using Mdv.Core.Mods;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mdv.App.ViewModels;

/// <summary>
/// One mod type's settings panel. The window shows it through DataTemplates keyed on the
/// concrete type, in up to three slots: <c>Source</c> (the modder's input, above the output
/// card), <c>Form</c> (the settings) and <c>Side</c> (the analysis column). The shell owns
/// the drop, the game, the output folder, the log and the result; a panel turns its form
/// into a <see cref="PanelJob"/>.
/// </summary>
public abstract class ModPanelViewModel(MainViewModel shell) : ObservableObject
{
    public MainViewModel Shell { get; } = shell;

    public abstract ModCategory Category { get; }

    /// <summary>False for a kind that can't be built / installed yet.</summary>
    public virtual bool CanRun => true;

    /// <summary>Check the form and turn it into a job, or say what's missing.</summary>
    public abstract (PanelJob? Job, string? Error) Prepare();

    /// <summary>The window switched between the player and the modder flow.</summary>
    public virtual void OnModeChanged() { }
}

/// <summary>
/// What a panel hands the shell to run: the build / install itself (on a worker thread),
/// and for the player the package whose install plan is shown before anything happens.
/// </summary>
/// <param name="Stage">the caption over the build stage ("Installing into GTA V…")</param>
public sealed record PanelJob(string Stage, Func<Action<string>, PanelOutcome> Run)
{
    public ModPackage? Package { get; init; }
    /// <summary>Lines the log starts with (what is being built from).</summary>
    public IReadOnlyList<string> LogHeader { get; init; } = [];
}

/// <summary>How a job ended, for the result banner. A failure marks the log as an error log.</summary>
public sealed record PanelOutcome(bool Ok, string Title, string Detail, string? Path = null);

/// <summary>A kind of mod ModDrop V doesn't build or install yet: the panel just says so.</summary>
public sealed class ComingSoonViewModel(MainViewModel shell, ModCategory category, string what, string blurb)
    : ModPanelViewModel(shell)
{
    public override ModCategory Category => category;
    public override bool CanRun => false;

    /// <summary>"Vehicle add-ons".</summary>
    public string What { get; } = what;
    /// <summary>What it will do once it's there.</summary>
    public string Blurb { get; } = blurb;

    public override (PanelJob? Job, string? Error) Prepare() =>
        (null, $"{What} can't be built yet — they're coming in a later version of ModDrop V.");
}
