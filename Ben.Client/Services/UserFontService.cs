namespace Ben.Services;

public class UserFontService
{
    private const string SelectedUserFontPreferenceKey = "SelectedUserFont";
    private const string DefaultUserFontAlias = "Inter-Regular";

    private string _currentUserFont;

    public string CurrentUserFont => _currentUserFont;

    public IReadOnlyList<AppFontOption> AvailableFonts => AppFontCatalog.UserSelectableFonts;

    public UserFontService()
    {
        _currentUserFont = NormalizeFontAlias(Preferences.Get(SelectedUserFontPreferenceKey, DefaultUserFontAlias));
    }

    public void InitializeUserFont()
    {
        ApplyUserFont(_currentUserFont, skipPrefsSave: true);
    }

    public void ApplyUserFont(string fontAlias, bool skipPrefsSave = false)
    {
        var normalizedFontAlias = NormalizeFontAlias(fontAlias);
        _currentUserFont = normalizedFontAlias;

        var appResources = Application.Current?.Resources;
        if (appResources != null)
        {
            appResources[AppFontCatalog.UserFontResourceKey] = normalizedFontAlias;
        }

        if (!skipPrefsSave)
        {
            Preferences.Set(SelectedUserFontPreferenceKey, normalizedFontAlias);
        }
    }

    private static string NormalizeFontAlias(string fontAlias)
    {
        return AppFontCatalog.UserSelectableFonts.Any(x => x.Alias == fontAlias)
            ? fontAlias
            : DefaultUserFontAlias;
    }
}
