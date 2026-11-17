using System.Buffers.Binary;
using System.Text;

namespace SaveTether.Core.Resolution;

/// <summary>A node of Valve's KeyValues format: a string value, or a list of named children in file order.</summary>
internal sealed class VdfNode
{
    private VdfNode(string? value, List<KeyValuePair<string, VdfNode>>? children)
    {
        Value = value;
        Children = children;
    }

    /// <summary>The value of a leaf. Numbers from the binary format are written as decimal text.</summary>
    public string? Value { get; }

    /// <summary>The children of a section; null for a leaf. Keys may repeat, as in the files themselves.</summary>
    public IReadOnlyList<KeyValuePair<string, VdfNode>>? Children { get; }

    public static VdfNode Leaf(string value) => new(value, null);

    public static VdfNode Section(List<KeyValuePair<string, VdfNode>> children) => new(null, children);

    /// <summary>The first child with this key, ignoring case as Steam does; null when there is none.</summary>
    public VdfNode? Get(string key) =>
        Children?.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Thrown when a VDF file cannot be read as one.</summary>
internal sealed class VdfException(string message) : Exception(message);

/// <summary>
/// Readers for the two forms of Valve's KeyValues files that the resolver needs: the text form
/// (libraryfolders.vdf) and the binary form (shortcuts.vdf).
/// </summary>
internal static class Vdf
{
    /// <summary>
    /// Reads the text form: quoted or bare tokens, "{" and "}" for sections, // comments. Escapes
    /// \\, \" , \n and \t are understood inside quotes. Conditionals such as [$WIN32] are skipped.
    /// </summary>
    public static VdfNode ParseText(string text)
    {
        var position = 0;
        var root = ParseSectionBody(text, ref position, topLevel: true);
        return root;
    }

    private static VdfNode ParseSectionBody(string text, ref int position, bool topLevel)
    {
        var children = new List<KeyValuePair<string, VdfNode>>();
        while (true)
        {
            var token = NextToken(text, ref position);
            if (token is null)
            {
                if (!topLevel)
                {
                    throw new VdfException("The file ends inside a section.");
                }

                return VdfNode.Section(children);
            }

            if (token is { Quoted: false, Text: "}" })
            {
                if (topLevel)
                {
                    throw new VdfException("A '}' closes a section that was never opened.");
                }

                return VdfNode.Section(children);
            }

            if (token is { Quoted: false, Text: "{" })
            {
                throw new VdfException("A '{' has no key before it.");
            }

            var value = NextToken(text, ref position) ?? throw new VdfException($"The key '{token.Text}' has no value.");
            if (value is { Quoted: false, Text: "{" })
            {
                children.Add(new(token.Text, ParseSectionBody(text, ref position, topLevel: false)));
            }
            else if (value is { Quoted: false, Text: "}" })
            {
                throw new VdfException($"The key '{token.Text}' has no value.");
            }
            else
            {
                children.Add(new(token.Text, VdfNode.Leaf(value.Text)));
            }

            SkipConditional(text, ref position);
        }
    }

    private sealed record Token(string Text, bool Quoted);

    private static Token? NextToken(string text, ref int position)
    {
        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
            }
            else if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }
            }
            else
            {
                break;
            }
        }

        if (position >= text.Length)
        {
            return null;
        }

        if (text[position] is '{' or '}')
        {
            return new Token(text[position++].ToString(), Quoted: false);
        }

        var builder = new StringBuilder();
        if (text[position] == '"')
        {
            position++;
            while (true)
            {
                if (position >= text.Length)
                {
                    throw new VdfException("A quoted string is never closed.");
                }

                var c = text[position++];
                if (c == '"')
                {
                    return new Token(builder.ToString(), Quoted: true);
                }

                if (c == '\\' && position < text.Length)
                {
                    var escaped = text[position++];
                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        _ => escaped,
                    });
                }
                else
                {
                    builder.Append(c);
                }
            }
        }

        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"'))
        {
            builder.Append(text[position++]);
        }

        return new Token(builder.ToString(), Quoted: false);
    }

    /// <summary>Skips a [$CONDITION] after a value, which Steam's own files sometimes carry.</summary>
    private static void SkipConditional(string text, ref int position)
    {
        var look = position;
        while (look < text.Length && text[look] is ' ' or '\t')
        {
            look++;
        }

        if (look < text.Length && text[look] == '[')
        {
            var close = text.IndexOf(']', look);
            position = close < 0 ? text.Length : close + 1;
        }
    }

    // Binary type bytes (Valve's KeyValues binary format).
    private const byte TypeSection = 0x00;
    private const byte TypeString = 0x01;
    private const byte TypeInt32 = 0x02;
    private const byte TypeFloat = 0x03;
    private const byte TypePointer = 0x04;
    private const byte TypeColor = 0x06;
    private const byte TypeUInt64 = 0x07;
    private const byte TypeEnd = 0x08;
    private const byte TypeInt64 = 0x0A;

    /// <summary>
    /// Reads the binary form: each entry is a type byte, a NUL-terminated name and a value; a
    /// section ends at 0x08. 32-bit integers are read as unsigned, which is how Steam uses app IDs.
    /// </summary>
    public static VdfNode ParseBinary(ReadOnlySpan<byte> data)
    {
        var position = 0;
        var root = ParseBinarySection(data, ref position, topLevel: true);
        return root;
    }

    private static VdfNode ParseBinarySection(ReadOnlySpan<byte> data, ref int position, bool topLevel)
    {
        var children = new List<KeyValuePair<string, VdfNode>>();
        while (true)
        {
            if (position >= data.Length)
            {
                if (topLevel)
                {
                    return VdfNode.Section(children);
                }

                throw new VdfException("The file ends inside a section.");
            }

            var type = data[position++];
            if (type == TypeEnd)
            {
                return VdfNode.Section(children);
            }

            var name = ReadCString(data, ref position);
            VdfNode node = type switch
            {
                TypeSection => ParseBinarySection(data, ref position, topLevel: false),
                TypeString => VdfNode.Leaf(ReadCString(data, ref position)),
                TypeInt32 or TypePointer or TypeColor => VdfNode.Leaf(BinaryPrimitives.ReadUInt32LittleEndian(Take(data, ref position, 4)).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                TypeFloat => VdfNode.Leaf(BinaryPrimitives.ReadSingleLittleEndian(Take(data, ref position, 4)).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                TypeUInt64 => VdfNode.Leaf(BinaryPrimitives.ReadUInt64LittleEndian(Take(data, ref position, 8)).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                TypeInt64 => VdfNode.Leaf(BinaryPrimitives.ReadInt64LittleEndian(Take(data, ref position, 8)).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                _ => throw new VdfException($"Unknown entry type 0x{type:X2} at byte {position - 1}."),
            };
            children.Add(new(name, node));
        }
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int position, int count)
    {
        if (position + count > data.Length)
        {
            throw new VdfException("The file ends inside a value.");
        }

        var slice = data.Slice(position, count);
        position += count;
        return slice;
    }

    private static string ReadCString(ReadOnlySpan<byte> data, ref int position)
    {
        var end = data[position..].IndexOf((byte)0);
        if (end < 0)
        {
            throw new VdfException("The file ends inside a name or string.");
        }

        var text = Encoding.UTF8.GetString(data.Slice(position, end));
        position += end + 1;
        return text;
    }
}
