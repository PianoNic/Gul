using System.Text.Json;

namespace Gul.Client;

public sealed class Config
{
    public string? ServerUrl { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gul");

    private static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Config Load()
    {
        if (!File.Exists(FilePath)) return new Config();
        try
        {
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch
        {
            return new Config();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
