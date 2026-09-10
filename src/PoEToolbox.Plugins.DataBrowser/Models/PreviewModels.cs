using System.Collections.ObjectModel;
using System.Data;
using System.Text;

namespace PoEToolbox.Plugins.DataBrowser.Models;

public enum PreviewKind
{
    Empty,
    Text,
    Json,
    DatTable,
    Hex,
    Error,
}

public sealed class PreviewDocument
{
    public string FilePath { get; init; } = "";
    public string Title { get; init; } = "";
    public PreviewKind Kind { get; init; }
    public string Summary { get; init; } = "";
    public string? Text { get; init; }
    public Encoding TextEncoding { get; init; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    public string? HexText { get; init; }
    public DataTable? Table { get; init; }
    public ObservableCollection<JsonNodeViewModel>? JsonRoot { get; init; }
    public bool IsTruncated { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record PendingTextEdit(
    string VirtualPath,
    string OriginalText,
    string EditedText,
    Encoding Encoding);

public sealed class JsonNodeViewModel
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public ObservableCollection<JsonNodeViewModel> Children { get; } = [];
    public bool HasChildren => Children.Count > 0;
}
