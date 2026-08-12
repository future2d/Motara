using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Motara.Persistence;

namespace Motara.App.Localization;

/// <summary>Resolves the supported UI culture and reads strings from its Avalonia resources.</summary>
public sealed class LocalizationManager
{
    private const string EnglishCultureName = "en-US";
    private const string ChineseCultureName = "zh-CN";
    private readonly ResourceDictionary resources;

    public LocalizationManager(CultureInfo? systemCulture = null)
    {
        Culture = ResolveCulture(systemCulture ?? CultureInfo.CurrentUICulture);
        var resourceUri = new Uri($"avares://Motara.App/Assets/Strings.{Culture.Name}.axaml");
        resources = (ResourceDictionary)AvaloniaXamlLoader.Load(resourceUri, resourceUri);
    }

    public CultureInfo Culture { get; }

    public static LocalizationManager Create(
        ApplicationLanguage preference,
        CultureInfo? systemCulture = null)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        return preference switch
        {
            ApplicationLanguage.Automatic => new LocalizationManager(systemCulture),
            ApplicationLanguage.English => new LocalizationManager(
                CultureInfo.GetCultureInfo(EnglishCultureName)),
            ApplicationLanguage.SimplifiedChinese => new LocalizationManager(
                CultureInfo.GetCultureInfo(ChineseCultureName)),
            _ => throw new ArgumentOutOfRangeException(nameof(preference)),
        };
    }

    public static CultureInfo ResolveCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TwoLetterISOLanguageName switch
        {
            "zh" => CultureInfo.GetCultureInfo(ChineseCultureName),
            _ => CultureInfo.GetCultureInfo(EnglishCultureName),
        };
    }

    public string GetString(string resourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        if (resources.TryGetResource(resourceKey, null, out object? value) && value is string text)
        {
            return text;
        }

        throw new KeyNotFoundException(resourceKey);
    }
}
