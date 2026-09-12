using System.Text.Json;
using Contracts;

namespace ChatCore;

public sealed record SavedSession(SessionEntry Entry, string? Model, ChatMessage[] Messages, SessionInfo? Info, TurnInfo[] Turns, bool TitleManuallySet = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasConversation => Messages.Any(message => message.Role == "user");
}

public sealed class SessionStore(string directory)
{
    private readonly object _sync = new();
    private static string ValidateId(string id) => Guid.TryParse(id, out _) && !id.Contains('/') && !id.Contains('\\')
        ? id : throw new ArgumentException("Invalid session ID.");
    private string FilePath(string id) => Path.Combine(directory, ValidateId(id) + ".json");

    public SavedSession Load(string id)
    {
        lock (_sync)
        {
            var path = FilePath(id);
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new IOException("Session history is too large.");
            var saved = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(path)) ?? throw new IOException("Invalid session history.");
            if (saved.Entry is null || saved.Messages is null || saved.Turns is null || saved.Entry.Id != id)
                throw new IOException("Invalid session history or identity mismatch.");
            return saved;
        }
    }

    public SessionEntry[] List()
    {
        lock (_sync)
        {
            if (!Directory.Exists(directory)) return [];
            var entries = new List<SessionEntry>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try { entries.Add(Load(Path.GetFileNameWithoutExtension(path)).Entry); }
                catch (Exception failure) when (failure is IOException or JsonException or ArgumentException) { }
            }
            return entries.OrderByDescending(entry => entry.UpdatedAt).ToArray();
        }
    }

    public void Save(SavedSession session)
    {
        lock (_sync)
        {
            var path = FilePath(session.Entry.Id);
            Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, session);
                if (new FileInfo(temporary).Length > 64 * 1024 * 1024) throw new IOException("Session history exceeds 64 MiB. Start a new chat.");
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public void Rename(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Any(char.IsControl))
            throw new ArgumentException("Session names must contain 1 to 120 printable characters.");
        lock (_sync)
        {
            var saved = Load(id);
            Save(saved with { Entry = saved.Entry with { Title = title.Trim() }, TitleManuallySet = true });
        }
    }

    public void Delete(string id) { lock (_sync) File.Delete(FilePath(id)); }
}