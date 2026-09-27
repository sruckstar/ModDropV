using Mdv.Core;
using CommunityToolkit.Mvvm.Input;

namespace Mdv.App.ViewModels;

/// <summary>A language in the header's menu.</summary>
public sealed record LanguageOption(string Code, string NativeName, bool IsCurrent);

public sealed partial class MainViewModel
{
    /// <summary>The interface languages, the current one ticked.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
        [.. L.Languages.Select(l => new LanguageOption(l.Code, l.NativeName, l.Code == L.Current))];

    /// <summary>"EN", "PT", "ZH"… on the header's language button.</summary>
    public string LanguageShort => L.Current.Split('-')[0].ToUpperInvariant();

    /// <summary>Raised to switch the interface language (the app saves it and rebuilds the window).</summary>
    public event Action<string>? LanguageChanged;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private void SetLanguage(string code)
    {
        if (code == L.Current) return;
        Settings.Language = code;
        Settings.Save();
        LanguageChanged?.Invoke(code);
    }
}
