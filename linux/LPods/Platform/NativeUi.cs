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

    /// <summary>The language's name in that language ("español (España)"), for the "Same as the system" row.</summary>
    public static string NativeDisplayName(string tag)
    {
        try
        {
            return System.Globalization.CultureInfo.GetCultureInfo(tag).NativeName;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return tag;
        }
    }

    /// <summary>Upstream maps Windows virtual keys to characters; there are no Windows hotkeys on Linux.</summary>
    public static uint MapVirtualKey(uint code, uint mapType) => 0;
}
