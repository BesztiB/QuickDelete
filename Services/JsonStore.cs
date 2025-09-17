using System.Text.Json;

namespace TelegramAutoDeleteBot.Services;

public static class JsonStore
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public static async Task<T?> LoadAsync<T>(string path)
    {
        if (!File.Exists(path)) return default;
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(fs, _opts);
    }

    public static async Task SaveAsync<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, value, _opts);
            await fs.FlushAsync();
        }
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
