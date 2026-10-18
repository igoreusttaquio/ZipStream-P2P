using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace ZipP2P.App.Localization;

/// <summary>
/// Singleton de internacionalização. Expõe um indexador para binding no XAML e
/// notifica mudanças de idioma via <see cref="INotifyPropertyChanged"/>.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    private const string ResourceBaseName = "ZipP2P.App.Localization.Strings";

    public static Localizer Instance { get; } = new();

    private readonly ResourceManager _resources =
        new(ResourceBaseName, typeof(Localizer).Assembly);

    private CultureInfo _culture;

    private Localizer()
    {
        _culture = CultureInfo.GetCultureInfo("en-US");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Idiomas suportados (en-US, pt-BR, es-ES).</summary>
    public static IReadOnlyList<CultureInfo> SupportedCultures { get; } = new[]
    {
        CultureInfo.GetCultureInfo("en-US"),
        CultureInfo.GetCultureInfo("pt-BR"),
        CultureInfo.GetCultureInfo("es-ES"),
    };

    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (Equals(_culture, value))
                return;

            _culture = value;
            CultureInfo.CurrentUICulture = value;
            CultureInfo.CurrentCulture = value;

            // string.Empty sinaliza "todas as propriedades mudaram" (recarrega os bindings).
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    public string this[string key] => _resources.GetString(key, _culture) ?? key;
}
