using AirCode.Models;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace AirCode.Services;

/// <summary>SQLite-backed local storage for settings, chat history, and transfer history.</summary>
public class DatabaseService
{
    private readonly string _dbPath;
    private readonly string _connStr;

    public DatabaseService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirCode");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "aircode.db");
        _connStr = $"Data Source={_dbPath}";
        Initialize();
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ChatHistory (
                Id          TEXT PRIMARY KEY,
                SenderId    TEXT NOT NULL,
                SenderName  TEXT NOT NULL,
                RecipientId TEXT NOT NULL,
                Content     TEXT NOT NULL,
                Timestamp   INTEGER NOT NULL,
                IsGroup     INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS TransferHistory (
                Id         TEXT PRIMARY KEY,
                FileName   TEXT NOT NULL,
                FileSize   INTEGER NOT NULL,
                PeerId     TEXT NOT NULL,
                PeerName   TEXT NOT NULL,
                Direction  TEXT NOT NULL,
                Status     TEXT NOT NULL,
                SavePath   TEXT,
                StartedAt  INTEGER NOT NULL,
                CompletedAt INTEGER
            );";
        cmd.ExecuteNonQuery();
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    public AppSettings LoadSettings()
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = 'app_settings'";
        var raw = cmd.ExecuteScalar() as string;
        if (raw == null) return new AppSettings();
        return JsonConvert.DeserializeObject<AppSettings>(raw) ?? new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Settings (Key, Value) VALUES ('app_settings', @v)";
        cmd.Parameters.AddWithValue("@v", JsonConvert.SerializeObject(settings));
        cmd.ExecuteNonQuery();
    }

    // ── Chat History ──────────────────────────────────────────────────────────

    public void SaveChatMessage(ChatMessage msg)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR IGNORE INTO ChatHistory
            (Id, SenderId, SenderName, RecipientId, Content, Timestamp, IsGroup)
            VALUES (@id, @sid, @sn, @rid, @c, @ts, @ig)";
        cmd.Parameters.AddWithValue("@id", msg.Id);
        cmd.Parameters.AddWithValue("@sid", msg.SenderId);
        cmd.Parameters.AddWithValue("@sn", msg.SenderName);
        cmd.Parameters.AddWithValue("@rid", msg.RecipientId ?? "");
        cmd.Parameters.AddWithValue("@c", msg.Content);
        cmd.Parameters.AddWithValue("@ts", new DateTimeOffset(msg.Timestamp).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@ig", msg.IsGroupMessage ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public List<ChatMessage> LoadGroupChatHistory(int limit = 200)
    {
        var list = new List<ChatMessage>();
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT * FROM ChatHistory WHERE IsGroup=1
            ORDER BY Timestamp DESC LIMIT @l";
        cmd.Parameters.AddWithValue("@l", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ChatMessage
            {
                Id = reader.GetString(0),
                SenderId = reader.GetString(1),
                SenderName = reader.GetString(2),
                RecipientId = reader.GetString(3),
                Content = reader.GetString(4),
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).LocalDateTime
            });
        }
        list.Reverse();
        return list;
    }

    // ── Transfer History ──────────────────────────────────────────────────────

    public void SaveTransfer(FileTransfer t)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO TransferHistory
            (Id, FileName, FileSize, PeerId, PeerName, Direction, Status, SavePath, StartedAt, CompletedAt)
            VALUES (@id,@fn,@fs,@pid,@pn,@dir,@st,@sp,@sa,@ca)";
        cmd.Parameters.AddWithValue("@id", t.Id);
        cmd.Parameters.AddWithValue("@fn", t.FileName);
        cmd.Parameters.AddWithValue("@fs", t.FileSize);
        cmd.Parameters.AddWithValue("@pid", t.PeerId);
        cmd.Parameters.AddWithValue("@pn", t.PeerName);
        cmd.Parameters.AddWithValue("@dir", t.Direction.ToString());
        cmd.Parameters.AddWithValue("@st", t.Status.ToString());
        cmd.Parameters.AddWithValue("@sp", t.SavePath ?? "");
        cmd.Parameters.AddWithValue("@sa", new DateTimeOffset(t.StartedAt).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@ca", t.CompletedAt.HasValue
            ? new DateTimeOffset(t.CompletedAt.Value).ToUnixTimeSeconds() : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<FileTransfer> LoadTransferHistory()
    {
        var list = new List<FileTransfer>();
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM TransferHistory ORDER BY StartedAt DESC LIMIT 500";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FileTransfer
            {
                Id = reader.GetString(0),
                FileName = reader.GetString(1),
                FileSize = reader.GetInt64(2),
                PeerId = reader.GetString(3),
                PeerName = reader.GetString(4),
                Direction = Enum.Parse<TransferDirection>(reader.GetString(5)),
                Status = Enum.Parse<TransferStatus>(reader.GetString(6)),
                SavePath = reader.IsDBNull(7) ? null : reader.GetString(7),
                StartedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)).LocalDateTime,
                CompletedAt = reader.IsDBNull(9) ? null :
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(9)).LocalDateTime
            });
        }
        return list;
    }
}
