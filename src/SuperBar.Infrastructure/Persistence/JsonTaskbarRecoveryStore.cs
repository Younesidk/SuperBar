using System.Text.Json;
using SuperBar.Core.Shell;

namespace SuperBar.Infrastructure.Persistence;

/// <summary>Writes the native-taskbar recovery record atomically (temp file + move) so a crash mid-write cannot corrupt it.</summary>
public sealed class JsonTaskbarRecoveryStore(string filePath) : ITaskbarRecoveryStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public NativeTaskbarRecoveryRecord? Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<NativeTaskbarRecoveryRecord>(File.ReadAllText(filePath), Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged record still means "something was changed": assume the taskbar was hidden.
            return new NativeTaskbarRecoveryRecord(true, false, 0, 0, DateTimeOffset.MinValue);
        }
    }

    public void Save(NativeTaskbarRecoveryRecord record)
    {
        var temp = filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Options));
        File.Move(temp, filePath, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
        }
    }
}
