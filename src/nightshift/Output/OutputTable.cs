namespace Nightshift.Output;

/// <summary>A caller-owned table shape: display headers, stable field names, and rows.</summary>
internal sealed record OutputTable(IReadOnlyList<OutputColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>One output table column with a human header and stable machine field name.</summary>
internal sealed record OutputColumn(string Header, string Field);
