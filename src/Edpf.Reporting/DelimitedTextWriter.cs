using System;
using System.Collections.Generic;
using System.Text;
using Edpf.Core.Guards;

namespace Edpf.Reporting;

/// <summary>
/// Writes delimited text whose cells cannot execute in a spreadsheet
/// (Phase 33b).
/// </summary>
/// <remarks>
/// <para>
/// **A spreadsheet is a program, and a CSV cell is source code for it**
/// (CWE-1236, "formula injection"). A cell whose value begins with
/// <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> is evaluated when the file is
/// opened. Excel's <c>DDE</c> and <c>WEBSERVICE</c> functions turn that into
/// remote content retrieval and, historically, command execution — triggered
/// by a user double-clicking a report someone emailed them.
/// </para>
/// <para>
/// The attacker's input does not need to be sophisticated. Someone types
/// <c>=cmd|'/c calc'!A1</c> into a free-text notes field; it sits inertly in
/// the database for months; then a monthly report exports it and a
/// finance manager opens the file.
/// </para>
/// <para>
/// **Quoting is not sufficient.** A CSV field written as <c>"=1+1"</c> is
/// still parsed as a formula by Excel — the quotes are CSV syntax, consumed
/// before the cell value is interpreted. Neutralisation has to change the
/// value itself.
/// </para>
/// </remarks>
public sealed class DelimitedTextWriter
{
    /// <summary>
    /// The characters that make a cell executable when it leads the value.
    /// </summary>
    /// <remarks>
    /// <c>=</c> and <c>@</c> start a formula. <c>+</c> and <c>-</c> start one
    /// too, and are the easy ones to forget because they look like ordinary
    /// numeric signs. Tab and carriage return are included because several
    /// importers strip leading whitespace *before* deciding whether the
    /// remainder is a formula, which puts <c>\t=cmd</c> straight back into the
    /// dangerous case.
    /// </remarks>
    public static IReadOnlyList<char> ExecutableLeadingCharacters { get; } =
        ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>Initializes a writer.</summary>
    /// <param name="delimiter">The field delimiter.</param>
    /// <param name="neutralizeFormulas">
    /// Whether to neutralise executable cells. Defaults to true; see
    /// <see cref="Neutralize"/> for why turning it off is a decision rather
    /// than a preference.
    /// </param>
    public DelimitedTextWriter(char delimiter = ',', bool neutralizeFormulas = true)
    {
        if (delimiter is '"' or '\n' or '\r')
        {
            throw new ArgumentOutOfRangeException(
                nameof(delimiter), delimiter, "That character cannot be a delimiter.");
        }

        Delimiter = delimiter;
        NeutralizeFormulas = neutralizeFormulas;
    }

    /// <summary>The field delimiter.</summary>
    public char Delimiter { get; }

    /// <summary>Whether executable cells are neutralised.</summary>
    public bool NeutralizeFormulas { get; }

    /// <summary>The marker prefixed to a cell that would otherwise execute.</summary>
    /// <remarks>
    /// An apostrophe, which every major spreadsheet treats as "the rest of
    /// this cell is text" and does not display.
    /// </remarks>
    public const char TextMarker = '\'';

    /// <summary>
    /// Neutralises a value that would execute as a formula.
    /// </summary>
    /// <param name="value">The raw cell value.</param>
    /// <returns>The value, prefixed if it would otherwise execute.</returns>
    /// <remarks>
    /// <para>
    /// **This changes the data, and that is a real cost.** A part number
    /// legitimately beginning <c>-</c> exports as <c>'-</c>, and a downstream
    /// system re-importing the file sees a different string.
    /// </para>
    /// <para>
    /// It is accepted because the alternative is worse in kind rather than
    /// degree: an altered value is a data-quality problem, and an executing
    /// value is code running on the recipient's machine. A deployment that
    /// genuinely round-trips exports between systems should use a format that
    /// is not also a programming language — the writer's flag exists so that
    /// choice is explicit and reviewable, not so it is convenient.
    /// </para>
    /// </remarks>
    public string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value) || !NeutralizeFormulas)
        {
            return value ?? string.Empty;
        }

        foreach (char dangerous in ExecutableLeadingCharacters)
        {
            if (value![0] == dangerous)
            {
                return TextMarker + value;
            }
        }

        return value!;
    }

    /// <summary>
    /// Writes one row.
    /// </summary>
    /// <param name="values">The cell values.</param>
    /// <returns>The row, without a trailing newline.</returns>
    public string WriteRow(IReadOnlyList<string?> values)
    {
        Guard.NotNull(values, nameof(values));

        var row = new StringBuilder();

        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                row.Append(Delimiter);
            }

            row.Append(Quote(Neutralize(values[i])));
        }

        return row.ToString();
    }

    /// <summary>
    /// Quotes a cell for CSV, doubling embedded quotes.
    /// </summary>
    /// <param name="value">The neutralised value.</param>
    /// <returns>The quoted cell.</returns>
    /// <remarks>
    /// Applied whenever the value contains a delimiter, a quote or a line
    /// break. An unquoted delimiter inside a value shifts every subsequent
    /// column by one for that row — which is not a security problem but is
    /// the single most common way an export is silently wrong.
    /// </remarks>
    private string Quote(string value)
    {
        bool needsQuoting = false;

        foreach (char c in value)
        {
            if (c == Delimiter || c == '"' || c == '\n' || c == '\r')
            {
                needsQuoting = true;
                break;
            }
        }

        if (!needsQuoting)
        {
            return value;
        }

        var quoted = new StringBuilder(value.Length + 2);
        quoted.Append('"');

        foreach (char c in value)
        {
            if (c == '"')
            {
                quoted.Append('"');
            }

            quoted.Append(c);
        }

        quoted.Append('"');
        return quoted.ToString();
    }
}
