using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MessengerCRM.Server;

public sealed class AuthService
{
    private const int Iterations = 150_000;
    private readonly string _path = @"C:\MessengerCRMServer\data\auth.json";
    private readonly string _setupCodePath = @"C:\MessengerCRMServer\data\setup-code.txt";
    private readonly object _gate = new();

    public bool IsConfigured => File.Exists(_path) && Load() is not null;

    public void Setup(string setupCode, string username, string password)
    {
        lock (_gate)
        {
            if (IsConfigured) throw new InvalidOperationException("Доступ уже налаштований.");
            if (!File.Exists(_setupCodePath)) throw new UnauthorizedAccessException("Первинне налаштування заблоковане.");
            var expected = File.ReadAllText(_setupCodePath).Trim();
            var supplied = (setupCode ?? "").Trim();
            if (expected.Length == 0 || supplied.Length != expected.Length ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied)))
                throw new UnauthorizedAccessException("Невірний код первинного налаштування.");

            username = (username ?? "").Trim();
            if (username.Length < 3) throw new ArgumentException("Логін має містити щонайменше 3 символи.");
            if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
                throw new ArgumentException("Пароль має містити щонайменше 10 символів.");
            Save(username, password);
            try { File.Delete(_setupCodePath); } catch { }
        }
    }

    public bool Validate(string username, string password)
    {
        lock (_gate)
        {
            var data = Load();
            if (data is null || !string.Equals(username?.Trim(), data.Username, StringComparison.OrdinalIgnoreCase)) return false;
            var salt = Convert.FromBase64String(data.Salt);
            var expected = Convert.FromBase64String(data.Hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }

    public void ChangePassword(string username, string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 10)
            throw new ArgumentException("Новий пароль має містити щонайменше 10 символів.");
        if (!Validate(username, currentPassword)) throw new UnauthorizedAccessException("Невірний поточний пароль.");
        Save(username.Trim(), newPassword);
    }

    private void Save(string username, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var data = new AuthData(username, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private AuthData? Load()
    {
        if (!File.Exists(_path)) return null;
        try { return JsonSerializer.Deserialize<AuthData>(File.ReadAllText(_path)); }
        catch { return null; }
    }

    private sealed record AuthData(string Username, string Salt, string Hash);
}
