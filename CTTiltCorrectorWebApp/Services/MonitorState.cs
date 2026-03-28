namespace CTTiltCorrector.Services;

// ─── Per-user channel ─────────────────────────────────────────────────────────

/// <summary>
/// Holds the live log state for a single user's active job.
/// </summary>
public class UserMonitorChannel
{
    private const int MaxLines = 500;
    private readonly List<string> _lines = new(MaxLines);
    private readonly List<Action> _subscribers = new();
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public string? CurrentJobDescription { get; private set; }

    public IReadOnlyList<string> Lines
    {
        get { lock (_lock) return _lines.ToList(); }
    }

    public IProgress<string> CreateProgressReporter()
        => new Progress<string>(OnMessage);

    public void Subscribe(Action onChange)
    {
        lock (_lock) _subscribers.Add(onChange);
    }

    public void Unsubscribe(Action onChange)
    {
        lock (_lock) _subscribers.Remove(onChange);
    }

    public void SetJobStarted(string description)
    {
        lock (_lock)
        {
            IsRunning = true;
            CurrentJobDescription = description;
            _lines.Clear();
        }
        Notify();
    }

    public void SetJobFinished()
    {
        lock (_lock) IsRunning = false;
        Notify();
    }

    internal void OnMessage(string message)
    {
        lock (_lock)
        {
            if (_lines.Count >= MaxLines)
                _lines.RemoveAt(0);
            _lines.Add(message);
        }
        Notify();
    }

    private void Notify()
    {
        List<Action> subs;
        lock (_lock) subs = _subscribers.ToList();
        foreach (var sub in subs)
        {
            try { sub(); }
            catch { /* subscriber may have disconnected */ }
        }
    }
}

// ─── Singleton router ─────────────────────────────────────────────────────────

/// <summary>
/// Singleton that maintains one <see cref="UserMonitorChannel"/> per Windows
/// username. The Monitor page requests the channel for the current user —
/// it will only ever receive messages from that user's own jobs.
/// </summary>
public class MonitorState
{
    private readonly Dictionary<string, UserMonitorChannel> _channels = new();
    private readonly object _lock = new();

    /// <summary>
    /// Returns the channel for the given user, creating it if it doesn't exist.
    /// </summary>
    public UserMonitorChannel GetChannel(string userName)
    {
        lock (_lock)
        {
            if (!_channels.TryGetValue(userName, out var channel))
            {
                channel = new UserMonitorChannel();
                _channels[userName] = channel;
            }
            return channel;
        }
    }

    /// <summary>
    /// Convenience: creates a progress reporter that writes to the given user's channel.
    /// Called by CorrectionService when a job starts.
    /// </summary>
    public IProgress<string> CreateProgressReporter(string userName)
        => GetChannel(userName).CreateProgressReporter();

    /// <summary>
    /// Marks the user's channel as running and clears previous log lines.
    /// </summary>
    public void SetJobStarted(string userName, string description)
        => GetChannel(userName).SetJobStarted(description);

    /// <summary>
    /// Marks the user's channel as finished.
    /// </summary>
    public void SetJobFinished(string userName)
        => GetChannel(userName).SetJobFinished();
}
