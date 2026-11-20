using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MessengerCRM.Server;

public sealed class SecretStore
{
    private readonly string _path = @"C:\MessengerCRMServer\data\secrets.json";
    private readonly object _gate = new();

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var all = LoadRaw();
            var bytes = Encoding.UTF8.GetBytes(value);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
            all[key] = Convert.ToBase64String(protectedBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            var all = LoadRaw();
            if (!all.TryGetValue(key, out var encoded) || string.IsNullOrWhiteSpace(encoded)) return null;
            try
            {
                var protectedBytes = Convert.FromBase64String(encoded);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
    }

    public bool Has(string key) => Get(key) is not null;

    private Dictionary<string, string> LoadRaw()
    {
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                   ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }
}
