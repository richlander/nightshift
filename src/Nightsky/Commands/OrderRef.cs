namespace Nightsky.Commands;

internal readonly record struct OrderRef(string Plan, string Order)
{
    public string Base => $"/plan/{Plan}/order/{Order}";

    public string ReadyKey => $"/ready/{Plan}/{Order}";

    public static OrderRef? FromBase(string? orderBase)
    {
        if (orderBase is null)
        {
            return null;
        }

        string[] segments = orderBase.Split('/');
        if (segments.Length != 5 || segments[0].Length != 0 || segments[1] != "plan" || segments[3] != "order"
            || segments[2].Length == 0 || segments[4].Length == 0)
        {
            return null;
        }

        return new OrderRef(segments[2], segments[4]);
    }

    public static OrderRef? FromReadyKey(string? readyKey)
    {
        if (readyKey is null)
        {
            return null;
        }

        string[] segments = readyKey.Split('/');
        if (segments.Length != 4 || segments[0].Length != 0 || segments[1] != "ready"
            || segments[2].Length == 0 || segments[3].Length == 0)
        {
            return null;
        }

        return new OrderRef(segments[2], segments[3]);
    }
}
