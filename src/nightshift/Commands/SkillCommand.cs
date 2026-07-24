namespace Nightshift.Commands;

using System.Reflection;

/// <summary>
/// <c>nightshift skill [role]</c> — print a packaged Nightshift skill straight from the binary. With no
/// argument it prints the general orientation (what Nightshift is, the roles, how they fit together);
/// with a role it prints that role's operating skill. The skills are the same <c>.github/skills</c> bytes
/// the marketplace ships, embedded as manifest resources; this command reads them out and strips the YAML
/// frontmatter so the body prints clean. It needs no Turnstile socket — it never touches the coordination
/// path. Unknown role → one-line error on stderr and the usage exit code.
/// </summary>
internal static class SkillCommand
{
    private const string GeneralKey = "nightshift";

    /// <summary>
    /// Skill token → embedded resource logical name. <see cref="GeneralKey"/> is the general orientation
    /// (the no-argument default); the rest are the role operating skills. Keep in sync with the
    /// <c>EmbeddedResource</c> entries in <c>nightshift.csproj</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Skills = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GeneralKey] = "nightshift.skill.nightshift.md",
        // A Planner is a Coordinator scoped to registering work, so it shares the coordinator skill.
        ["planner"] = "nightshift.skill.coordinator.md",
        ["coordinator"] = "nightshift.skill.coordinator.md",
        ["worker"] = "nightshift.skill.worker.md",
        ["builder"] = "nightshift.skill.builder.md",
        ["reviewer"] = "nightshift.skill.reviewer.md",
    };

    /// <summary>The role skills a caller can request by name (the general orientation is the no-arg default).</summary>
    internal static readonly IReadOnlyList<string> Roles = ["planner", "coordinator", "worker", "builder", "reviewer"];

    public static Task<int> RunAsync(string? role)
    {
        string key = string.IsNullOrEmpty(role) ? GeneralKey : role;
        if (!Skills.TryGetValue(key, out string? resource))
        {
            Console.Error.WriteLine($"nightshift skill: unknown role '{role}' (choose one of: {string.Join(", ", Roles)}).");
            return Task.FromResult(ExitCode.Usage);
        }

        Console.Out.Write(LoadSkill(resource));
        return Task.FromResult(ExitCode.Ok);
    }

    /// <summary>Reads an embedded skill resource and returns its body with the YAML frontmatter stripped.</summary>
    internal static string LoadSkill(string resourceName)
    {
        Assembly assembly = typeof(SkillCommand).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded skill resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return StripFrontmatter(reader.ReadToEnd());
    }

    /// <summary>
    /// Removes a leading YAML frontmatter block (a <c>---</c> line, content, a closing <c>---</c> line) and any
    /// blank lines that follow it. A <c>---</c> horizontal rule inside the body is left untouched, and content
    /// without a well-formed frontmatter block is returned unchanged.
    /// </summary>
    internal static string StripFrontmatter(string content)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal) && !content.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            return content;
        }

        // Start scanning at the line after the opening delimiter.
        int index = content.IndexOf('\n', StringComparison.Ordinal) + 1;
        while (index < content.Length)
        {
            int lineEnd = content.IndexOf('\n', index);
            int nextLine = lineEnd < 0 ? content.Length : lineEnd + 1;
            ReadOnlySpan<char> line = content.AsSpan(index, (lineEnd < 0 ? content.Length : lineEnd) - index).TrimEnd('\r');
            if (line.SequenceEqual("---"))
            {
                return content[SkipBlankLines(content, nextLine)..];
            }

            index = nextLine;
        }

        // No closing delimiter: not a real frontmatter block, so print the content verbatim.
        return content;
    }

    private static int SkipBlankLines(string content, int index)
    {
        while (index < content.Length)
        {
            int lineEnd = content.IndexOf('\n', index);
            int nextLine = lineEnd < 0 ? content.Length : lineEnd + 1;
            ReadOnlySpan<char> line = content.AsSpan(index, (lineEnd < 0 ? content.Length : lineEnd) - index).TrimEnd('\r');
            if (!line.IsEmpty)
            {
                break;
            }

            index = nextLine;
        }

        return index;
    }
}
