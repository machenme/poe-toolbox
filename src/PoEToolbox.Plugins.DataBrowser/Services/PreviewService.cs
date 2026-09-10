using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using LibDat2;
using LibBundle3.Records;
using PoEToolbox.Core.Binary.Datc64;
using PoEToolbox.Plugins.DataBrowser.Models;

namespace PoEToolbox.Plugins.DataBrowser.Services;

public static class PreviewService
{
    private const int MaxTextBytes = 4 * 1024 * 1024;
    private const int MaxHexBytes = 256 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".xml", ".ini", ".csv", ".json", ".hlsl", ".fx", ".vshader", ".pshader",
        ".amd", ".ao", ".arm", ".ecf", ".et", ".gft", ".gt", ".mat", ".pet", ".trl", ".tsi", ".tmo",
    };

    public static Task<PreviewDocument> LoadAsync(FileRecord file, string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Load(file, path, cancellationToken), cancellationToken);
    }

    private static PreviewDocument Load(FileRecord file, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var bytes = file.Read().ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path);

            if (IsDatExtension(extension))
                return LoadDat(bytes, path, extension, cancellationToken);

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                return LoadJson(bytes, path);

            if (TextExtensions.Contains(extension) || LooksLikeText(bytes))
                return LoadText(bytes, path);

            return LoadHex(bytes, path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(path, ex.Message);
        }
    }

    private static PreviewDocument LoadText(byte[] bytes, string path)
    {
        var truncated = bytes.Length > MaxTextBytes;
        var text = DecodeText(truncated ? bytes[..MaxTextBytes] : bytes, out var encoding);
        return new PreviewDocument
        {
            FilePath = path,
            Title = Path.GetFileName(path),
            Kind = PreviewKind.Text,
            Summary = $"文本 · {bytes.Length:N0} bytes" + (truncated ? " · 已截断" : ""),
            Text = text,
            TextEncoding = encoding,
            IsTruncated = truncated,
        };
    }

    private static PreviewDocument LoadJson(byte[] bytes, string path)
    {
        var text = DecodeText(bytes, out var encoding);
        try
        {
            using var json = JsonDocument.Parse(text);
            return new PreviewDocument
            {
                FilePath = path,
                Title = Path.GetFileName(path),
                Kind = PreviewKind.Json,
                Summary = $"JSON · {bytes.Length:N0} bytes",
                Text = text,
                TextEncoding = encoding,
                JsonRoot = [CreateJsonNode("root", json.RootElement)],
            };
        }
        catch (JsonException ex)
        {
            return new PreviewDocument
            {
                FilePath = path,
                Title = Path.GetFileName(path),
                Kind = PreviewKind.Text,
                Summary = $"JSON 解析失败，显示原文 · {bytes.Length:N0} bytes",
                Text = text,
                TextEncoding = encoding,
                ErrorMessage = ex.Message,
            };
        }
    }

    private static PreviewDocument LoadDat(byte[] bytes, string path, string extension, CancellationToken cancellationToken)
    {
        var tableName = Datc64Constants.TableNameFromPath(path);
        var table = extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
            ? LoadLegacyDat(bytes, path, tableName)
            : LoadDatc64(bytes, path, tableName, cancellationToken);

        return new PreviewDocument
        {
            FilePath = path,
            Title = tableName,
            Kind = PreviewKind.DatTable,
            Summary = $"DAT · {table.Rows.Count:N0} rows · {table.Columns.Count:N0} columns",
            Table = table,
        };
    }

    private static DataTable LoadDatc64(byte[] bytes, string path, string tableName, CancellationToken cancellationToken)
    {
        var (is64, validFor) = Datc64File.DetectFromExtension(path);
        var file = Datc64File.FromBytes(bytes, tableName, is64, validFor);
        var table = CreateTable(tableName, file.Columns.Select(c => (c.Name, c.Type)));

        foreach (var sourceRow in file.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = table.NewRow();
            row[0] = table.Rows.Count;
            for (var i = 0; i < file.Columns.Count; i++)
            {
                var column = file.Columns[i];
                row[i + 1] = FormatValue(sourceRow.GetValueOrDefault(column.Name));
            }
            table.Rows.Add(row);
        }

        return table;
    }

    private static DataTable LoadLegacyDat(byte[] bytes, string path, string tableName)
    {
        if (DatContainer.DatDefinitions is null)
            DatContainer.ReloadDefinitions();

        var dat = new DatContainer(bytes, Path.GetFileName(path));
        var table = CreateTable(tableName, dat.FieldDefinitions.Select(c => (c.Key, c.Value)));

        for (var rowIndex = 0; rowIndex < dat.FieldDatas.Count; rowIndex++)
        {
            var row = table.NewRow();
            row[0] = rowIndex;
            var fields = dat.FieldDatas[rowIndex];
            for (var columnIndex = 0; columnIndex < dat.FieldDefinitions.Count; columnIndex++)
                row[columnIndex + 1] = fields is not null && columnIndex < fields.Length
                    ? fields[columnIndex]?.ToString() ?? ""
                    : "";
            table.Rows.Add(row);
        }

        return table;
    }

    private static DataTable CreateTable(string tableName, IEnumerable<(string Name, string Type)> definitions)
    {
        var table = new DataTable(tableName);
        table.Columns.Add("#", typeof(int));

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "#" };
        foreach (var (definition, index) in definitions.Select((value, index) => (value, index)))
        {
            var baseName = string.IsNullOrWhiteSpace(definition.Name) ? $"Column{index}" : definition.Name;
            var columnName = baseName;
            var suffix = 2;
            while (!usedNames.Add(columnName))
                columnName = $"{baseName}_{suffix++}";

            var column = table.Columns.Add(columnName, typeof(string));
            column.Caption = $"{columnName} : {definition.Type}";
        }

        return table;
    }

    private static string FormatValue(object? value)
    {
        if (value is null) return "";
        if (value is string text) return text;
        if (value is System.Collections.IEnumerable enumerable and not byte[])
        {
            var values = enumerable.Cast<object?>().Select(FormatValue);
            return "[" + string.Join(", ", values) + "]";
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static PreviewDocument LoadHex(byte[] bytes, string path)
    {
        var count = Math.Min(bytes.Length, MaxHexBytes);
        var builder = new StringBuilder((count / 16 + 1) * 78);
        for (var offset = 0; offset < count; offset += 16)
        {
            var lineCount = Math.Min(16, count - offset);
            builder.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                if (i < lineCount)
                    builder.Append(bytes[offset + i].ToString("X2", CultureInfo.InvariantCulture));
                else
                    builder.Append("  ");
                if (i == 7) builder.Append("  ");
                else builder.Append(' ');
            }
            builder.Append(" |");
            for (var i = 0; i < lineCount; i++)
            {
                var value = bytes[offset + i];
                builder.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }
            builder.AppendLine("|");
        }

        return new PreviewDocument
        {
            FilePath = path,
            Title = Path.GetFileName(path),
            Kind = PreviewKind.Hex,
            Summary = $"二进制 · {bytes.Length:N0} bytes" + (count < bytes.Length ? $" · 显示前 {count:N0} bytes" : ""),
            HexText = builder.ToString(),
            IsTruncated = count < bytes.Length,
        };
    }

    private static JsonNodeViewModel CreateJsonNode(string name, JsonElement value)
    {
        var node = new JsonNodeViewModel { Name = name, Value = GetJsonValue(value) };
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                node.Children.Add(CreateJsonNode(property.Name, property.Value));
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in value.EnumerateArray())
                node.Children.Add(CreateJsonNode($"[{index++}]", child));
        }
        return node;
    }

    private static string GetJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{ }",
        JsonValueKind.Array => "[ ]",
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.ToString(),
    };

    private static bool IsDatExtension(string extension)
        => extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".dat64", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".datc64", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".datcl64", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeText(byte[] bytes)
    {
        var count = Math.Min(bytes.Length, 4096);
        if (count == 0) return true;
        for (var i = 0; i < count; i++)
            if (bytes[i] == 0) return false;

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes, 0, count);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string DecodeText(byte[] content, out Encoding encoding)
    {
        if (content.Length >= 4 && content[0] == 0xFF && content[1] == 0xFE && content[2] == 0x00 && content[3] == 0x00)
        {
            encoding = new UTF32Encoding(false, true);
            return encoding.GetString(content, 4, content.Length - 4);
        }
        if (content.Length >= 4 && content[0] == 0x00 && content[1] == 0x00 && content[2] == 0xFE && content[3] == 0xFF)
        {
            encoding = new UTF32Encoding(true, true);
            return encoding.GetString(content, 4, content.Length - 4);
        }
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(false, true, true);
            return encoding.GetString(content, 2, content.Length - 2);
        }
        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(true, true, true);
            return encoding.GetString(content, 2, content.Length - 2);
        }
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            encoding = new UTF8Encoding(true);
            return encoding.GetString(content, 3, content.Length - 3);
        }

        encoding = new UTF8Encoding(false);
        return encoding.GetString(content);
    }

    private static PreviewDocument Error(string path, string message) => new()
    {
        FilePath = path,
        Title = Path.GetFileName(path),
        Kind = PreviewKind.Error,
        Summary = "预览失败",
        ErrorMessage = message,
    };
}
