namespace MessageService.Web.Diagnostics;

/// <summary>
/// 記憶體環形緩衝，保留最新 200 筆日誌記錄。
/// </summary>
public sealed class LogRingBuffer
{
    public const int Capacity = 200;
    private readonly object _lock = new();
    private readonly Queue<LogBufferEntry> _entries = new(Capacity);

    /// <summary>
    /// 新增一筆記錄至緩衝區。若已滿則淘汰最舊的一筆。
    /// </summary>
    public void Add(LogBufferEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            if (_entries.Count >= Capacity)
            {
                _entries.Dequeue();
            }
            _entries.Enqueue(entry);
        }
    }

    /// <summary>
    /// 取得目前緩衝區的快照，由新到舊排序。
    /// </summary>
    public IReadOnlyList<LogBufferEntry> Snapshot()
    {
        LogBufferEntry[] snapshot;
        lock (_lock)
        {
            snapshot = _entries.ToArray();
        }

        Array.Reverse(snapshot);
        return snapshot;
    }
}
