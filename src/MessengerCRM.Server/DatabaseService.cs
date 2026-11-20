using Microsoft.Data.Sqlite;

namespace MessengerCRM.Server;

public sealed class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService()
    {
        var dbPath = Path.Combine(@"C:\MessengerCRMServer\data", "server.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath};Cache=Shared";
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Contacts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Phone TEXT NOT NULL UNIQUE,
    Notes TEXT NOT NULL DEFAULT '',
    IsArchived INTEGER NOT NULL DEFAULT 0,
    CreatedAtUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Contacts_Name ON Contacts(Name);

CREATE TABLE IF NOT EXISTS Messages (
    Id TEXT PRIMARY KEY,
    ContactName TEXT NOT NULL,
    Phone TEXT NOT NULL,
    Channel TEXT NOT NULL,
    Direction TEXT NOT NULL,
    Text TEXT NOT NULL,
    Status TEXT NOT NULL,
    ExternalPeerId INTEGER NULL,
    ExternalMessageId INTEGER NULL,
    CreatedAtUtc TEXT NOT NULL,
    SentAtUtc TEXT NULL,
    ReadAtUtc TEXT NULL,
    RepliedAtUtc TEXT NULL,
    Error TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_Messages_Status ON Messages(Status, CreatedAtUtc);
CREATE INDEX IF NOT EXISTS IX_Messages_Peer ON Messages(Channel, ExternalPeerId, ExternalMessageId);
CREATE INDEX IF NOT EXISTS IX_Messages_Phone ON Messages(Phone, CreatedAtUtc);
";
        cmd.ExecuteNonQuery();
        EnsureColumn(db, "Contacts", "TelegramState", "TEXT NOT NULL DEFAULT 'unknown'");
        EnsureColumn(db, "Contacts", "WhatsAppState", "TEXT NOT NULL DEFAULT 'unknown'");
        EnsureColumn(db, "Contacts", "ChannelsCheckedAtUtc", "TEXT NULL");
        EnsureColumn(db, "Contacts", "TelegramUserId", "INTEGER NULL");
        EnsureColumn(db, "Contacts", "WhatsAppJid", "TEXT NULL");
        EnsureColumn(db, "Messages", "ReplyText", "TEXT NULL");
        EnsureColumn(db, "Messages", "ExternalPeerKey", "TEXT NULL");
        EnsureColumn(db, "Messages", "ExternalMessageKey", "TEXT NULL");
        EnsureColumn(db, "Messages", "IsHidden", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureColumn(SqliteConnection db, string table, string column, string definition)
    {
        using var check = db.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = db.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    public IReadOnlyList<ContactRow> GetContacts(bool includeArchived = false)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        const string deliveryColumns = @",
(SELECT m.Status FROM Messages m WHERE m.Phone=c.Phone AND m.Channel='telegram' AND m.Direction='out' ORDER BY m.CreatedAtUtc DESC LIMIT 1) AS TelegramDeliveryState,
(SELECT m.Status FROM Messages m WHERE m.Phone=c.Phone AND m.Channel='whatsapp' AND m.Direction='out' ORDER BY m.CreatedAtUtc DESC LIMIT 1) AS WhatsAppDeliveryState";
        cmd.CommandText = includeArchived
            ? "SELECT c.*" + deliveryColumns + " FROM Contacts c ORDER BY c.IsArchived, c.Name COLLATE NOCASE, c.Id"
            : "SELECT c.*" + deliveryColumns + " FROM Contacts c WHERE c.IsArchived=0 ORDER BY c.Name COLLATE NOCASE, c.Id";
        using var reader = cmd.ExecuteReader();
        var result = new List<ContactRow>();
        while (reader.Read()) result.Add(ReadContact(reader));
        return result;
    }

    public IReadOnlyList<ContactRow> GetContactsByIds(IEnumerable<int> ids)
    {
        var idList = ids.Distinct().Where(x => x > 0).ToArray();
        if (idList.Length == 0) return Array.Empty<ContactRow>();
        using var db = Open();
        using var cmd = db.CreateCommand();
        var p = new List<string>();
        for (var i = 0; i < idList.Length; i++)
        {
            var name = "$id" + i;
            p.Add(name);
            cmd.Parameters.AddWithValue(name, idList[i]);
        }
        cmd.CommandText = $@"SELECT c.*,
(SELECT m.Status FROM Messages m WHERE m.Phone=c.Phone AND m.Channel='telegram' AND m.Direction='out' ORDER BY m.CreatedAtUtc DESC LIMIT 1) AS TelegramDeliveryState,
(SELECT m.Status FROM Messages m WHERE m.Phone=c.Phone AND m.Channel='whatsapp' AND m.Direction='out' ORDER BY m.CreatedAtUtc DESC LIMIT 1) AS WhatsAppDeliveryState
FROM Contacts c WHERE c.IsArchived=0 AND c.Id IN ({string.Join(',', p)}) ORDER BY c.Name COLLATE NOCASE";
        using var reader = cmd.ExecuteReader();
        var result = new List<ContactRow>();
        while (reader.Read()) result.Add(ReadContact(reader));
        return result;
    }

    public int AddContact(ContactCreateRequest request)
    {
        var name = (request.Name ?? "").Trim();
        var phone = NormalizePhone(request.Phone ?? "");
        if (name.Length == 0) throw new ArgumentException("Ім'я обов'язкове.");
        if (!IsPlausiblePhone(phone)) throw new ArgumentException("Некоректний номер телефону.");
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"INSERT INTO Contacts(Name,Phone,Notes,IsArchived,CreatedAtUtc)
VALUES($name,$phone,$notes,0,$created); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$phone", phone);
        cmd.Parameters.AddWithValue("$notes", (request.Notes ?? "").Trim());
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt32((long)(cmd.ExecuteScalar() ?? 0L));
    }

    public bool TryAddImportedContact(string name, string phone, string notes, out bool duplicate)
    {
        duplicate = false;
        name = (name ?? "").Trim();
        phone = NormalizePhone(phone ?? "");
        if (name.Length == 0 || !IsPlausiblePhone(phone)) return false;
        try
        {
            AddContact(new ContactCreateRequest(name, phone, notes));
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            duplicate = true;
            return false;
        }
    }


    public void UpdateContactChannelState(int id, string telegramState, string whatsAppState, long? telegramUserId = null, string? whatsAppJid = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"UPDATE Contacts SET TelegramState=$tg, WhatsAppState=$wa,
TelegramUserId=CASE WHEN $tg='yes' THEN COALESCE($tgid,TelegramUserId) ELSE NULL END,
WhatsAppJid=CASE WHEN $wa='yes' THEN COALESCE($wajid,WhatsAppJid) ELSE NULL END,
ChannelsCheckedAtUtc=$now WHERE Id=$id";
        cmd.Parameters.AddWithValue("$tg", telegramState);
        cmd.Parameters.AddWithValue("$wa", whatsAppState);
        cmd.Parameters.AddWithValue("$tgid", (object?)telegramUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wajid", (object?)whatsAppJid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateTelegramChannelState(int id, string state, long? userId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"UPDATE Contacts SET
TelegramState=CASE WHEN $state='unknown' THEN TelegramState ELSE $state END,
TelegramUserId=CASE
  WHEN $state='yes' THEN COALESCE($uid,TelegramUserId)
  WHEN $state IN ('hidden','no') THEN NULL
  ELSE TelegramUserId END,
ChannelsCheckedAtUtc=$now WHERE Id=$id";
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$uid", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateWhatsAppChannelState(int id, string state, string? jid)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"UPDATE Contacts SET
WhatsAppState=CASE WHEN $state='unknown' THEN WhatsAppState ELSE $state END,
WhatsAppJid=CASE
  WHEN $state='yes' THEN COALESCE($jid,WhatsAppJid)
  WHEN $state='no' THEN NULL
  ELSE WhatsAppJid END,
ChannelsCheckedAtUtc=$now WHERE Id=$id";
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$jid", (object?)jid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public ContactRow? GetContactByPhone(string phone)
    {
        phone = NormalizePhone(phone);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM Contacts WHERE Phone=$phone LIMIT 1";
        cmd.Parameters.AddWithValue("$phone", phone);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadContact(reader) : null;
    }

    public void MarkContactChannelConfirmed(string phone, string channel)
    {
        phone = NormalizePhone(phone);
        using var db = Open();
        using var cmd = db.CreateCommand();
        if (channel.Equals("telegram", StringComparison.OrdinalIgnoreCase))
            cmd.CommandText = "UPDATE Contacts SET TelegramState='yes', ChannelsCheckedAtUtc=$now WHERE Phone=$phone";
        else if (channel.Equals("whatsapp", StringComparison.OrdinalIgnoreCase))
            cmd.CommandText = "UPDATE Contacts SET WhatsAppState='yes', ChannelsCheckedAtUtc=$now WHERE Phone=$phone";
        else return;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$phone", phone);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<MessageRow> GetTrackingCandidates(string channel, int limit = 5)
    {
        limit = Math.Clamp(limit, 1, 20);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $@"SELECT * FROM Messages WHERE Direction='out' AND Channel=$channel AND Status IN ('sent','read') AND SentAtUtc IS NOT NULL ORDER BY SentAtUtc DESC LIMIT {limit}";
        cmd.Parameters.AddWithValue("$channel", channel);
        using var reader = cmd.ExecuteReader();
        var result = new List<MessageRow>();
        while (reader.Read()) result.Add(ReadRow(reader));
        return result;
    }

    public void MarkRead(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Messages SET Status='read', ReadAtUtc=COALESCE(ReadAtUtc,$now) WHERE Id=$id AND Status='sent'";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkReplied(string id, string? replyText)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Messages SET Status='replied', RepliedAtUtc=COALESCE(RepliedAtUtc,$now), ReplyText=$reply WHERE Id=$id AND Direction='out'";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$reply", (object?)replyText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void ArchiveContact(int id, bool archived)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Contacts SET IsArchived=$archived WHERE Id=$id";
        cmd.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public bool DeleteContact(int id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM Contacts WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int DeleteMessages(IEnumerable<string> ids)
    {
        var list = ids.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(1000).ToArray();
        if (list.Length == 0) return 0;
        using var db = Open();
        using var cmd = db.CreateCommand();
        var ps = new List<string>();
        for (var i = 0; i < list.Length; i++)
        {
            var pn = "$id" + i;
            ps.Add(pn);
            cmd.Parameters.AddWithValue(pn, list[i]);
        }
        cmd.CommandText = $"UPDATE Messages SET IsHidden=1 WHERE Id IN ({string.Join(',', ps)}) AND Status NOT IN ('queued','processing')";
        return cmd.ExecuteNonQuery();
    }

    public int ClearMessageHistory()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Messages SET IsHidden=1 WHERE Status NOT IN ('queued','processing') AND IsHidden=0";
        return cmd.ExecuteNonQuery();
    }

    public string Enqueue(SendMessageRequest request)
    {
        var row = new MessageRow
        {
            ContactName = request.Name.Trim(),
            Phone = NormalizePhone(request.Phone),
            Channel = request.Channel.Trim().ToLowerInvariant(),
            Text = request.Text,
            Direction = "out",
            Status = "queued"
        };
        Insert(row);
        return row.Id;
    }

    public MessageRow? GetNextQueued()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM Messages WHERE Direction='out' AND Status='queued' ORDER BY CreatedAtUtc LIMIT 1";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    public void MarkProcessing(string id) => UpdateStatus(id, "processing");

    public void MarkSent(string id, long? peerId, long? messageId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"UPDATE Messages SET Status='sent', SentAtUtc=$now, ExternalPeerId=$peer, ExternalMessageId=$mid, Error=NULL WHERE Id=$id";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$peer", (object?)peerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkSentByKey(string id, string? peerKey, string? messageKey)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"UPDATE Messages SET Status='sent', SentAtUtc=$now, ExternalPeerKey=$peer, ExternalMessageKey=$mid, Error=NULL WHERE Id=$id";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$peer", (object?)peerKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", (object?)messageKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkReadByExternalKey(string channel, string messageKey)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"UPDATE Messages SET Status='read', ReadAtUtc=COALESCE(ReadAtUtc,$now)
WHERE Direction='out' AND Channel=$channel AND ExternalMessageKey=$mid AND Status='sent'";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$channel", channel);
        cmd.Parameters.AddWithValue("$mid", messageKey);
        cmd.ExecuteNonQuery();
    }

    public void AddIncomingByKey(string channel, string peerKey, string? messageKey, string text, string? phone = null)
    {
        var candidatePhone = string.IsNullOrWhiteSpace(phone) ? "" : NormalizePhone(phone);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"UPDATE Messages SET Status='replied', RepliedAtUtc=COALESCE(RepliedAtUtc,$now), ReplyText=$reply
WHERE Id=(SELECT Id FROM Messages WHERE Direction='out' AND Channel=$channel AND Status IN ('sent','read')
AND (($peer<>'' AND ExternalPeerKey=$peer) OR ($phone<>'' AND Phone=$phone)) ORDER BY SentAtUtc DESC LIMIT 1)";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$reply", text);
        cmd.Parameters.AddWithValue("$channel", channel);
        cmd.Parameters.AddWithValue("$peer", peerKey ?? "");
        cmd.Parameters.AddWithValue("$phone", candidatePhone);
        cmd.ExecuteNonQuery();
    }

    public void ReplaceExternalMessageId(string channel, long peerId, long oldMessageId, long newMessageId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"UPDATE Messages SET ExternalMessageId=$newId
WHERE Direction='out' AND Channel=$channel AND ExternalPeerId=$peer AND ExternalMessageId=$oldId";
        cmd.Parameters.AddWithValue("$newId", newMessageId);
        cmd.Parameters.AddWithValue("$channel", channel);
        cmd.Parameters.AddWithValue("$peer", peerId);
        cmd.Parameters.AddWithValue("$oldId", oldMessageId);
        cmd.ExecuteNonQuery();
    }

    public void MarkFailed(string id, string error)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Messages SET Status='failed', Error=$error WHERE Id=$id";
        cmd.Parameters.AddWithValue("$error", error);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void ResetInterrupted()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Messages SET Status='queued' WHERE Direction='out' AND Status='processing'";
        cmd.ExecuteNonQuery();
    }

    public void AddIncoming(string channel, long? peerId, long? messageId, string text, string? phone = null, string? name = null)
    {
        if (peerId is null) return;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"UPDATE Messages SET Status='replied', RepliedAtUtc=COALESCE(RepliedAtUtc,$now), ReplyText=$reply
WHERE Id=(SELECT Id FROM Messages WHERE Direction='out' AND Channel=$channel AND ExternalPeerId=$peer
AND Status IN ('sent','read') ORDER BY SentAtUtc DESC LIMIT 1)";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$reply", text);
        cmd.Parameters.AddWithValue("$channel", channel);
        cmd.Parameters.AddWithValue("$peer", peerId.Value);
        cmd.ExecuteNonQuery();
    }

    public void MarkReadThrough(string channel, long peerId, long maxMessageId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"UPDATE Messages SET Status='read', ReadAtUtc=COALESCE(ReadAtUtc,$now)
WHERE Direction='out' AND Channel=$channel AND ExternalPeerId=$peer AND ExternalMessageId <= $maxId AND Status='sent'";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$channel", channel);
        cmd.Parameters.AddWithValue("$peer", peerId);
        cmd.Parameters.AddWithValue("$maxId", maxMessageId);
        cmd.ExecuteNonQuery();
    }

    public long? GetLatestPeerIdForPhone(string channel, string phone)
    {
        phone = NormalizePhone(phone);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"SELECT ExternalPeerId FROM Messages WHERE Channel=$channel AND Phone=$phone AND ExternalPeerId IS NOT NULL ORDER BY CreatedAtUtc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$channel", channel);
        cmd.Parameters.AddWithValue("$phone", phone);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }

    public string? GetLatestPeerKeyForPhone(string channel, string phone)
    {
        phone = NormalizePhone(phone);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"SELECT ExternalPeerKey FROM Messages WHERE Channel=$channel AND Phone=$phone AND ExternalPeerKey IS NOT NULL AND ExternalPeerKey<>'' ORDER BY CreatedAtUtc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$channel", channel);
        cmd.Parameters.AddWithValue("$phone", phone);
        return cmd.ExecuteScalar() as string;
    }

    public string? GetPhoneForPeer(string channel, long peerId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"SELECT Phone FROM Messages WHERE Channel=$channel AND ExternalPeerId=$peer AND Phone<>'' ORDER BY CreatedAtUtc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$channel", channel);
        cmd.Parameters.AddWithValue("$peer", peerId);
        return cmd.ExecuteScalar() as string;
    }

    public IReadOnlyList<MessageRow> GetRecent(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM Messages WHERE Direction='out' AND IsHidden=0 ORDER BY CreatedAtUtc DESC LIMIT {limit}";
        using var reader = cmd.ExecuteReader();
        var result = new List<MessageRow>();
        while (reader.Read()) result.Add(ReadRow(reader));
        return result;
    }

    public IReadOnlyList<ExportResultRow> GetExportResults(IEnumerable<int>? contactIds = null)
    {
        var ids = contactIds?.Distinct().Where(x => x > 0).ToArray();
        using var db = Open();
        using var cmd = db.CreateCommand();
        var phoneFilter = "";
        if (ids is { Length: > 0 })
        {
            var ps = new List<string>();
            for (var i = 0; i < ids.Length; i++)
            {
                var pn = "$cid" + i;
                ps.Add(pn);
                cmd.Parameters.AddWithValue(pn, ids[i]);
            }
            phoneFilter = $" AND m.Phone IN (SELECT Phone FROM Contacts WHERE Id IN ({string.Join(',', ps)}))";
        }
        cmd.CommandText = $@"WITH ranked AS (
SELECT m.ContactName,m.Phone,m.Status,
ROW_NUMBER() OVER(PARTITION BY m.Phone ORDER BY COALESCE(m.SentAtUtc,m.CreatedAtUtc) DESC) rn
FROM Messages m WHERE m.Direction='out' {phoneFilter})
SELECT ContactName,Phone,Status FROM ranked WHERE rn=1 ORDER BY ContactName COLLATE NOCASE";
        using var reader = cmd.ExecuteReader();
        var rows = new List<ExportResultRow>();
        while (reader.Read())
        {
            var status = reader.GetString(2);
            var result = status == "replied" ? "відповіли" : status == "read" ? "прочитано без відповіді" : "не прочитано";
            rows.Add(new ExportResultRow(reader.GetString(0), reader.GetString(1), result));
        }
        return rows;
    }

    public QueueSummary GetSummary()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"SELECT
SUM(CASE WHEN Status='queued' THEN 1 ELSE 0 END),
SUM(CASE WHEN Status='sent' THEN 1 ELSE 0 END),
SUM(CASE WHEN Status='read' THEN 1 ELSE 0 END),
SUM(CASE WHEN Status='replied' THEN 1 ELSE 0 END),
SUM(CASE WHEN Status='failed' THEN 1 ELSE 0 END),
SUM(CASE WHEN Direction='in' THEN 1 ELSE 0 END)
FROM Messages WHERE IsHidden=0";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new QueueSummary(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt32(5));
    }

    private void Insert(MessageRow row)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"INSERT INTO Messages
(Id,ContactName,Phone,Channel,Direction,Text,Status,ExternalPeerId,ExternalMessageId,ExternalPeerKey,ExternalMessageKey,CreatedAtUtc,SentAtUtc,ReadAtUtc,RepliedAtUtc,Error)
VALUES ($id,$name,$phone,$channel,$direction,$text,$status,$peer,$mid,$peerKey,$midKey,$created,$sent,$read,$replied,$error)";
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$name", row.ContactName);
        cmd.Parameters.AddWithValue("$phone", row.Phone);
        cmd.Parameters.AddWithValue("$channel", row.Channel);
        cmd.Parameters.AddWithValue("$direction", row.Direction);
        cmd.Parameters.AddWithValue("$text", row.Text);
        cmd.Parameters.AddWithValue("$status", row.Status);
        cmd.Parameters.AddWithValue("$peer", (object?)row.ExternalPeerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", (object?)row.ExternalMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$peerKey", (object?)row.ExternalPeerKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$midKey", (object?)row.ExternalMessageKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", row.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$sent", (object?)row.SentAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$read", (object?)row.ReadAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$replied", (object?)row.RepliedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)row.Error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void UpdateStatus(string id, string status)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Messages SET Status=$status WHERE Id=$id";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static ContactRow ReadContact(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(r.GetOrdinal("Id")),
        Name = r.GetString(r.GetOrdinal("Name")),
        Phone = r.GetString(r.GetOrdinal("Phone")),
        Notes = r.GetString(r.GetOrdinal("Notes")),
        IsArchived = r.GetInt32(r.GetOrdinal("IsArchived")) != 0,
        TelegramState = r.GetString(r.GetOrdinal("TelegramState")),
        WhatsAppState = r.GetString(r.GetOrdinal("WhatsAppState")),
        TelegramDeliveryState = ReadOptionalString(r, "TelegramDeliveryState"),
        WhatsAppDeliveryState = ReadOptionalString(r, "WhatsAppDeliveryState"),
        TelegramUserId = r.IsDBNull(r.GetOrdinal("TelegramUserId")) ? null : r.GetInt64(r.GetOrdinal("TelegramUserId")),
        WhatsAppJid = r.IsDBNull(r.GetOrdinal("WhatsAppJid")) ? null : r.GetString(r.GetOrdinal("WhatsAppJid")),
        ChannelsCheckedAtUtc = ReadDate(r, "ChannelsCheckedAtUtc"),
        CreatedAtUtc = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAtUtc")), null, System.Globalization.DateTimeStyles.RoundtripKind)
    };

    private static MessageRow ReadRow(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        ContactName = r.GetString(r.GetOrdinal("ContactName")),
        Phone = r.GetString(r.GetOrdinal("Phone")),
        Channel = r.GetString(r.GetOrdinal("Channel")),
        Direction = r.GetString(r.GetOrdinal("Direction")),
        Text = r.GetString(r.GetOrdinal("Text")),
        Status = r.GetString(r.GetOrdinal("Status")),
        ExternalPeerId = r.IsDBNull(r.GetOrdinal("ExternalPeerId")) ? null : r.GetInt64(r.GetOrdinal("ExternalPeerId")),
        ExternalMessageId = r.IsDBNull(r.GetOrdinal("ExternalMessageId")) ? null : r.GetInt64(r.GetOrdinal("ExternalMessageId")),
        ExternalPeerKey = r.IsDBNull(r.GetOrdinal("ExternalPeerKey")) ? null : r.GetString(r.GetOrdinal("ExternalPeerKey")),
        ExternalMessageKey = r.IsDBNull(r.GetOrdinal("ExternalMessageKey")) ? null : r.GetString(r.GetOrdinal("ExternalMessageKey")),
        CreatedAtUtc = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAtUtc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
        SentAtUtc = ReadDate(r, "SentAtUtc"),
        ReadAtUtc = ReadDate(r, "ReadAtUtc"),
        RepliedAtUtc = ReadDate(r, "RepliedAtUtc"),
        ReplyText = r.IsDBNull(r.GetOrdinal("ReplyText")) ? null : r.GetString(r.GetOrdinal("ReplyText")),
        Error = r.IsDBNull(r.GetOrdinal("Error")) ? null : r.GetString(r.GetOrdinal("Error"))
    };

    private static DateTime? ReadDate(SqliteDataReader r, string name)
    {
        var ordinal = r.GetOrdinal(name);
        return r.IsDBNull(ordinal) ? null : DateTime.Parse(r.GetString(ordinal), null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static string? ReadOptionalString(SqliteDataReader r, string name)
    {
        for (var i = 0; i < r.FieldCount; i++)
            if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                return r.IsDBNull(i) ? null : r.GetString(i);
        return null;
    }

    public static string NormalizePhone(string input)
    {
        var digits = new string((input ?? "").Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.Length == 10 && digits.StartsWith('0')) digits = "38" + digits;
        if (digits.Length == 9) digits = "380" + digits;
        return digits.Length == 0 ? "" : "+" + digits;
    }

    public static bool IsPlausiblePhone(string phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length is >= 10 and <= 15;
    }
}
