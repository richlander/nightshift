namespace Nightshift.Tests;

using System.Text.Json;
using Nightshift.Commands;
using Xunit;

/// <summary>Pure projection tests for <see cref="Plan"/> — no daemon, just JSON in / keys + spec out.</summary>
public class PlanTests
{
    private const string Sha = "deadbeefcafe1234";

    [Fact]
    public void Parse_DerivesOrderBaseAndReadyKey()
    {
        Plan plan = Plan.Parse("""{ "plan": "1234", "orders": [ { "order": "op4" } ] }""", Sha);

        Assert.Equal("1234", plan.PlanId);
        Order order = Assert.Single(plan.Orders);
        Assert.Equal("op4", order.Id);
        Assert.Equal("/plan/1234/order/op4", order.Base);
        Assert.Equal("/ready/1234/op4", plan.ReadyKey(order));
    }

    [Fact]
    public void Parse_AfterAcceptsNumbersAndStrings()
    {
        Plan plan = Plan.Parse(
            """{ "plan": "p", "orders": [ { "order": "x", "after": [4, "y"] } ] }""", Sha);

        Order order = Assert.Single(plan.Orders);
        Assert.Equal(["4", "y"], order.After);
    }

    [Fact]
    public void Parse_InheritsPlanStandard_OrderOverrides()
    {
        Plan plan = Plan.Parse(
            """
            { "plan": "p", "standard": "docs/base.md",
              "orders": [ { "order": "a" }, { "order": "b", "standard": "docs/override.md#2" } ] }
            """, Sha);

        Assert.Equal("docs/base.md", SpecField(plan.Orders[0], "standard"));
        Assert.Equal("docs/override.md#2", SpecField(plan.Orders[1], "standard"));
    }

    [Fact]
    public void Parse_SpecCarriesPointerFields()
    {
        Plan plan = Plan.Parse(
            """
            { "plan": "1234",
              "orders": [ { "order": "op4", "issue": 1238, "title": "Retain outcomes",
                            "paths": ["src/A.cs"], "after": ["op2"] } ] }
            """, Sha);

        using JsonDocument doc = JsonDocument.Parse(plan.Orders[0].SpecJson);
        JsonElement root = doc.RootElement;
        Assert.Equal("1234", root.GetProperty("plan").GetString());
        Assert.Equal("op4", root.GetProperty("order").GetString());
        Assert.Equal(Sha, root.GetProperty("order_sha").GetString());
        Assert.Equal("1238", root.GetProperty("issue").GetString());
        Assert.Equal("Retain outcomes", root.GetProperty("title").GetString());
        Assert.Equal("src/A.cs", root.GetProperty("paths")[0].GetString());
        Assert.Equal("op2", root.GetProperty("after")[0].GetString());
    }

    [Fact]
    public void Parse_OmitsOrderShaWhenBlank()
    {
        Plan plan = Plan.Parse("""{ "plan": "p", "orders": [ { "order": "a" } ] }""", string.Empty);

        using JsonDocument doc = JsonDocument.Parse(plan.Orders[0].SpecJson);
        Assert.False(doc.RootElement.TryGetProperty("order_sha", out _));
    }

    [Fact]
    public void Parse_MissingPlan_Throws()
        => Assert.Throws<InvalidDataException>(() => Plan.Parse("""{ "orders": [] }""", Sha));

    [Fact]
    public void Parse_MissingOrderId_Throws()
        => Assert.Throws<InvalidDataException>(
            () => Plan.Parse("""{ "plan": "p", "orders": [ { "title": "no id" } ] }""", Sha));

    private static string? SpecField(Order order, string name)
    {
        using JsonDocument doc = JsonDocument.Parse(order.SpecJson);
        return doc.RootElement.TryGetProperty(name, out JsonElement v) ? v.GetString() : null;
    }
}
