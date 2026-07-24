using System.Buffers;

namespace OpenForecourt.Emv.BerTlv;

/// <summary>
/// One node in a parsed BER-TLV tree: a tag, and either a primitive value or child nodes.
/// </summary>
/// <remarks>
/// A constructed node (<see cref="Tag.IsConstructed"/>) carries <see cref="Children"/> and
/// an empty <see cref="Value"/>; a primitive node carries <see cref="Value"/> and no
/// children. The tree is immutable once parsed or built.
/// </remarks>
public sealed class TlvNode
{
    private readonly byte[] _value;
    private readonly TlvNode[] _children;

    private TlvNode(Tag tag, byte[] value, TlvNode[] children)
    {
        Tag = tag;
        _value = value;
        _children = children;
    }

    /// <summary>This node's tag.</summary>
    public Tag Tag { get; }

    /// <summary>The primitive value bytes (empty for a constructed node).</summary>
    public ReadOnlyMemory<byte> Value => _value;

    /// <summary>The child nodes (empty for a primitive node).</summary>
    public IReadOnlyList<TlvNode> Children => _children;

    /// <summary>Creates a primitive node from a tag and value.</summary>
    public static TlvNode Primitive(Tag tag, ReadOnlySpan<byte> value) =>
        new(tag, value.ToArray(), []);

    /// <summary>Creates a constructed node from a tag and children.</summary>
    public static TlvNode Constructed(Tag tag, params TlvNode[] children) =>
        new(tag, [], children);

    /// <summary>
    /// Finds the first descendant with the given tag, searching this node's subtree
    /// breadth-first. Returns null if absent.
    /// </summary>
    public TlvNode? Find(Tag tag)
    {
        var queue = new Queue<TlvNode>();
        foreach (var child in _children)
        {
            queue.Enqueue(child);
        }

        // Include self-match convenience: callers usually search within a template.
        if (Tag == tag)
        {
            return this;
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.Tag == tag)
            {
                return node;
            }

            foreach (var child in node._children)
            {
                queue.Enqueue(child);
            }
        }

        return null;
    }

    /// <summary>Writes this node (tag, length, value/children) to the writer.</summary>
    /// <remarks>
    /// Length is emitted in the minimal BER definite form. For input that already used
    /// minimal lengths — which EMV always does — the output is byte-identical to the input.
    /// </remarks>
    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write(Tag.Bytes);

        if (Tag.IsConstructed)
        {
            // Encode children into a scratch buffer so we know the content length.
            var scratch = new ArrayBufferWriter<byte>();
            foreach (var child in _children)
            {
                child.WriteTo(scratch);
            }

            WriteLength(writer, scratch.WrittenCount);
            writer.Write(scratch.WrittenSpan);
        }
        else
        {
            WriteLength(writer, _value.Length);
            writer.Write(_value);
        }
    }

    /// <summary>Serialises this node to a fresh byte array.</summary>
    public byte[] ToBytes()
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteTo(writer);
        return writer.WrittenSpan.ToArray();
    }

    private static void WriteLength(IBufferWriter<byte> writer, int length)
    {
        if (length < 0x80)
        {
            writer.Write([(byte)length]);
            return;
        }

        // Long form: 0x80 | number-of-length-bytes, then the length big-endian.
        Span<byte> tmp = stackalloc byte[4];
        int n = 0;
        int v = length;
        while (v > 0)
        {
            tmp[n++] = (byte)(v & 0xFF);
            v >>= 8;
        }

        Span<byte> header = stackalloc byte[1 + n];
        header[0] = (byte)(0x80 | n);
        for (int i = 0; i < n; i++)
        {
            header[1 + i] = tmp[n - 1 - i];
        }

        writer.Write(header);
    }
}
