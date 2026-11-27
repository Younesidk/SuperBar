using System.Text.Json;
using SuperBar.Core.Shell;

namespace SuperBar.Infrastructure.Persistence;

/// <summary>Saves badges as JSON, atomically (temp file + move). A missing or damaged file just means no badges.</summary>
public sealed class JsonBadgeStore(string filePath) : IBadgeStore
{
    public IReadOnlyList<SavedBadge> Load()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<List<SavedBadge>>(File.ReadAllBytes(filePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<SavedBadge> badges)
    {
        var temp = filePath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(badges));
        File.Move(temp, filePath, overwrite: true);
    }
}
