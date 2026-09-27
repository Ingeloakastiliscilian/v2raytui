namespace V2RayTui.Engine;

/// <summary>
/// Process-wide message sink. Collects engine messages (v2rayN's message view + snack notices)
/// into a bounded ring buffer and fans them out to the TUI or stdout.
/// </summary>
public static class LogBus
{
    private const int Capacity = 3000;
    private static readonly Lock _gate = new();
    private static readonly Queue<string> _lines = new();

    /// <summary>Raised for every log line (from any thread).</summary>
    public static event Action<string>? LineAdded;

    /// <summary>Raised for short user-facing notices (from any thread).</summary>
    public static event Action<string>? NoticeAdded;

    /// <summary>When true, lines are also written to stdout (daemon / CLI mode).</summary>
    public static bool EchoToConsole { get; set; }

    /// <summary>When false, core process output (very chatty during speed tests) is dropped.</summary>
    public static bool KeepCoreOutput { get; set; } = true;

    public static void Write(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        foreach (var raw in message.Replace("\r", "").Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }
            var line = $"{stamp} {raw}";
            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > Capacity)
                {
                    _lines.Dequeue();
                }
            }
            if (EchoToConsole)
            {
                Console.WriteLine(line);
            }
            LineAdded?.Invoke(line);
        }
    }

    public static void Notice(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        Write(message);
        NoticeAdded?.Invoke(message.Replace('\n', ' ').Trim());
    }

    public static string[] Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToArray();
        }
    }

    public static void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }
}

/// <summary>Minimal IObserver adapter (ServiceLib exposes events as IObservable).</summary>
internal sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(T value) => onNext(value);
}
