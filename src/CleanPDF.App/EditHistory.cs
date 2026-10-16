using CleanPDF.Engine;

namespace CleanPDF.App;

/// <summary>
/// Undo and redo for one tab. Before each edit the whole document is kept as PDF bytes; undoing
/// reopens those bytes. Simple and exact for every kind of edit, at the cost of memory, so only
/// the most recent steps are kept.
/// </summary>
internal sealed class EditHistory
{
    private const int MaxSteps = 30;
    private const long MaxBytes = 600L * 1024 * 1024;

    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Call before an edit: remembers the document as it is now.</summary>
    public async Task CheckpointAsync(PdfDocument doc)
    {
        _undo.Add(new Snapshot(await doc.SaveToBytesAsync(), !doc.IsModified));
        _redo.Clear();
        // Drop the oldest steps past the limits.
        while (_undo.Count > MaxSteps || (_undo.Count > 1 && _undo.Sum(s => (long)s.Bytes.Length) > MaxBytes))
            _undo.RemoveAt(0);
    }

    /// <summary>Forgets the last checkpoint, when the edit after it failed.</summary>
    public void DropLast()
    {
        if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
    }

    /// <summary>
    /// One step back (or forward when <paramref name="redo"/>): returns the state to go to and
    /// keeps <paramref name="current"/> so the step can be reversed. Null when there's nothing to do.
    /// </summary>
    public Snapshot? Step(bool redo, Snapshot current)
    {
        var from = redo ? _redo : _undo;
        if (from.Count == 0) return null;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        (redo ? _undo : _redo).Add(current);
        return target;
    }

    /// <summary>After a save the file on disk changed, so no earlier state matches it any more.</summary>
    public void MarkAllUnsaved()
    {
        foreach (var s in _undo.Concat(_redo)) s.MatchesFile = false;
    }

    internal sealed class Snapshot(byte[] bytes, bool matchesFile)
    {
        public byte[] Bytes { get; } = bytes;
        /// <summary>The state equals the file on disk, so returning to it clears the unsaved-changes dot.</summary>
        public bool MatchesFile { get; set; } = matchesFile;
    }
}
