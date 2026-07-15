namespace Nightshift.Output;

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Markout;
using Markout.Formatting;

/// <summary>Shared output option and Markout/STJ render dispatch for board-style commands.</summary>
internal static class OutputFormatter
{
    public static Option<OutputFormat> CreateOutputOption()
    {
        var option = new Option<OutputFormat>("--output")
        {
            Description = "Output format: plaintext, table, markdown, json, jsonl, or tsv.",
        };
        option.DefaultValueFactory = _ => OutputFormat.Plaintext;
        option.Validators.Add(result =>
        {
            foreach (var token in result.Tokens)
            {
                if (!Enum.TryParse<OutputFormat>(token.Value, ignoreCase: true, out var value)
                    || !Enum.IsDefined(typeof(OutputFormat), value))
                {
                    result.AddError("--output must be one of plaintext|table|markdown|json|jsonl|tsv");
                    break;
                }
            }
        });
        return option;
    }

    public static Option<OutputFormat> CreateWatchOutputOption()
    {
        var option = new Option<OutputFormat>("--output")
        {
            Description = "Output mode: table (live redraw) or jsonl (one row per change).",
        };
        option.DefaultValueFactory = _ => OutputFormat.Table;
        option.Validators.Add(result =>
        {
            foreach (var token in result.Tokens)
            {
                if (!Enum.TryParse<OutputFormat>(token.Value, ignoreCase: true, out var value)
                    || (value != OutputFormat.Table && value != OutputFormat.Jsonl))
                {
                    result.AddError("--output must be one of table|jsonl");
                    break;
                }
            }
        });
        return option;
    }

    public static void Render<TView, TJson>(
        TView view,
        TJson jsonValue,
        TextWriter output,
        OutputFormat format,
        MarkoutSerializerContext markoutContext,
        JsonTypeInfo<TJson> jsonTypeInfo)
    {
        switch (format)
        {
            case OutputFormat.Json:
                output.WriteLine(JsonSerializer.Serialize(jsonValue, jsonTypeInfo));
                break;
            case OutputFormat.Plaintext:
                MarkoutSerializer.Serialize(view, output, new PlainTextFormatter(), markoutContext);
                break;
            case OutputFormat.Markdown:
                MarkoutSerializer.Serialize(view, output, new MarkdownFormatter(), markoutContext);
                break;
            case OutputFormat.Table:
                MarkoutSerializer.Serialize(view, output, new TableFormatter(showHeader: true), markoutContext);
                break;
            case OutputFormat.Jsonl:
                MarkoutSerializer.Serialize(
                    view,
                    output,
                    new TableFormatter(showHeader: true),
                    markoutContext,
                    new MarkoutWriterOptions { TableMode = MarkoutTableMode.Jsonl });
                break;
            case OutputFormat.Tsv:
                MarkoutSerializer.Serialize(
                    view,
                    output,
                    new TableFormatter(showHeader: false),
                    markoutContext,
                    new MarkoutWriterOptions { TableMode = MarkoutTableMode.Tsv });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }
}
