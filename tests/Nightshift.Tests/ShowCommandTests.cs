namespace Nightshift.Tests;

using Nightshift.Commands;
using Nightshift.Output;
using Xunit;

/// <summary>
/// The multi-format rendering behind <c>nightshift show</c>: plaintext stays the WORK packet verbatim (the
/// recovery contract), while <c>--output</c> decomposes the same order into structured [field, value] rows.
/// </summary>
public class ShowCommandTests
{
    private const string Spec =
        """
        { "title": "Retain outcomes", "issue": "1238", "paths": ["src/A.cs", "src/B.cs"],
          "standard": "docs/std.md", "brief": "do the thing", "order_sha": "abc123" }
        """;

    [Fact]
    public void Render_Plaintext_IsWorkPacketVerbatim()
    {
        OrderView view = OrderView.Parse(Spec);

        using var actual = new StringWriter();
        ShowCommand.Render(view, "/plan/9001/order/op4", fence: 7, OutputFormat.Plaintext, actual);

        using var expected = new StringWriter();
        view.PrintWork(expected, "/plan/9001/order/op4", fence: 7);

        Assert.Equal(expected.ToString(), actual.ToString());
        Assert.StartsWith("WORK /plan/9001/order/op4", actual.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFields_MirrorsWorkPacketOrderAndOmitsAbsentFields()
    {
        OrderView view = OrderView.Parse(Spec);

        List<OrderField> fields = ShowCommand.BuildFields(view, "/plan/9001/order/op4", fence: 7);

        Assert.Equal(
            ["base", "branch", "title", "issue", "paths", "standard", "brief", "order_sha", "fence"],
            fields.Select(f => f.Field));
        Assert.Equal("/plan/9001/order/op4", fields[0].Value);
        Assert.Equal("nightshift/9001/op4", fields[1].Value);
        Assert.Equal("src/A.cs, src/B.cs", fields.Single(f => f.Field == "paths").Value);
        Assert.Equal("7", fields[^1].Value);
        Assert.DoesNotContain(fields, f => f.Field == "supersedes");
    }

    [Fact]
    public void Render_Json_EmitsStructuredRows()
    {
        OrderView view = OrderView.Parse("{ \"title\": \"T\" }");

        using var writer = new StringWriter();
        ShowCommand.Render(view, "/plan/1/order/op-a", fence: 3, OutputFormat.Json, writer);

        Assert.Equal(
            "[{\"field\":\"base\",\"value\":\"/plan/1/order/op-a\"},"
            + "{\"field\":\"branch\",\"value\":\"nightshift/1/op-a\"},"
            + "{\"field\":\"title\",\"value\":\"T\"},"
            + "{\"field\":\"fence\",\"value\":\"3\"}]\n",
            writer.ToString());
    }

    [Fact]
    public void Render_Tsv_EmitsFieldValueBytes()
    {
        OrderView view = OrderView.Parse("{ \"title\": \"T\" }");

        using var writer = new StringWriter();
        ShowCommand.Render(view, "/plan/1/order/op-a", fence: 3, OutputFormat.Tsv, writer);

        Assert.Equal(
            "base\t/plan/1/order/op-a\n"
            + "branch\tnightshift/1/op-a\n"
            + "title\tT\n"
            + "fence\t3\n",
            writer.ToString());
    }
}
