using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Monoview.Engine;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Monoview.App.Viewer;

/// <summary>
/// Form filling: clicks on fields and keys while a field has focus go to the document's form engine, which draws
/// the result; the viewer redraws the page when told to. Each field changed is one undo step.
/// </summary>
public sealed partial class PdfViewer
{
    private readonly LruCache<int, IReadOnlyList<FormField>> _formFields = new(48);
    private readonly HashSet<int> _formFieldsLoading = new();
    private readonly HashSet<int> _formPagesToRedraw = new();
    private readonly InputCursor _fieldTextCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private bool _formFocused;      // a field has keyboard focus
    private bool _formCheckpoint;   // an undo step was taken when the field got focus
    private bool _formChanged;      // the focused field's value changed since then
    private int? _formPressPage;

    /// <summary>Drops the undo step taken when a field got focus, when nothing changed in it.</summary>
    public Action? CancelEdit { get; set; }

    /// <summary>True while a form field has keyboard focus: typing goes to it.</summary>
    public bool FormHasFocus => _formFocused;

    private void AttachForm(PdfDocument doc)
    {
        if (!doc.HasForm) return;
        doc.FormPageInvalidated += OnFormPageInvalidated;
        doc.FormValueChanged += OnFormValueChanged;
    }

    private void DetachForm(PdfDocument doc)
    {
        doc.FormPageInvalidated -= OnFormPageInvalidated;
        doc.FormValueChanged -= OnFormValueChanged;
        _formFields.Clear();
        _formFocused = _formCheckpoint = _formChanged = false;
    }

    // Raised on the PDFium thread, often several times per key; redraw each page once, on the UI thread.
    private void OnFormPageInvalidated(object? sender, int page)
    {
        lock (_formPagesToRedraw)
            if (!_formPagesToRedraw.Add(page) || _formPagesToRedraw.Count > 1) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            int[] pages;
            lock (_formPagesToRedraw)
            {
                pages = _formPagesToRedraw.ToArray();
                _formPagesToRedraw.Clear();
            }
            foreach (int p in pages) RedrawTiles(p);
        });
    }

    private void OnFormValueChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        _formChanged = true;
        PagesEdited?.Invoke(this, []); // the tab shows unsaved changes
    });

    /// <summary>Re-renders a page's tiles only, keeping the old ones until the new arrive; thumbnails wait until the field is left.</summary>
    private void RedrawTiles(int page)
    {
        if (page < 0 || page >= PageCount) return;
        _tiles.RemoveWhere(k => k.Page == page);
        if (_views.TryGetValue(page, out var view)) view.MarkTilesStale(_w[page]);
        UpdateViewport(requestTiles: true);
    }

    private void EnsureFormFields(int index)
    {
        if (!_doc.HasForm || _formFields.TryGet(index, out _) || !_formFieldsLoading.Add(index)) return;
        _ = LoadFormFieldsAsync(index);
    }

    private async Task LoadFormFieldsAsync(int index)
    {
        try
        {
            var doc = _doc;
            var fields = await doc.GetFormFieldsAsync(index);
            if (doc == _doc) _formFields.Add(index, fields);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or PdfException)
        {
        }
        finally
        {
            _formFieldsLoading.Remove(index);
        }
    }

    private FormField? FieldAt(Point p)
    {
        if (!_doc.HasForm || !_laidOut || PageCount == 0) return null;
        int page = PageAtPoint(p);
        if (!_formFields.TryGet(page, out var fields)) return null;
        var (x, y) = ToPageCoords(page, p);
        return fields.LastOrDefault(f => f.Rect.Contains(x, y));
    }

    // ---- Pointer hooks, called first by the viewer's pointer handlers; true when a field took the event ----

    private bool HandleFormPress(PointerRoutedEventArgs e, Point p)
    {
        if (!_doc.HasForm || _tool != MarkupTool.Select || PendingStamp is not null) return false;
        if (FieldAt(p) is not { } field)
        {
            if (_formFocused) _ = LeaveFieldAsync();
            return false;
        }
        int page = field.PageIndex;
        _formPressPage = page;
        _surface.CapturePointer(e.Pointer);
        _ = PressFieldAsync(page, ToPageCoords(page, p), (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0);
        return true;
    }

    private async Task PressFieldAsync(int page, (double X, double Y) at, bool shift)
    {
        // A new field: finish the old one, then take the undo step before anything changes.
        if (_formFocused) await LeaveFieldAsync();
        if (BeforeEdit is not null) await BeforeEdit();
        _formCheckpoint = true;
        _formChanged = false;
        await _doc.FormPointerAsync(page, FormPointer.Down, at.X, at.Y, shift);
        _formFocused = await _doc.FormFocusPageAsync() is not null;
        if (!_formFocused) await FinishFieldEditAsync(); // a check box or button: done with one click
    }

    private bool HandleFormRelease(PointerRoutedEventArgs e, Point p)
    {
        if (_formPressPage is not { } page) return false;
        _formPressPage = null;
        _surface.ReleasePointerCapture(e.Pointer);
        var (x, y) = ToPageCoords(page, p);
        _ = _doc.FormPointerAsync(page, FormPointer.Up, x, y);
        return true;
    }

    private bool HandleFormMove(Point p)
    {
        if (_formPressPage is { } page)
        {
            var (x, y) = ToPageCoords(page, p);
            _ = _doc.FormPointerAsync(page, FormPointer.Move, x, y); // drag-selects text in the field
            return true;
        }
        if (_tool != MarkupTool.Select || PendingStamp is not null || FieldAt(p) is not { } field) return false;
        ProtectedCursor = field.Kind is FormFieldKind.Text ? _fieldTextCursor : _handCursor;
        _textCursorShown = false;
        _overLink = true; // so leaving the field puts the usual cursor back
        return true;
    }

    private bool HandleFormDoubleTap(Point p)
    {
        if (FieldAt(p) is not { } field) return false;
        var (x, y) = ToPageCoords(field.PageIndex, p);
        _ = _doc.FormPointerAsync(field.PageIndex, FormPointer.DoubleClick, x, y); // selects a word
        return true;
    }

    /// <summary>Leaves the focused field, keeping what was typed.</summary>
    private async Task LeaveFieldAsync()
    {
        _formFocused = false;
        await _doc.FormKillFocusAsync();
        await FinishFieldEditAsync();
    }

    /// <summary>Keeps the undo step if the field changed, or drops it; then refreshes thumbnails and field values.</summary>
    private async Task FinishFieldEditAsync()
    {
        if (!_formCheckpoint) return;
        _formCheckpoint = false;
        await Task.Yield(); // lets a pending value-changed notice arrive first
        if (_formChanged)
        {
            for (int page = 0; page < PageCount; page++) _previews.Invalidate(page);
            _formFields.Clear();
        }
        else
        {
            CancelEdit?.Invoke();
        }
        _formChanged = false;
    }

    // ---- Keys, while a field has focus ----

    /// <summary>Keys for the focused field. Tab and Shift+Tab move between fields; Esc leaves the field.</summary>
    private bool HandleFormKey(KeyRoutedEventArgs e)
    {
        if (!_formFocused) return false;
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                _ = LeaveFieldAsync();
                return true;
            case VirtualKey.Tab:
                _ = TabToFieldAsync(shift);
                return true;
            case VirtualKey.Delete or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
                or VirtualKey.Home or VirtualKey.End:
                _ = _doc.FormKeyAsync((int)e.Key, shift, control);
                return true;
            case VirtualKey.V when control:
                _ = PasteIntoFieldAsync();
                return true;
            case VirtualKey.X when control:
                _ = CutFromFieldAsync();
                return true;
            default:
                return false; // characters arrive through CharacterReceived
        }
    }

    private async Task TabToFieldAsync(bool back)
    {
        // Moving to another field finishes this one as its own undo step.
        bool changed = _formChanged;
        await _doc.FormKeyAsync((int)VirtualKey.Tab, back);
        if (changed)
        {
            await FinishFieldEditAsync();
            if (BeforeEdit is not null) await BeforeEdit();
            _formCheckpoint = true;
        }
        _formFocused = await _doc.FormFocusPageAsync() is { } page;
        if (_formFocused && await _doc.FormFocusPageAsync() is { } focusPage && focusPage != CurrentPage)
            GoToPage(focusPage);
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (!_formFocused || e.Character == '\t' || e.Character == 27) return;
        e.Handled = true;
        _ = _doc.FormCharAsync(e.Character);
    }

    public async Task CopyFromFieldAsync()
    {
        string text = await _doc.FormSelectedTextAsync();
        if (text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    public Task SelectAllInFieldAsync() => _doc.FormSelectAllAsync();

    private async Task CutFromFieldAsync()
    {
        await CopyFromFieldAsync();
        await _doc.FormReplaceSelectionAsync("");
    }

    private async Task PasteIntoFieldAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return;
        await _doc.FormReplaceSelectionAsync(await content.GetTextAsync());
    }
}
