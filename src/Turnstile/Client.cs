namespace Turnstile;

using System.Net.Http.Headers;
using System.Net.Sockets;

/// <summary>
/// The thin client. Every invocation is a fresh, cold process: connect to the daemon's Unix socket,
/// issue one request, print, exit. It holds no state — identity and coordination live in the daemon.
/// </summary>
internal static class Client
{
    public static async Task<int> RunAsync(string verb, string[] args)
    {
        string socket = Cli.OptionValue(args, "--socket") ?? Paths.DefaultSocket;

        try
        {
            using HttpClient http = CreateClient(socket);
            return verb switch
            {
                "status" => await StatusAsync(http),
                "get" => await GetAsync(http, args),
                "create" => await CreateAsync(http, args),
                "put" => await PutAsync(http, args),
                "delete" => await DeleteAsync(http, args),
                "lease" => await LeaseAsync(http, args),
                "txn" => await TxnAsync(http, args),
                "watch" => NotYet(verb),
                _ => Unknown(verb),
            };
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            Console.Error.WriteLine($"turnstile: cannot reach daemon at {socket} (is 'turnstile serve' running?)");
            return 1;
        }
    }

    private static async Task<int> StatusAsync(HttpClient http)
    {
        HttpResponseMessage res = await http.GetAsync("/status");
        return await Emit(res);
    }

    private static async Task<int> GetAsync(HttpClient http, string[] args)
    {
        if (Positional(args) is not string key)
        {
            return MissingKey();
        }

        HttpResponseMessage res = await http.GetAsync($"/kv{key}");
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.Error.WriteLine($"turnstile: key not found: {key}");
            return 1;
        }

        byte[] body = await res.Content.ReadAsByteArrayAsync();
        using Stream stdout = Console.OpenStandardOutput();
        await stdout.WriteAsync(body);
        return res.IsSuccessStatusCode ? 0 : 1;
    }

    private static async Task<int> CreateAsync(HttpClient http, string[] args)
    {
        if (Positional(args) is not string key)
        {
            return MissingKey();
        }

        string query = BuildQuery(
            ("immutable", Cli.HasFlag(args, "--immutable") ? "true" : null),
            ("lease", Cli.OptionValue(args, "--lease")));

        using HttpContent content = await BodyContent(args);
        HttpResponseMessage res = await http.PostAsync($"/kv{key}{query}", content);
        return await Emit(res);
    }

    private static async Task<int> PutAsync(HttpClient http, string[] args)
    {
        if (Positional(args) is not string key)
        {
            return MissingKey();
        }

        string query = BuildQuery(("unconditional", Cli.HasFlag(args, "--unconditional") ? "true" : null));
        using HttpContent content = await BodyContent(args);
        var req = new HttpRequestMessage(HttpMethod.Put, $"/kv{key}{query}") { Content = content };
        if (Cli.OptionValue(args, "--if-match") is string rev)
        {
            req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{rev}\""));
        }

        HttpResponseMessage res = await http.SendAsync(req);
        return await Emit(res);
    }

    private static async Task<int> DeleteAsync(HttpClient http, string[] args)
    {
        if (Positional(args) is not string key)
        {
            return MissingKey();
        }

        string query = BuildQuery(("unconditional", Cli.HasFlag(args, "--unconditional") ? "true" : null));
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/kv{key}{query}");
        if (Cli.OptionValue(args, "--if-match") is string rev)
        {
            req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{rev}\""));
        }

        HttpResponseMessage res = await http.SendAsync(req);
        return await Emit(res);
    }

    private static async Task<int> TxnAsync(HttpClient http, string[] args)
    {
        // The txn body is JSON (compare/success/failure). Read it from --file or stdin and pass it
        // straight through — the client stays ignorant of the protocol so callers can build any txn.
        string json;
        if (Cli.OptionValue(args, "--file") is string path)
        {
            json = await File.ReadAllTextAsync(path);
        }
        else
        {
            using var reader = new StreamReader(Console.OpenStandardInput());
            json = await reader.ReadToEndAsync();
        }

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await Emit(await http.PostAsync("/txn", content));
    }

    private static async Task<int> LeaseAsync(HttpClient http, string[] args)
    {
        string? sub = Positional(args);
        switch (sub)
        {
            case "create":
                string ttl = Cli.OptionValue(args, "--ttl") ?? "2700";
                using (var body = new StringContent($"{{\"ttl\":{ttl}}}", System.Text.Encoding.UTF8, "application/json"))
                {
                    return await Emit(await http.PostAsync("/lease", body));
                }

            case "keepalive":
                return await Emit(await http.PutAsync($"/lease/{LeaseId(args)}", null));

            case "revoke":
                return await Emit(await http.DeleteAsync($"/lease/{LeaseId(args)}"));

            case "get":
                return await Emit(await http.GetAsync($"/lease/{LeaseId(args)}"));

            default:
                Console.Error.WriteLine("turnstile: usage: turnstile lease <create|keepalive|revoke|get> [id] [--ttl N]");
                return 2;
        }
    }

    private static string LeaseId(string[] args)
    {
        // The subcommand is the first positional; the lease id is the second.
        bool seenSub = false;
        foreach (string arg in args)
        {
            if (arg.StartsWith('-'))
            {
                continue;
            }

            if (!seenSub)
            {
                seenSub = true;
                continue;
            }

            return arg;
        }

        return string.Empty;
    }

    private static async Task<HttpContent> BodyContent(string[] args)
    {
        byte[] body;
        if (Cli.OptionValue(args, "--value") is string value)
        {
            body = System.Text.Encoding.UTF8.GetBytes(value);
        }
        else
        {
            using Stream stdin = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            await stdin.CopyToAsync(ms);
            body = ms.ToArray();
        }

        return new ByteArrayContent(body);
    }

    private static async Task<int> Emit(HttpResponseMessage res)
    {
        string body = await res.Content.ReadAsStringAsync();
        if (res.IsSuccessStatusCode)
        {
            if (body.Length > 0)
            {
                Console.WriteLine(body);
            }

            return 0;
        }

        Console.Error.WriteLine($"turnstile: {(int)res.StatusCode} {res.StatusCode}{(body.Length > 0 ? " " + body : string.Empty)}");
        return 1;
    }

    private static string BuildQuery(params (string Name, string? Value)[] parameters)
    {
        List<string> parts = [];
        foreach ((string name, string? value) in parameters)
        {
            if (value is not null)
            {
                parts.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    private static string? Positional(string[] args)
    {
        foreach (string arg in args)
        {
            if (!arg.StartsWith('-'))
            {
                return arg;
            }
        }

        return null;
    }

    private static int MissingKey()
    {
        Console.Error.WriteLine("turnstile: a key is required (must begin with '/')");
        return 2;
    }

    private static int NotYet(string verb)
    {
        Console.Error.WriteLine($"turnstile: '{verb}' is not implemented yet");
        return 2;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"turnstile: unknown command '{verb}'");
        return 2;
    }

    private static HttpClient CreateClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };

        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }
}
