using System.Globalization;
using System.Reflection;
using System.Text;

namespace Intrf4ce.Core.Commands;

/// <summary>Marks a method as a command that can be typed after <c>:</c> and put on a key.</summary>
/// <remarks>
/// The method's parameters are the command's arguments, so its signature is the one place that
/// says what it takes: <c>:help</c>, completion and the error for a wrong argument all read it.
/// A parameter named <c>count</c> receives the number typed before a key (<c>3l</c>).
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    public string[] Aliases { get; init; } = [];

    /// <summary>One line, for completion and <c>:help</c>.</summary>
    public string Description { get; init; } = "";

    /// <summary>Where it is listed in <c>:help</c>.</summary>
    public string Group { get; init; } = "General";
}

/// <summary>The last string argument takes the rest of the line, spaces and all: <c>:ask how deep is it</c>.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class RestAttribute : Attribute;

/// <summary>Which list completion offers for an argument: <c>settings</c>, <c>sessions</c>, <c>groups</c>, <c>commands</c>.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ChoicesAttribute(string list) : Attribute
{
    public string List { get; } = list;
}

/// <summary>A command could not be run; the message is for the user, as it stands.</summary>
public sealed class CommandException(string message) : Exception(message);

/// <summary>One registered command.</summary>
public sealed record CommandInfo(
    string Name,
    IReadOnlyList<string> Aliases,
    string Description,
    string Group,
    MethodInfo Method,
    object? Target)
{
    /// <summary>How it is written with its arguments: <c>:go &lt;slide&gt;</c>.</summary>
    public string Usage
    {
        get
        {
            var text = new StringBuilder(":").Append(Name);
            foreach (var parameter in Method.GetParameters().Where(p => p.Name != "count"))
            {
                var name = parameter.Name!;
                text.Append(' ').Append(parameter.GetCustomAttribute<RestAttribute>() is not null
                    ? $"[{name}...]"
                    : parameter.IsOptional ? $"[{name}]" : $"<{name}>");
            }

            return text.ToString();
        }
    }
}

/// <summary>A candidate the prompt offers: what to put in, and a word on what it is.</summary>
public sealed record Completion(string Text, string Description);

/// <summary>
/// Every command, found on the objects that declare them, and the way a typed line becomes a
/// call to one.
/// </summary>
/// <remarks>
/// Found by their attributes rather than listed by hand, so a command cannot exist without
/// being in <c>:help</c> and in completion, or be listed there without existing. The same
/// design as Fjord's; the code is intrf4ce's own.
/// </remarks>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lists for argument completion, by the name a <see cref="ChoicesAttribute"/> gives.</summary>
    public Dictionary<string, Func<IEnumerable<Completion>>> Choices { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command, once each, by name.</summary>
    public IReadOnlyList<CommandInfo> All =>
        [.. _byName.Values.Distinct().OrderBy(c => c.Name, StringComparer.Ordinal)];

    public void Register(object target)
    {
        ArgumentNullException.ThrowIfNull(target);

        foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.GetCustomAttribute<CommandAttribute>() is not { } attribute)
            {
                continue;
            }

            var info = new CommandInfo(attribute.Name, attribute.Aliases, attribute.Description, attribute.Group, method, method.IsStatic ? null : target);
            foreach (var name in attribute.Aliases.Prepend(attribute.Name))
            {
                if (!_byName.TryAdd(name, info))
                {
                    throw new InvalidOperationException($"the command '{name}' is declared twice");
                }
            }
        }
    }

    /// <summary>
    /// Adds one command made at run time — a plugin's ready-made request — rather than found on
    /// an object's methods.
    /// </summary>
    /// <exception cref="InvalidOperationException">A command of that name exists.</exception>
    public void Add(CommandInfo command)
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (var name in command.Aliases.Prepend(command.Name))
        {
            if (_byName.ContainsKey(name))
            {
                throw new InvalidOperationException($"the command '{name}' exists already");
            }
        }

        foreach (var name in command.Aliases.Prepend(command.Name))
        {
            _byName[name] = command;
        }
    }

    public CommandInfo? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// Runs a line as typed after <c>:</c>.
    /// </summary>
    /// <param name="count">The number typed before a key, when the line came from one.</param>
    /// <returns>What the command had to say, if anything.</returns>
    /// <exception cref="CommandException">The line does not name a command, or its arguments will not do.</exception>
    public string? Run(string line, int? count = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        var text = line.Trim().TrimStart(':').Trim();
        if (text.Length == 0)
        {
            return null;
        }

        // ":12" goes to slide 12, as it goes to line 12 in vim.
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && Find("go") is not null)
        {
            text = "go " + number.ToString(CultureInfo.InvariantCulture);
        }

        var tokens = Tokenize(text);
        var name = tokens[0].Text;
        var command = Find(name) ?? throw new CommandException(Unknown(name));

        var parameters = command.Method.GetParameters();
        var values = new object?[parameters.Length];
        var next = 1;

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

            if (parameter.Name == "count" && type == typeof(int))
            {
                values[i] = count ?? (parameter.HasDefaultValue ? parameter.DefaultValue : 1);
                continue;
            }

            if (parameter.GetCustomAttribute<RestAttribute>() is not null)
            {
                var rest = next < tokens.Count ? text[tokens[next].Start..].Trim() : "";
                values[i] = rest.Length > 0 ? rest : parameter.HasDefaultValue ? parameter.DefaultValue : throw Missing(command, parameter);
                next = tokens.Count;
                continue;
            }

            if (next < tokens.Count)
            {
                values[i] = Convert(tokens[next++].Text, type, parameter, command);
            }
            else if (parameter.HasDefaultValue)
            {
                values[i] = parameter.DefaultValue;
            }
            else
            {
                throw Missing(command, parameter);
            }
        }

        if (next < tokens.Count)
        {
            throw new CommandException($"too many arguments — {command.Usage}");
        }

        try
        {
            return command.Method.Invoke(command.Target, values) as string;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// What could come next in a line being typed: command names while the first word is being
    /// written, then whatever list the argument being typed takes.
    /// </summary>
    public IReadOnlyList<Completion> Complete(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var text = line.TrimStart(':');
        var tokens = Tokenize(text);
        var typingName = tokens.Count <= 1 && !text.EndsWith(' ');

        if (typingName)
        {
            var prefix = tokens.Count == 1 ? tokens[0].Text : "";
            return
            [
                .. All.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Concat(All.Where(c => !c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && c.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
                    .Select(c => new Completion(c.Name, c.Description)),
            ];
        }

        if (Find(tokens[0].Text) is not { } command)
        {
            return [];
        }

        // Which argument is being typed, and what of it so far.
        var argument = text.EndsWith(' ') ? tokens.Count - 1 : tokens.Count - 2;
        var partial = text.EndsWith(' ') ? "" : tokens[^1].Text;
        var parameters = command.Method.GetParameters().Where(p => p.Name != "count").ToList();
        if (argument < 0 || argument >= parameters.Count
            || parameters[argument].GetCustomAttribute<ChoicesAttribute>() is not { } choices
            || !Choices.TryGetValue(choices.List, out var list))
        {
            return [];
        }

        return
        [
            .. list().Where(c => c.Text.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .Concat(list().Where(c => !c.Text.StartsWith(partial, StringComparison.OrdinalIgnoreCase)
                    && c.Text.Contains(partial, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    /// <summary>Words, with quotes grouping: <c>group "Rivers of Africa"</c> is two.</summary>
    public static IReadOnlyList<(string Text, int Start)> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new List<(string, int)>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            var start = i;
            var word = new StringBuilder();
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                if (text[i] is '"' or '\'')
                {
                    var quote = text[i++];
                    while (i < text.Length && text[i] != quote)
                    {
                        word.Append(text[i++]);
                    }

                    i++;
                    continue;
                }

                word.Append(text[i++]);
            }

            tokens.Add((word.ToString(), start));
        }

        return tokens;
    }

    private string Unknown(string name)
    {
        // A slip of the fingers is a letter or two out, wherever it falls in the word.
        var near = All.Select(c => (c.Name, Distance: Distance(name.ToLowerInvariant(), c.Name.ToLowerInvariant())))
            .Where(c => c.Distance <= Math.Max(1, name.Length / 3))
            .OrderBy(c => c.Distance)
            .Select(c => c.Name)
            .Take(4)
            .ToList();

        return $"there is no command '{name}'" + (near.Count > 0 ? $" — did you mean {string.Join(", ", near)}?" : "; :help lists them");
    }

    /// <summary>Edits between two words: letters added, removed or changed.</summary>
    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }

    private static CommandException Missing(CommandInfo command, ParameterInfo parameter) =>
        new($"{parameter.Name} is missing — {command.Usage}");

    private static object Convert(string token, Type type, ParameterInfo parameter, CommandInfo command)
    {
        if (type == typeof(string))
        {
            return token;
        }

        if (type == typeof(int) && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        if (type == typeof(double) && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
        {
            return real;
        }

        if (type == typeof(bool))
        {
            switch (token.ToLowerInvariant())
            {
                case "true" or "yes" or "on" or "1":
                    return true;
                case "false" or "no" or "off" or "0":
                    return false;
            }
        }

        throw new CommandException($"{parameter.Name} should be {Describe(type)}, not '{token}' — {command.Usage}");
    }

    private static string Describe(Type type) =>
        type == typeof(int) ? "a whole number" : type == typeof(double) ? "a number" : type == typeof(bool) ? "on or off" : type.Name;
}
