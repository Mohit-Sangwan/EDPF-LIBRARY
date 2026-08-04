using System;

namespace Edpf.Abstractions.Communication;

/// <summary>The kind of endpoint an address identifies.</summary>
public enum AddressKind
{
    /// <summary>An RFC 5322 mailbox.</summary>
    Email = 0,

    /// <summary>An E.164 telephone number.</summary>
    Phone = 1,
}

/// <summary>
/// A validated destination for an outbound message.
/// </summary>
/// <remarks>
/// <para>
/// The same trick as <see cref="Storage.BlobPath"/>: there is no public
/// constructor, so an unvalidated address is unconstructable and every channel
/// receives one that has already been checked.
/// </para>
/// <para>
/// **Malformed input is rejected, not repaired.** A library that strips the
/// carriage return out of an address and sends anyway has decided, on the
/// user's behalf, that a header-injection attempt was a typo.
/// </para>
/// </remarks>
public sealed class MessageAddress : IEquatable<MessageAddress>
{
    /// <summary>Longest accepted address, covering the RFC 5321 mailbox limit.</summary>
    public const int MaxLength = 254;

    private MessageAddress(AddressKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>What kind of endpoint this is.</summary>
    public AddressKind Kind { get; }

    /// <summary>The validated address.</summary>
    public string Value { get; }

    /// <summary>
    /// Validates an email address.
    /// </summary>
    /// <param name="address">The mailbox.</param>
    /// <returns>The validated address.</returns>
    /// <exception cref="ArgumentException">
    /// The address is blank, over-long, carries a control character or
    /// whitespace, or is not <c>local@domain</c> with a dotted domain.
    /// </exception>
    /// <remarks>
    /// Deliberately stricter than RFC 5322, which permits quoted local parts
    /// containing almost anything including <c>@</c> and spaces. Accepting the
    /// full grammar means accepting addresses that downstream MTAs, log
    /// pipelines and support tooling parse differently from each other, and a
    /// disagreement about where an address ends is how mail reaches the wrong
    /// mailbox. Rejecting the exotic 0.01% is the better trade.
    /// </remarks>
    public static MessageAddress ForEmail(string address)
    {
        string value = Require(address, nameof(address));

        // Scanned by hand rather than with IndexOf. This assembly targets
        // net472 as well (ADR-002), where the StringComparison-taking char
        // overloads do not exist — and the analyser insists on them everywhere
        // they do. One explicit loop satisfies five target frameworks; that is
        // the multi-target tax, paid here for the eighth time.
        int at = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '@')
            {
                continue;
            }

            if (at >= 0)
            {
                throw new ArgumentException(
                    "An email address must have exactly one @ separator.", nameof(address));
            }

            at = i;
        }

        if (at <= 0 || at == value.Length - 1)
        {
            throw new ArgumentException(
                "An email address must be local@domain with exactly one separator.", nameof(address));
        }

        int firstDot = -1;
        for (int i = at + 1; i < value.Length; i++)
        {
            if (value[i] == '.')
            {
                firstDot = i;
                break;
            }
        }

        if (firstDot <= at + 1 || value[value.Length - 1] == '.')
        {
            throw new ArgumentException("The domain part must be dotted and must not end in a dot.", nameof(address));
        }

        return new MessageAddress(AddressKind.Email, value);
    }

    /// <summary>
    /// Validates an E.164 telephone number.
    /// </summary>
    /// <param name="number">The number, including the leading <c>+</c>.</param>
    /// <returns>The validated address.</returns>
    /// <exception cref="ArgumentException">
    /// The number is not <c>+</c> followed by 8 to 15 digits.
    /// </exception>
    /// <remarks>
    /// E.164 only. National formats are ambiguous without knowing the origin
    /// country, and a platform that guesses the country of a bare
    /// <c>0123456789</c> will eventually text a stranger somebody's appointment
    /// reminder.
    /// </remarks>
    public static MessageAddress ForPhone(string number)
    {
        string value = Require(number, nameof(number));

        if (value.Length is < 9 or > 16 || value[0] != '+')
        {
            throw new ArgumentException(
                "A phone number must be E.164: a leading + and 8 to 15 digits.", nameof(number));
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                throw new ArgumentException(
                    "A phone number must be E.164: a leading + and 8 to 15 digits.", nameof(number));
            }
        }

        return new MessageAddress(AddressKind.Phone, value);
    }

    private static string Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An address is required.", parameterName);
        }

        string trimmed = value!.Trim();

        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException("The address exceeds the maximum length.", parameterName);
        }

        foreach (char c in trimmed)
        {
            // The CR/LF case is the security-relevant one: an address carrying
            // a newline becomes an extra SMTP header, which is how a message
            // acquires a Bcc nobody authorised.
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                throw new ArgumentException(
                    "An address may not contain whitespace or control characters. A newline here would become "
                    + "an additional header.",
                    parameterName);
            }
        }

        return trimmed;
    }

    /// <inheritdoc />
    public bool Equals(MessageAddress? other)
        => other is not null
            && Kind == other.Kind
            && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as MessageAddress);

    /// <inheritdoc />
    public override int GetHashCode()
        => unchecked((StringComparer.OrdinalIgnoreCase.GetHashCode(Value) * 397) ^ (int)Kind);

    /// <summary>The address. Personal data — never log this.</summary>
    public override string ToString() => Value;
}
