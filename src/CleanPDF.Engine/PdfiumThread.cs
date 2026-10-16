namespace CleanPDF.Engine;

public enum WorkPriority
{
    /// <summary>Visible content: tiles on screen, text under the pointer.</summary>
    High,
    /// <summary>Prefetch, thumbnails, search.</summary>
    Low,
}

/// <summary>
/// PDFium is not thread-safe, so every native call runs on this one thread. High-priority
/// work always runs before low-priority work; work whose token is cancelled before it starts
/// is skipped without touching PDFium.
/// </summary>
internal static class PdfiumThread
{
    private static readonly Queue<WorkItem> High = new();
    private static readonly Queue<WorkItem> Low = new();
    private static readonly object Gate = new();
    private static readonly Thread Worker;

    static PdfiumThread()
    {
        Worker = new Thread(Loop) { IsBackground = true, Name = "PDFium" };
        Worker.Start();
    }

    public static bool IsCurrent => Thread.CurrentThread == Worker;

    public static Task<T> Run<T>(Func<T> work, WorkPriority priority = WorkPriority.High, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<T>(ct);
        if (IsCurrent)
            return Task.FromResult(work());

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        lock (Gate)
        {
            (priority == WorkPriority.High ? High : Low).Enqueue(item);
            Monitor.Pulse(Gate);
        }
        return tcs.Task;
    }

    public static Task Run(Action work, WorkPriority priority = WorkPriority.High, CancellationToken ct = default) =>
        Run(() => { work(); return true; }, priority, ct);

    private static void Loop()
    {
        while (true)
        {
            WorkItem item;
            lock (Gate)
            {
                while (High.Count == 0 && Low.Count == 0)
                    Monitor.Wait(Gate);
                item = High.Count > 0 ? High.Dequeue() : Low.Dequeue();
            }
            item.Execute();
        }
    }

    private sealed record WorkItem(Action Execute);
}
