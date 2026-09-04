namespace Migurdex.Core.Interop;

public unsafe class RustNativeStream : Stream
{
    private readonly delegate* unmanaged<byte*, nuint, void> _freeFunc;
    private readonly long                                    _length;
    private          long                                    _position;
    private          byte*                                   _ptr;

    public RustNativeStream(byte* ptr, nuint length, delegate* unmanaged<byte*, nuint, void> freeFunc)
    {
        _ptr      = ptr;
        _length   = (long) length;
        _freeFunc = freeFunc;
        _position = 0;
    }

    public override bool CanRead  => _ptr != null;
    public override bool CanSeek  => _ptr != null;
    public override bool CanWrite => false;
    public override long Length   => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(new Span<byte>(buffer, offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (_ptr == null)
        {
            throw new ObjectDisposedException(nameof(RustNativeStream));
        }

        var remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var toRead  = (int) Math.Min(buffer.Length, remaining);
        var srcSpan = new ReadOnlySpan<byte>(_ptr + _position, toRead);
        srcSpan.CopyTo(buffer);

        _position += toRead;

        return toRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPos = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => _length + offset,
            _                  => throw new ArgumentException(null, nameof(origin))
        };

        if (newPos < 0 || newPos > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        _position = newPos;

        return _position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_ptr != null)
        {
            _freeFunc(_ptr, (nuint) _length);
            _ptr = null;
        }

        base.Dispose(disposing);
    }
}
