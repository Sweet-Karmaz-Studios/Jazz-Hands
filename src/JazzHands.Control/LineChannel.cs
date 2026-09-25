using System.Text;
using System.Threading.Channels;

namespace JazzHands.Control;

/// <summary>
/// Newline-framed text over a stream: one reader, and one writer fed through a queue, so anything
/// can send (a response, an event from an engine thread) without waiting on the socket.
/// </summary>
/// <remarks>
/// A frame longer than <see cref="MaxFrameBytes"/> ends the connection rather than filling memory:
/// no request is that big, and a response that big goes out in chunks. The outgoing queue is
/// bounded too; a client that stops reading is dropped rather than letting its events pile up
/// behind it forever.
/// </remarks>
public sealed class LineChannel : IAsyncDisposable
{
    /// <summary>The longest frame read: 64 MB.</summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly Channel<string> _outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private readonly Task _writer;
    private readonly CancellationTokenSource _closing = new();
    private readonly char[] _buffer = new char[64 * 1024];
    private int _start;
    private int _end;
    private int _disposed;

    /// <summary>Frames a stream.</summary>
    public LineChannel(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>True once the other end has gone or the channel was closed.</summary>
    public bool IsClosed => _closing.IsCancellationRequested;

    /// <summary>Signalled when the channel closes.</summary>
    public CancellationToken Closed => _closing.Token;

    /// <summary>The next line, or null when the other end has gone or sent a frame too long.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        try
        {
            // StreamReader.ReadLineAsync has no length limit, so lines are found here, a buffer at
            // a time, and a frame past the limit ends the connection before it fills memory.
            var line = new StringBuilder();
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await _reader.ReadAsync(_buffer, linked.Token).ConfigureAwait(false);
                    if (_end == 0)
                    {
                        Close();
                        return null;
                    }
                }

                int newline = Array.IndexOf(_buffer, '\n', _start, _end - _start);
                int stop = newline >= 0 ? newline : _end;
                line.Append(_buffer, _start, stop - _start);
                _start = newline >= 0 ? newline + 1 : _end;

                if (line.Length > MaxFrameBytes)
                {
                    Close();
                    return null;
                }

                if (newline >= 0)
                {
                    // A client that ends lines with \r\n is forgiven the \r.
                    if (line.Length > 0 && line[^1] == '\r')
                    {
                        line.Length--;
                    }

                    return line.ToString();
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Close();
            return null;
        }
    }

    /// <summary>Queues a line to send. False when the channel has closed.</summary>
    public bool TrySend(string line) => !IsClosed && _outgoing.Writer.TryWrite(line);

    /// <summary>Queues a line, waiting for room. False when the channel has closed.</summary>
    public async Task<bool> SendAsync(string line, CancellationToken cancellationToken = default)
    {
        if (IsClosed)
        {
            return false;
        }

        try
        {
            await _outgoing.Writer.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>Stops reading and writing; queued lines that have not gone are dropped.</summary>
    public void Close()
    {
        if (!_closing.IsCancellationRequested)
        {
            _closing.Cancel();
            _outgoing.Writer.TryComplete();
        }
    }

    /// <summary>Waits for queued lines to be written, up to a moment, then closes.</summary>
    public async Task FlushAndCloseAsync(TimeSpan wait)
    {
        _outgoing.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(wait)).ConfigureAwait(false);
        Close();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close();
        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        _reader.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _closing.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            byte[] newline = [(byte)'\n'];
            while (await _outgoing.Reader.WaitToReadAsync(_closing.Token).ConfigureAwait(false))
            {
                while (_outgoing.Reader.TryRead(out string? line))
                {
                    byte[] bytes = Utf8.GetBytes(line);
                    await _stream.WriteAsync(bytes, _closing.Token).ConfigureAwait(false);
                    await _stream.WriteAsync(newline, _closing.Token).ConfigureAwait(false);
                }

                await _stream.FlushAsync(_closing.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Close();
        }
    }
}
