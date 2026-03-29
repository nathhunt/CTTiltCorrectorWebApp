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

    /// <summary><see langword="true"/> while a correction job is actively running for this user.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Human-readable description of the current job (Patient ID + abbreviated Series UID),
    /// set when the job starts and cleared implicitly when <see cref="IsRunning"/> becomes
    /// <see langword="false"/>.
    /// </summary>
    public string? CurrentJobDescription { get; private set; }

    /// <summary>
    /// Snapshot of all log lines buffered for the current job (up to <c>MaxLines = 500</c>).
    /// Returns a copy — safe to enumerate outside the lock.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_lock) return _lines.ToList(); }
    }

    /// <summary>
    /// Creates an <see cref="IProgress{T}"/> that appends messages to this channel
    /// and notifies all subscribers. Used by <c>CorrectionService</c> to stream
    /// log output to the Monitor page.
    /// </summary>
    public IProgress<string> CreateProgressReporter()
        => new Progress<string>(OnMessage);

    /// <summary>
    /// Registers a callback to be invoked whenever a new message arrives or the
    /// job state changes. Typically called by the Monitor Blazor component on mount.
    /// </summary>
    public void Subscribe(Action onChange)
    {
        lock (_lock) _subscribers.Add(onChange);
    }

    /// <summary>
    /// Removes a previously registered callback. Call on component dispose to
    /// prevent memory leaks from disconnected Blazor circuits.
    /// </summary>
    public void Unsubscribe(Action onChange)
    {
        lock (_lock) _subscribers.Remove(onChange);
    }

    /// <summary>
    /// Marks the channel as running, sets the job description, and clears
    /// any lines from the previous job. Called by <c>CorrectionService</c>
    /// at the start of each job.
    /// </summary>
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

    /// <summary>
    /// Marks the channel as no longer running. Called by <c>CorrectionService</c>
    /// in the <c>finally</c> block after a job completes, fails, or is cancelled.
    /// </summary>
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
