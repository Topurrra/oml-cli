using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace OmlCli;

// The messages between an oml process and a warm worker, on a named pipe: one byte for the kind, four for the length
// (little-endian), then the text as UTF-16 code units, so any .NET string (even a lone surrogate) arrives unchanged.
//   caller -> worker   q  the request (JSON)
//   worker -> caller   o  stdout text   e  stderr text   x  the end: {"code": n, "crash": "<type>"}
//                      b  busy (another command runs; try another worker or run it here)
//                      s  starting (Service Studio is not loaded yet)   r  refused (run it here; the text says why)
//                      i  status (JSON)   k  stopping
internal static class WarmFrames
{
    public static void Write(Stream stream, char kind, string text)
    {
        var payload = MemoryMarshal.AsBytes(text.AsSpan());
        var frame = new byte[5 + payload.Length];
        frame[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        stream.Write(frame);
        stream.Flush();
    }

    // Null at the end of the stream; EndOfStreamException when it ends inside a frame.
    public static (char Kind, string Text)? Read(Stream stream)
    {
        var header = new byte[5];
        var first = stream.Read(header, 0, 5);
        if (first == 0) return null;
        if (first < 5) stream.ReadExactly(header, first, 5 - first);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length % 2 != 0 || length > 1 << 30) throw new InvalidDataException("A warm worker message has a bad length.");
        var payload = new byte[length];
        stream.ReadExactly(payload);
        return ((char)header[0], new string(MemoryMarshal.Cast<byte, char>(payload)));
    }
}

// The worker's stdout and stderr for one command: text is collected and sent as o and e messages, in the order it was
// written, at most 100 ms late (so a progress line still moves) and in pieces of up to 32 KB. A caller that went away
// does not stop the command: what is written after that is dropped.
internal sealed class WarmSink : IDisposable
{
    readonly Stream pipe;
    readonly StringBuilder pending = new();
    readonly Timer timer;
    readonly object gate = new();
    char kind = 'o';
    bool broken;

    public WarmSink(Stream pipe)
    {
        this.pipe = pipe;
        timer = new Timer(_ => Flush(), null, 100, 100);
    }

    public bool Wrote { get; private set; }

    public void Append(char channel, string text)
    {
        if (text.Length == 0) return;
        lock (gate)
        {
            if (channel != kind && pending.Length > 0) FlushLocked();
            kind = channel;
            pending.Append(text);
            if (pending.Length >= 16 * 1024) FlushLocked();
        }
    }

    public void Flush()
    {
        lock (gate) FlushLocked();
    }

    void FlushLocked()
    {
        if (pending.Length == 0) return;
        var text = pending.ToString();
        pending.Clear();
        Send(kind, text);
        Wrote = true;
    }

    public void Send(char frame, string text)
    {
        lock (gate)
        {
            if (broken) return;
            try { WarmFrames.Write(pipe, frame, text); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { broken = true; }
        }
    }

    public void Dispose() => timer.Dispose();
}

internal sealed class WarmWriter(WarmSink sink, char channel) : TextWriter
{
    public override Encoding Encoding => Encoding.Unicode;
    public override void Write(char value) => sink.Append(channel, value.ToString());
    public override void Write(string? value) { if (value is not null) sink.Append(channel, value); }
    public override void Write(char[] buffer, int index, int count) => sink.Append(channel, new string(buffer, index, count));
    public override void Write(ReadOnlySpan<char> buffer) => sink.Append(channel, new string(buffer));
    public override void WriteLine(string? value) => sink.Append(channel, (value ?? "") + NewLine);
    public override void Flush() { }   // the sink sends at most 100 ms later; a flush per write would make many tiny messages
}
