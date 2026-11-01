using System.Text.RegularExpressions;

namespace ProxySentryCsv.Services;

internal static class FirefoxNativePreferences
{
    internal static string RemoveUserAgentOverride(string text) => Regex.Replace(text,
        @"(?m)^[\t ]*user_pref\(""general\.useragent\.override"",[^\r\n]*\);[\t ]*\r?\n?", "");

    public static void Migrate(string profilePath)
    {
        // Firefox persists user.js values in prefs.js, even after user.js stops setting them.
        var path = Path.Combine(profilePath, "prefs.js");
        if (!File.Exists(path)) return;
        var oldText = File.ReadAllText(path);
        var newText = RemoveUserAgentOverride(oldText);
        if (oldText == newText) return;
        var backup = path + ".before-native-ua.bak";
        if (!File.Exists(backup)) File.Copy(path, backup);
        var temporary = path + ".native-ua.tmp";
        File.WriteAllText(temporary, newText);
        File.Move(temporary, path, overwrite: true);
    }
}
