using System.Text.Json;

namespace TaskAssist.Core;

public sealed record DisplayPreferences(double Scale = 1, bool Beginner = true);

public static class PreferencesStore
{
    public static void Save(string folder, DisplayPreferences preferences)
    {
        if (preferences.Scale is not (1 or 1.5 or 2)) throw new RuleException("未対応の表示倍率です。");
        var path = Path.Combine(folder, "preferences.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(preferences));
        File.Move(path + ".new", path, true);
    }
}
