using System.Text;
using System.Text.Json;

namespace SaveTether.Core.Definitions;

/// <summary>What kind of value a node of a definition file is.</summary>
internal enum NodeKind
{
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null,
}

/// <summary>
/// One value in a definition file, with the line it starts on. The built-in JSON document model
/// does not keep positions, and a mistake in a hand-written file needs to be pointed at.
/// </summary>
/// <param name="Line">1-based line where the value starts.</param>
/// <param name="Text">The text of a string; the raw digits of a number.</param>
/// <param name="Members">For an object: its fields, in file order.</param>
/// <param name="Items">For an array: its values, in file order.</param>
internal sealed record JsonTreeNode(
    NodeKind Kind,
    int Line,
    string? Text = null,
    bool Flag = false,
    IReadOnlyList<JsonTreeMember>? Members = null,
    IReadOnlyList<JsonTreeNode>? Items = null)
{
    /// <summary>The whole number this node holds, when it is a number without a fraction or exponent.</summary>
    public long? AsInteger =>
        Kind == NodeKind.Number && long.TryParse(Text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public string Describe() => Kind switch
    {
        NodeKind.Object => "an object",
        NodeKind.Array => "a list",
        NodeKind.String => "text",
        NodeKind.Number => "a number",
        NodeKind.Boolean => "true or false",
        _ => "null",
    };
}

/// <param name="Line">1-based line of the field's name.</param>
internal sealed record JsonTreeMember(string Name, int Line, JsonTreeNode Value);

/// <summary>The text is not JSON at all. <see cref="Line"/> says where the reader gave up.</summary>
internal sealed class JsonTreeException : Exception
{
    public JsonTreeException()
    {
    }

    public JsonTreeException(string message)
        : base(message)
    {
    }

    public JsonTreeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public JsonTreeException(int line, string message)
        : base(message)
    {
        Line = line;
    }

    public JsonTreeException(int line, string message, Exception innerException)
        : base(message, innerException)
    {
        Line = line;
    }

    public int Line { get; } = 1;
}

/// <summary>
/// Reads JSON with comments and trailing commas into <see cref="JsonTreeNode"/>s. It uses the
/// platform's own tokenizer and no reflection, so it behaves the same in the native binary.
/// </summary>
internal static class JsonTree
{
    public static JsonTreeNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonTreeException(1, "the file is empty");
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var lines = new LineIndex(bytes);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        try
        {
            if (!reader.Read())
            {
                throw new JsonTreeException(1, "the file is empty");
            }

            var root = ReadValue(ref reader, lines);
            bool more;
            try
            {
                more = reader.Read();
            }
            catch (JsonException e)
            {
                // The tokenizer refuses a second value itself. Say what that means for this file.
                throw new JsonTreeException((int)(e.LineNumber ?? 0) + 1, "there is more after the end of the definition", e);
            }

            if (more)
            {
                throw new JsonTreeException(lines.LineAt(reader.TokenStartIndex), "there is more after the end of the definition");
            }

            return root;
        }
        catch (JsonException e)
        {
            // The tokenizer counts lines from 0 and words its messages for programmers.
            var line = (int)(e.LineNumber ?? 0) + 1;
            throw new JsonTreeException(line, "this is not valid JSON here (a missing comma, quote or bracket is the usual cause)", e);
        }
    }

    private static JsonTreeNode ReadValue(ref Utf8JsonReader reader, LineIndex lines)
    {
        var line = lines.LineAt(reader.TokenStartIndex);
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var members = new List<JsonTreeMember>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString() ?? string.Empty;
                    var nameLine = lines.LineAt(reader.TokenStartIndex);
                    if (members.Any(m => m.Name == name))
                    {
                        throw new JsonTreeException(nameLine, $"the field \"{name}\" is given twice");
                    }

                    reader.Read();
                    members.Add(new JsonTreeMember(name, nameLine, ReadValue(ref reader, lines)));
                }

                return new JsonTreeNode(NodeKind.Object, line, Members: members);

            case JsonTokenType.StartArray:
                var items = new List<JsonTreeNode>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(ReadValue(ref reader, lines));
                }

                return new JsonTreeNode(NodeKind.Array, line, Items: items);

            case JsonTokenType.String:
                return new JsonTreeNode(NodeKind.String, line, reader.GetString());

            case JsonTokenType.Number:
                return new JsonTreeNode(NodeKind.Number, line, Encoding.UTF8.GetString(reader.ValueSpan));

            case JsonTokenType.True or JsonTokenType.False:
                return new JsonTreeNode(NodeKind.Boolean, line, Flag: reader.TokenType == JsonTokenType.True);

            default:
                return new JsonTreeNode(NodeKind.Null, line);
        }
    }

    /// <summary>Turns a byte offset into a 1-based line number.</summary>
    private sealed class LineIndex
    {
        private readonly List<long> _lineStarts = [0];

        public LineIndex(byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\n')
                {
                    _lineStarts.Add(i + 1);
                }
            }
        }

        public int LineAt(long offset)
        {
            var index = _lineStarts.BinarySearch(offset);
            return index >= 0 ? index + 1 : ~index;
        }
    }
}
