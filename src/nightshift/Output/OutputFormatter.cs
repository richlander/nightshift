namespace Nightshift.Output;

using System.CommandLine;
using System.Text;
using System.Text.Json;
using Markout;
using Markout.Formatting;

/// <summary>Shared table renderer for Nightshift board/read commands.</summary>
internal static class OutputFormatter
{
    public static Option<OutputFormat> CreateOutputOption()
    {
        var output = new Option<OutputFormat>("--output")
        {
            Description = "Output format: plaintext, table, markdown, json, jsonl, or tsv.",
        };
        output.DefaultValueFactory = _ => OutputFormat.Plaintext;
        return output;
    }

    public static void WriteTable(TextWriter output, OutputFormat format, OutputTable table)
    {
        switch (format)
        {
            case OutputFormat.Json:
                WriteJson(output, table);
                break;
            case OutputFormat.Jsonl:
                WriteMarkoutTable(output, table, new TableFormatter(showHeader: false), MarkoutTableMode.Jsonl);
                break;
            case OutputFormat.Tsv:
                WriteMarkoutTable(output, table, new TableFormatter(showHeader: false), MarkoutTableMode.Tsv);
                break;
            case OutputFormat.Markdown:
                WriteMarkoutTable(output, table, new MarkdownFormatter(), tableMode: null);
                break;
            case OutputFormat.Table:
                WriteMarkoutTable(output, table, new TableFormatter(showHeader: true), tableMode: null);
                break;
            case OutputFormat.Plaintext:
            default:
                WriteMarkoutTable(output, table, new PlainTextFormatter(), tableMode: null);
                break;
        }
    }

    public static string RenderTable(OutputFormat format, OutputTable table)
    {
        using var writer = new StringWriter();
        WriteTable(writer, format, table);
        return writer.ToString();
    }

    private static void WriteMarkoutTable(
        TextWriter output,
        OutputTable table,
        IMarkoutFormatter formatter,
        MarkoutTableMode? tableMode)
    {
        var options = new MarkoutWriterOptions();
        if (tableMode is { } mode)
            options.TableMode = mode;

        var markoutWriter = new MarkoutWriter(output, formatter, options);
        markoutWriter.WriteTable(
            table.Columns.Select(column => column.Header).ToArray(),
            table.Columns.Select(column => column.Field).ToArray(),
            table.Rows.Select(row => row.ToArray()).ToArray());
        markoutWriter.Flush();
    }

    private static void WriteJson(TextWriter output, OutputTable table)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (IReadOnlyList<string> row in table.Rows)
            {
                WriteJsonObject(writer, table.Columns, row);
            }

            writer.WriteEndArray();
        }

        output.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteJsonObject(Utf8JsonWriter writer, IReadOnlyList<OutputColumn> columns, IReadOnlyList<string> row)
    {
        writer.WriteStartObject();
        for (int i = 0; i < columns.Count; i++)
        {
            string value = i < row.Count ? row[i] : string.Empty;
            writer.WriteString(columns[i].Field, value);
        }

        writer.WriteEndObject();
    }
}
