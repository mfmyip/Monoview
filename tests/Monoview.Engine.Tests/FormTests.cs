namespace Monoview.Engine.Tests;

public class FormTests
{
    private const int VkTab = 0x09;

    private static Task<PdfDocument> OpenForm() => PdfDocument.OpenAsync(FormPdf.Path);

    private static (double X, double Y) Middle(NormRect r) => ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);

    private static async Task Click(PdfDocument doc, NormRect r)
    {
        var (x, y) = Middle(r);
        await doc.FormPointerAsync(0, FormPointer.Down, x, y);
        await doc.FormPointerAsync(0, FormPointer.Up, x, y);
    }

    private static async Task Type(PdfDocument doc, string text)
    {
        foreach (char c in text) await doc.FormCharAsync(c);
    }

    private static async Task<FormField> Field(PdfDocument doc, string name) =>
        (await doc.GetFormFieldsAsync(0)).Single(f => f.Name == name);

    [Fact]
    public async Task Lists_the_fields_of_a_form()
    {
        using var doc = await OpenForm();
        Assert.True(doc.HasForm);
        var fields = await doc.GetFormFieldsAsync(0);
        Assert.Equal([("name", FormFieldKind.Text), ("agree", FormFieldKind.CheckBox), ("color", FormFieldKind.ComboBox)],
            fields.Select(f => (f.Name, f.Kind)));
        Assert.Equal("Red", fields[2].Value);
        Assert.False(fields[1].IsChecked);
    }

    [Fact]
    public async Task Documents_without_a_form_have_none()
    {
        using var doc = await PdfDocument.OpenAsync(SamplePdf.Path);
        Assert.False(doc.HasForm);
        Assert.Empty(await doc.GetFormFieldsAsync(0));
    }

    [Fact]
    public async Task Typing_fills_a_text_field_and_marks_the_document_changed()
    {
        using var doc = await OpenForm();
        await Click(doc, FormPdf.NameField);
        Assert.Equal(0, await doc.FormFocusPageAsync());
        await Type(doc, "Adaa"); // Backspace arrives as a typed character, as Windows sends it
        await doc.FormKillFocusAsync();
        Assert.Equal("Ada", (await Field(doc, "name")).Value);
        Assert.True(doc.IsModified);
        Assert.Null(await doc.FormFocusPageAsync());
    }

    [Fact]
    public async Task Clicking_a_check_box_checks_it()
    {
        using var doc = await OpenForm();
        await Click(doc, FormPdf.AgreeBox);
        Assert.True((await Field(doc, "agree")).IsChecked);
        await Click(doc, FormPdf.AgreeBox);
        Assert.False((await Field(doc, "agree")).IsChecked);
    }

    [Fact]
    public async Task Clicking_another_field_keeps_the_typed_text_and_acts_on_the_new_field()
    {
        using var doc = await OpenForm();
        await doc.RenderAsync(0, 612, 792);
        await Click(doc, FormPdf.NameField);
        await Type(doc, "Grace");
        await Click(doc, FormPdf.AgreeBox);
        Assert.Equal("Grace", (await Field(doc, "name")).Value);
        Assert.True((await Field(doc, "agree")).IsChecked);
    }

    [Fact]
    public async Task Tab_moves_to_the_next_field()
    {
        using var doc = await OpenForm();
        await Click(doc, FormPdf.NameField);
        Assert.True(await doc.FormKeyAsync(VkTab), "Tab as a key");
        await Type(doc, " "); // Space toggles the check box Tab moved to
        Assert.True((await Field(doc, "agree")).IsChecked);
    }

    [Fact]
    public async Task Typed_text_is_drawn_and_survives_save()
    {
        string path = Path.Combine(Path.GetTempPath(), $"monoview-form-saved-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = await OpenForm())
            {
                await Click(doc, FormPdf.NameField);
                await Type(doc, "Grace Hopper");
                var bmp = await doc.RenderAsync(0, 612, 792);
                Assert.True(DarkPixels(bmp, FormPdf.NameField) > 40, "typed text shows while typing");
                await doc.SaveCopyAsync(path);
                await doc.CloseAsync();
            }
            using var saved = await PdfDocument.OpenAsync(path);
            Assert.Equal("Grace Hopper", (await Field(saved, "name")).Value);
            Assert.True(DarkPixels(await saved.RenderAsync(0, 612, 792), FormPdf.NameField) > 40, "saved file shows the text");
            await saved.CloseAsync();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Clearly dark pixels inside the box, leaving out its border.</summary>
    private static int DarkPixels(RenderedBitmap bmp, NormRect box)
    {
        int n = 0;
        for (int y = (int)(box.Top * bmp.Height) + 3; y < (int)(box.Bottom * bmp.Height) - 3; y++)
            for (int x = (int)(box.Left * bmp.Width) + 3; x < (int)(box.Right * bmp.Width) - 3; x++)
            {
                int i = (y * bmp.Width + x) * 4;
                if (bmp.Bgra[i] + bmp.Bgra[i + 1] + bmp.Bgra[i + 2] < 300) n++;
            }
        return n;
    }
}
