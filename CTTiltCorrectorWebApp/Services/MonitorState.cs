namespace CTTiltCorrector.Services;

/// <summary>
/// Singleton that bridges the background <see cref="CorrectionJobProcessor"/>
/// to the Monitor Blazor page. Progress messages are stored in a ring-buffer
/// and fanned-out to all subscribed UI callbacks via StateHasChanged.
/// </summary>
public class MonitorState
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

    private void OnMessage(string message)
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
