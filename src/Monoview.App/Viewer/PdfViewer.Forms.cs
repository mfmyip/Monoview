using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
            if (!_formFocused) return false;
            // An open drop-down list hangs below its field, outside every field's box: the form gets the click first.
            int listPage = PageAtPoint(p);
            _formPressPage = listPage;
            _surface.CapturePointer(e.Pointer);
            _ = PressOutsideFieldAsync(listPage, ToPageCoords(listPage, p));
            return true;
        }
        int page = field.PageIndex;
        var box = FieldBox(field);
        // A drop-down list opens as a menu: anywhere on it, or on its arrow end when you can also type in it.
        if (field is { Kind: FormFieldKind.ComboBox, Options.Count: > 0 } && (!field.Editable || DropButtonBox(field).Contains(p)))
        {
            ShowChoices(field, box);
            return true;
        }
        _formPressPage = page;
        _surface.CapturePointer(e.Pointer);
        _ = PressFieldAsync(page, ToPageCoords(page, p), (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0);
        return true;
    }

    /// <summary>Where a field is on the surface.</summary>
    private Rect FieldBox(FormField field)
    {
        int page = field.PageIndex;
        var r = Monoview.Engine.Rotation.ToView(field.Rect, _rotation);
        return new Rect(_left[page] + r.Left * _w[page], _top[page] + r.Top * _h[page], r.Width * _w[page], r.Height * _h[page]);
    }

    /// <summary>The drop-down button at a drop-down list's right end: the form engine draws it 13 points wide, inside the field's border.</summary>
    private Rect DropButtonBox(FormField field)
    {
        var box = FieldBox(field);
        double width = Math.Min(box.Width, 15 * _w[field.PageIndex] / ShownSize(field.PageIndex).Width);
        return new Rect(box.Right - width, box.Top, width, box.Height);
    }

    /// <summary>A drop-down list's choices as a menu under it, the current one checked.</summary>
    private void ShowChoices(FormField field, Rect box)
    {
        var menu = new MenuFlyout
        {
            MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter))
            {
                Setters = { new Setter(MinWidthProperty, box.Width * _scroller.ZoomFactor) },
            },
        };
        for (int i = 0; i < field.Options!.Count; i++)
        {
            int option = i;
            var item = new ToggleMenuFlyoutItem { Text = field.Options[i], IsChecked = field.Options[i] == field.Value };
            item.Click += (_, _) => _ = ChooseAsync(field, option);
            menu.Items.Add(item);
        }
        menu.ShowAt(_surface, new FlyoutShowOptions { Position = new Point(box.Left, box.Bottom), Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    /// <summary>Sets a drop-down list to a choice, as one undo step.</summary>
    private async Task ChooseAsync(FormField field, int option)
    {
        if (_formFocused) await LeaveFieldAsync();
        if (BeforeEdit is not null) await BeforeEdit();
        _formCheckpoint = true;
        _formChanged = false;
        await _doc.FormChooseAsync(field.PageIndex, field.AnnotIndex, option);
        await FinishFieldEditAsync();
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

    /// <summary>A choice in an open drop-down list; anywhere else leaves the field.</summary>
    private async Task PressOutsideFieldAsync(int page, (double X, double Y) at)
    {
        if (!await _doc.FormPointerAsync(page, FormPointer.Down, at.X, at.Y)) await LeaveFieldAsync();
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
        if (_tool != MarkupTool.Select || PendingStamp is not null) return false;
        var field = FieldAt(p);
        // With a field focused, a click elsewhere goes to the form (an open drop-down list), not to text selection.
        if (field is null && !_formFocused) return false;
        ProtectedCursor = field is null ? _arrowCursor : field.Kind is FormFieldKind.Text ? _fieldTextCursor : _handCursor;
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
            UpdateViewport(requestTiles: false); // reloads the fields on screen, or clicks on them would miss until a scroll
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
