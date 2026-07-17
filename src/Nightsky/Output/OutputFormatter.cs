namespace Nightsky.Output;

using System.CommandLine;

internal static class OutputFormatter
{
    public static Option<OutputFormat> CreateOutputOption()
    {
        var option = new Option<OutputFormat>("--output")
        {
            Description = "Output mode: table, json, or jsonl.",
        };
        option.DefaultValueFactory = _ => OutputFormat.Table;
        option.Validators.Add(result =>
        {
            foreach (var token in result.Tokens)
            {
                if (!Enum.TryParse<OutputFormat>(token.Value, ignoreCase: true, out var value)
                    || !Enum.IsDefined(typeof(OutputFormat), value))
                {
                    result.AddError("--output must be one of table|json|jsonl");
                    break;
                }
            }
        });
        return option;
    }
}
