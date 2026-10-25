namespace Vinliste.Core.Diagnostics;

public enum ActivityLevel
{
    /// <summary>Individual web requests: plenty of them, hidden unless asked for.</summary>
    Detail,
    Info,
    Success,
    Warning,
    Error,
}

public sealed record ActivityEntry(DateTimeOffset Time, string Source, string Message, ActivityLevel Level);

/// <summary>
/// What the app's background work is doing, line by line, for the in-app activity panel.
/// Thread-safe; keeps the most recent entries so the panel can show history when opened.
/// </summary>
public sealed class ActivityLog(int capacity = 1000)
{
    private readonly Queue<ActivityEntry> _entries = new();
    private readonly object _lock = new();

    /// <summary>Raised on the writer's thread for every new entry.</summary>
    public event EventHandler<ActivityEntry>? EntryAdded;

    public void Write(string source, string message, ActivityLevel level = ActivityLevel.Info)
    {
        var entry = new ActivityEntry(DateTimeOffset.Now, source, message, level);
        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > capacity)
            {
                _entries.Dequeue();
            }
        }
        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
