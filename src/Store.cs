using System.Text.Json;

namespace FseMultiMonitor;

internal sealed record EventRecord(string Event, DateTimeOffset TimeUtc, string Description, Inspector.Snapshot Snapshot);
internal sealed record ProbeTransaction(string Id, string Hwnd, uint ProcessId, DateTimeOffset ProcessStartUtc,
    int Left, int Top, int Width, int Height, bool Restored, DateTimeOffset CreatedUtc,
    string Kind = "position", int OriginalCloak = 0);

internal static class Store
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private static int _cancelled;

    internal static string Root
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("FSE_MM_DATA");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FseMultiMonitor");
        }
    }
    internal static string ProfilesDirectory => Path.Combine(Root, "profiles");
    private static string SessionsDirectory => Path.Combine(Root, "sessions");
    private static string TransactionsDirectory => Path.Combine(Root, "transactions");
    internal static bool IsCancelled => Volatile.Read(ref _cancelled) != 0;
    internal static void Cancel() => Interlocked.Exchange(ref _cancelled, 1);
    internal static void ResetCancel() => Interlocked.Exchange(ref _cancelled, 0);

    internal static string NewSessionPath(string prefix)
    {
        Directory.CreateDirectory(SessionsDirectory);
        return Path.Combine(SessionsDirectory, $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl");
    }

    internal static string WriteJsonLine(string eventName, Inspector.Snapshot snapshot)
    {
        string path = NewSessionPath(eventName);
        Append(path, new EventRecord(eventName, DateTimeOffset.UtcNow, "Point-in-time state", snapshot));
        return path;
    }

    internal static void Append(string path, EventRecord entry) =>
        File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);

    internal static string SaveTransaction(ProbeTransaction transaction)
    {
        Directory.CreateDirectory(TransactionsDirectory);
        string path = TransactionPath(transaction.Id);
        File.WriteAllText(path, JsonSerializer.Serialize(transaction, JsonOptions));
        return path;
    }

    internal static ProbeTransaction ReadTransaction(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid transaction ID.");
        string path = TransactionPath(id);
        if (!File.Exists(path)) throw new FileNotFoundException("Recovery transaction not found.", path);
        return JsonSerializer.Deserialize<ProbeTransaction>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Recovery transaction was empty.");
    }

    internal static void MarkRestored(string id)
    {
        var old = ReadTransaction(id);
        var updated = old with { Restored = true };
        string path = TransactionPath(id);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(updated, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static string TransactionPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid transaction ID.");
        return Path.Combine(TransactionsDirectory, $"{id}.json");
    }
}
