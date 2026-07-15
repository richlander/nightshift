namespace Nightshift.Tests;

using System.Text;
using Nightshift.Commands;
using Xunit;

/// <summary>The WORK-packet renderer shared by next/show — parse tolerance and stable field ordering.</summary>
public class OrderViewTests
{
    [Fact]
    public void PrintWork_EmitsPresentFieldsThenFence()
    {
        OrderView view = OrderView.Parse(
            """
            { "title": "Retain outcomes", "issue": "1238", "paths": ["src/A.cs", "src/B.cs"],
              "standard": "docs/std.md", "brief": "do the thing", "order_sha": "abc123" }
            """);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        view.PrintWork(writer, "/plan/9001/order/op4", fence: 7);

        string output = sb.ToString();
        string[] lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("WORK /plan/9001/order/op4", lines[0]);
        Assert.Equal("branch: nightshift/9001/op4", lines[1]);
        Assert.Equal("title: Retain outcomes", lines[2]);
        Assert.Equal("issue: 1238", lines[3]);
        Assert.Equal("paths: src/A.cs, src/B.cs", lines[4]);
        Assert.Equal("fence: 7", lines[^1]);
        Assert.DoesNotContain("supersedes:", output); // absent fields are omitted
    }

    [Fact]
    public void Parse_MalformedJson_YieldsEmpty()
    {
        OrderView view = OrderView.Parse("not json");

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        view.PrintWork(writer, "/plan/p/order/x", fence: 1);

        // Empty view prints only the header, the derived branch, and the fence — no field lines.
        string[] lines = sb.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["WORK /plan/p/order/x", "branch: nightshift/p/x", "fence: 1"], lines);
    }
}
