namespace WPods.Ui;

/// <summary>Linux stand-in for the parts of upstream's NativeUi that shared UI code uses.</summary>
internal static class NativeUi
{
    /// <summary>The user's language as a BCP 47 tag ("es-ES") from LANGUAGE / LC_ALL / LC_MESSAGES / LANG.</summary>
    public static string UserUiLanguage()
    {
        foreach (string name in new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                // "es_ES.UTF-8", or LANGUAGE's "es_ES:en"
                string first = value.Split(':')[0];
                string tag = first.Split('.', '@')[0].Replace('_', '-');
                if (tag is not ("C" or "POSIX" or ""))
                {
                    return tag;
                }
            }
        }
        return "en-US";
    }
}
