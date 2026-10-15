// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Cli;

/// <summary>
///  A command line split into positional words and <c>--options</c>, which may carry a value
///  (<c>--flow gitflow</c> or <c>--flow=gitflow</c>) or stand alone (<c>--json</c>).
/// </summary>
/// <remarks>
///  Deliberately small: gx has a handful of commands, and a parsing library would outweigh them. An
///  option is a flag unless it is listed in <c>valued</c>, so a value can never be mistaken for the
///  next positional word or the other way round.
/// </remarks>
internal sealed class Arguments
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public Arguments(IEnumerable<string> words, params string[] valued)
    {
        HashSet<string> takesValue = new(valued, StringComparer.OrdinalIgnoreCase);
        List<string> positional = [];
        using IEnumerator<string> word = words.GetEnumerator();

        while (word.MoveNext())
        {
            string current = word.Current;

            if (!current.StartsWith("--", StringComparison.Ordinal) || current.Length == 2)
            {
                positional.Add(current);
                continue;
            }

            string name = current[2..];
            int equals = name.IndexOf('=', StringComparison.Ordinal);

            if (equals >= 0)
            {
                _options[name[..equals]] = name[(equals + 1)..];
            }
            else if (takesValue.Contains(name))
            {
                if (!word.MoveNext())
                {
                    throw new UsageException($"--{name} needs a value.");
                }

                _options[name] = word.Current;
            }
            else
            {
                _flags.Add(name);
            }
        }

        Positional = positional;
    }

    public IReadOnlyList<string> Positional { get; }

    public string this[int index] => index < Positional.Count ? Positional[index] : "";

    public bool Has(string flag) => _flags.Contains(flag);

    public string Option(string name, string fallback = "") => _options.TryGetValue(name, out string? value) ? value : fallback;
}

/// <summary>The command line was wrong; the message says how, and the exit code is 2.</summary>
internal sealed class UsageException(string message) : Exception(message);
