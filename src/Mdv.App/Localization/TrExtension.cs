using Avalonia.Markup.Xaml;
using Mdv.Core;

namespace Mdv.App.Localization;

/// <summary>
/// Interface text in XAML: <c>Text="{l:Tr 'Drop a mod here'}"</c> — the English text is the key
/// (<see cref="L"/>). Resolved when the view loads; the window is rebuilt when the language changes.
/// </summary>
public sealed class TrExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
