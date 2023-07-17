// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Microsoft.ManagedZLib;

public partial class DeflateStream : Stream
{
    private const int DefaultBufferSize = 8192;
    private Stream _stream;
    private Inflater? _inflater;
    private Deflater? _deflater;
    private byte[]? _buffer;
    private int _activeAsyncOperation; // 1 == true, 0 == false
    private CompressionMode _mode;
    private bool _leaveOpen;
    private bool _wroteBytes;
    private int _asyncOperations;

    internal DeflateStream(Stream stream, CompressionMode mode, long uncompressedSize) : this(stream, mode, leaveOpen: false, ManagedZLib.Deflate_DefaultWindowBits, uncompressedSize)
    {
    }

    public DeflateStream(Stream stream, CompressionMode mode) : this(stream, mode, leaveOpen: false)
    {
    }

    public DeflateStream(Stream stream, CompressionMode mode, bool leaveOpen) : this(stream, mode, leaveOpen, ManagedZLib.Deflate_DefaultWindowBits)
    {
    }

    // Implies mode = Compress
    public DeflateStream(Stream stream, CompressionLevel compressionLevel) : this(stream, compressionLevel, leaveOpen: false)
    {
    }

    // Implies mode = Compress
    public DeflateStream(Stream stream, CompressionLevel compressionLevel, bool leaveOpen) : this(stream, compressionLevel, leaveOpen, ManagedZLib.Deflate_DefaultWindowBits)
    {
    }

    /// <summary>
    /// Internal constructor to check stream validity and call the correct initialization function depending on
    /// the value of the CompressionMode given.
    /// </summary>

    public DeflateStream(Stream stream, CompressionMode mode, bool leaveOpen, int windowBits, long uncompressedSize = -1)
    {
        ArgumentNullException.ThrowIfNull(stream);

        switch (mode)
        {
            case CompressionMode.Decompress:
                if (!stream.CanRead)
                    //This would normally use System.SR
                    //For testing purposes, we are going to use the exception message directly
                    throw new ArgumentException("NotSupported_UnreadableStream - Stream does not support reading.", nameof(stream));

                _inflater = new Inflater(windowBits, uncompressedSize);
                _stream = stream;
                _mode = CompressionMode.Decompress;
                _leaveOpen = leaveOpen;
                break;

            case CompressionMode.Compress:
                InitializeDeflater(stream, leaveOpen, windowBits, CompressionLevel.Optimal);
                break;

            default:
                throw new ArgumentException("ArgumentOutOfRange_Enum - Enum value was out of legal range.", nameof(mode));
        }
        //For iflater having a buffer with the default size is enough for reading the input stream (compressed data)
        // For compressing this will vary depending on the Level of compression ask.
        // Reading more data at a time is more efficient
        _buffer = new byte[DefaultBufferSize]; //Instead of using array pool in Read** When tests working check if it's possible a change back
        //InflateInit2 - set the reference of the underlying stream, into input buffer
        //Debug.Assert(_inflater != null);
        //_inflater.SetInput(_buffer);

    }

    /// <summary>
    /// Internal constructor to specify the compressionlevel as well as the windowbits
    /// </summary>
    internal DeflateStream(Stream stream, CompressionLevel compressionLevel, bool leaveOpen, int windowBits)
    {
        _buffer = new byte[DefaultBufferSize]; //Instead of using array pool in Read** When tests working check if it's possible a change back
        ArgumentNullException.ThrowIfNull(stream);

        InitializeDeflater(stream, leaveOpen, windowBits, compressionLevel);
    }

    /// <summary>
    /// Sets up this DeflateStream to be used for Zlib Deflation/Compression
    /// </summary>
    [MemberNotNull(nameof(_stream))]
    internal void InitializeDeflater(Stream stream, bool leaveOpen, int windowBits, CompressionLevel compressionLevel)
    {
        Debug.Assert(stream != null);
        if (!stream.CanWrite)
            throw new ArgumentException("NotSupported_UnwritableStream - Stream does not support writing.", nameof(stream));

        _deflater = new Deflater(compressionLevel, windowBits);

        _stream = stream;
        _mode = CompressionMode.Compress;
        _leaveOpen = leaveOpen;
        InitializeBuffer();
    }

    //In case we decide to use this instead of initializing _buffer in the constructor as a regular byte array
    [MemberNotNull(nameof(_buffer))]
    private void InitializeBuffer()
    {
        Debug.Assert(_buffer == null);
        _buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
    }

    public Stream BaseStream => _stream;

    public override bool CanRead
    {
        get
        {
            if (_stream == null)
            {
                return false;
            }

            return (_mode == CompressionMode.Decompress && _stream.CanRead);
        }
    }

    public override bool CanWrite
    {
        get
        {
            if (_stream == null)
            {
                return false;
            }

            return (_mode == CompressionMode.Compress && _stream.CanWrite);
        }
    }

    public override bool CanSeek => false;

    public override long Length
    {
        get { throw new NotSupportedException("NotSupported - This operation is not supported."); }
    }

    public override long Position
    {
        get { throw new NotSupportedException("NotSupported - This operation is not supported."); }
        set { throw new NotSupportedException("NotSupported - This operation is not supported."); }
    }

    public override void Flush()
    {
        EnsureNotDisposed();
        if (_mode == CompressionMode.Compress)
            FlushBuffers();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        EnsureNoActiveAsyncOperation();
        EnsureNotDisposed();

        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        return _mode != CompressionMode.Compress ?
            Task.CompletedTask :
            Core(cancellationToken);

        async Task Core(CancellationToken cancellationToken)
        {
            AsyncOperationStarting();
            try
            {
                Debug.Assert(_deflater != null && _buffer != null);

                // Compress any bytes left:
                await WriteDeflaterOutputAsync(cancellationToken).ConfigureAwait(false);

                // Pull out any bytes left inside deflater:
                bool flushSuccessful;
                do
                {
                    int compressedBytes;
                    flushSuccessful = _deflater.Flush(_buffer, out compressedBytes);
                    if (flushSuccessful)
                    {
                        await _stream.WriteAsync(new ReadOnlyMemory<byte>(_buffer, 0, compressedBytes), cancellationToken).ConfigureAwait(false);
                    }
                    Debug.Assert(flushSuccessful == (compressedBytes > 0));
                } while (flushSuccessful);

                // Always flush on the underlying stream
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                AsyncOperationCompleting();
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException("NotSupported - This operation is not supported.");
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("NotSupported - This operation is not supported.");
    }

    public override int ReadByte()
    {
        EnsureDecompressionMode();
        EnsureNotDisposed();
        // Sanity check
        // Try to read a single byte from zlib without allocating an array, pinning an array, etc.
        // If zlib doesn't have any data, fall back to the base stream implementation, which will do that.
        Debug.Assert(_inflater != null);
        byte b = default;
        return Read(new Span<byte>(ref b)) == 1 ? b : -1;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Debug.Assert(_inflater != null);

        //Input class referring to the stream passed through the constructor
        return Read(new Span<byte>(buffer, offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (GetType() != typeof(DeflateStream))
        {
            // DeflateStream is not sealed, and a derived type may have overridden Read(byte[], int, int) prior
            // to this Read(Span<byte>) overload being introduced.  In that case, this Read(Span<byte>) overload
            // should use the behavior of Read(byte[],int,int) overload.
            return base.Read(buffer);
        }
        else
        {
            //Read Core
            EnsureDecompressionMode();
            EnsureNotDisposed();
            Debug.Assert(_inflater != null);
            Debug.Assert(_buffer != null);
            int bytesRead;

            while (true)
            {
                // Try to decompress any data from the inflater into the caller's buffer.
                // If we're able to decompress any bytes, or if decompression is completed, we're done.
                bytesRead = _inflater.Inflate(buffer); //Todo o la mayoria se hace aqui - lo mas modular

                buffer = buffer.Slice(bytesRead); //This would have now the bytes that could have been read
                                                  // If BytesRead (input available) < buffer.Length, then the slice will be smaller thant the original
                                                  // else, it would be same size - being the input either all decompressed or just a buffer.Length part

                if (bytesRead != 0 || InflatorIsFinished)
                {
                    // if we finished decompressing, we can't have anything left in the outputwindow.
                    Debug.Assert(_inflater.AvailableOutput == 0, "We should have copied all stuff out!");
                    break; //Break outside of the loop
                }

                // We were unable to decompress any data.  If the inflater needs additional input
                // data to proceed, read some to populate it.
                if (_inflater.NeedsInput())
                {
                    int n = _stream.Read(_buffer, 0, _buffer.Length);
                    if (n <= 0)
                    {
                        // - Inflater didn't return any data although a non-empty output buffer was passed by the caller.
                        // - More input is needed but there is no more input available.
                        // - Inflation is not finished yet.
                        // - Provided input wasn't completely empty
                        // In such case, we are dealing with a truncated input stream.
                        if (s_useStrictValidation && !buffer.IsEmpty && !_inflater.Finished() && _inflater.NonEmptyInput())
                        {
                            ThrowTruncatedInvalidData();
                        }
                        break;
                    }
                    else if (n > _buffer.Length)
                    {
                        ThrowGenericInvalidData();
                    }
                    else
                    {
                        _inflater.SetInput(_buffer, 0, n);
                    }
                }

                if (buffer.IsEmpty)
                {
                    // The caller provided a zero-byte buffer.  This is typically done in order to avoid allocating/renting
                    // a buffer until data is known to be available.  We don't have perfect knowledge here, as _inflater.Inflate
                    // will return 0 whether or not more data is required, and having input data doesn't necessarily mean it'll
                    // decompress into at least one byte of output, but it's a reasonable approximation for the 99% case.  If it's
                    // wrong, it just means that a caller using zero-byte reads as a way to delay getting a buffer to use for a
                    // subsequent call may end up getting one earlier than otherwise preferred.
                    Debug.Assert(bytesRead == 0);
                    break;
                }

            }

            return bytesRead; 
        }
    }

    private bool InflatorIsFinished =>
        // If the stream is finished then we have a few potential cases here:
        // 1. DeflateStream => return
        // 2. GZipStream that is finished but may have an additional GZipStream appended => feed more input
        // 3. GZipStream that is finished and appended with garbage => return
        _inflater!.Finished() &&
        (!_inflater.IsGzipStream() || !_inflater.NeedsInput());

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
    }

    private void EnsureDecompressionMode()
    {
        if (_mode != CompressionMode.Decompress)
            ThrowCannotReadFromDeflateStreamException();

        static void ThrowCannotReadFromDeflateStreamException() =>
            throw new InvalidOperationException("CannotReadFromDeflateStream - Reading from the compression stream is not supported.");
    }

    private void EnsureCompressionMode()
    {
        if (_mode != CompressionMode.Compress)
            ThrowCannotWriteToDeflateStreamException();

        static void ThrowCannotWriteToDeflateStreamException() =>
            throw new InvalidOperationException("CannotWriteToDeflateStream - Writing to the compression stream is not supported.");
    }

    private static void ThrowGenericInvalidData() =>
        // The stream is either malicious or poorly implemented and returned a number of
        // bytes < 0 || > than the buffer supplied to it.
        throw new InvalidDataException("GenericInvalidData - Found invalid data while decoding.");

    private static void ThrowTruncatedInvalidData() =>
        throw new InvalidDataException("TruncatedData - Found truncated data while decoding.");

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? asyncCallback, object? asyncState) =>
        TaskToAsyncResult.Begin(ReadAsync(buffer, offset, count, CancellationToken.None), asyncCallback, asyncState);

    public override int EndRead(IAsyncResult asyncResult)
    {
        EnsureDecompressionMode();
        EnsureNotDisposed();
        return TaskToAsyncResult.End<int>(asyncResult);
    }

    private ValueTask<int> ReadAsyncInternal(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        EnsureDecompressionMode();
        EnsureNoActiveAsyncOperation();
        EnsureNotDisposed();

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        Interlocked.Increment(ref _asyncOperations);
        Debug.Assert(_inflater != null);
        bool startedAsyncWork = false; //Async operation starting
        try
        {
            // Try to read decompressed data in output buffer
            int bytesRead = _inflater.Inflate(buffer.Span);
            if (bytesRead != 0)
            {
                // If decompression output buffer is not empty, return immediately.
                return ValueTask.FromResult(bytesRead);
            }

            if (_inflater.Finished())
            {
                // end of compression stream
                return ValueTask.FromResult(0);
            }

            // If there is no data on the output buffer and we are not at
            // the end of the stream, we need to get more data from the base stream
            ValueTask<int> readTask = _stream!.ReadAsync(_buffer.AsMemory(), cancellationToken);
            startedAsyncWork = true;

            return ReadAsyncCore(readTask, buffer, cancellationToken);
        }
        finally
        {
            // if we haven't started any async work, decrement the counter to end the transaction
            if (!startedAsyncWork)
            {
                Interlocked.Decrement(ref _asyncOperations);
            }
        }
    }

    private async ValueTask<int> ReadAsyncCore(ValueTask<int> readTask, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        Debug.Assert(_inflater != null);
        try
        {
            EnsureDecompressionMode();
            EnsureNotDisposed();
            Debug.Assert(_buffer != null);
            //EnsureBufferInitialized(); //--> Initialization done on constructor, might change it later to this

            while (true)
            {
                int bytesRead = await readTask.ConfigureAwait(false);
                EnsureNotDisposed();

                if (bytesRead <= 0)
                {
                    // This indicates the base stream has received EOF
                    return 0;
                }
                else if (bytesRead > _buffer.Length)
                {
                    // The stream is either malicious or poorly implemented and returned a number of
                    // bytes larger than the buffer supplied to it.
                    throw new InvalidDataException("GenericInvalidData - Found invalid data while decoding.");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Feed the data from base stream into decompression engine
                _inflater.SetInput(_buffer, 0, bytesRead);
                bytesRead = _inflater.Inflate(buffer.Span);

                if (bytesRead == 0 && !_inflater.Finished())
                {
                    // We could have read in head information and didn't get any data.
                    // Read from the base stream again.
                    readTask = _stream!.ReadAsync(_buffer.AsMemory(), cancellationToken);
                }
                else
                {
                    return bytesRead;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _asyncOperations);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        // We use this checking order for compat to earlier versions:
        if (_asyncOperations != 0)
            throw new InvalidOperationException("InvalidBeginCall");

        ValidateBufferArguments(buffer, offset, count);
        EnsureNotDisposed();
        //Passing the buffer portion required by the user to be filled with the uncompessed data
        return ReadAsyncInternal(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // We use this checking order for compat to earlier versions:
        if (_asyncOperations != 0)
            throw new InvalidOperationException("InvalidBeginCall");

        EnsureNotDisposed();

        return ReadAsyncInternal(buffer, cancellationToken);
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        WriteCore(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    public override void WriteByte(byte value)
    {
        if (GetType() != typeof(DeflateStream))
        {
            // DeflateStream is not sealed, and a derived type may have overridden Write(byte[], int, int) prior
            // to this WriteByte override being introduced.  In that case, this WriteByte override
            // should use the behavior of the Write(byte[],int,int) overload.
            base.WriteByte(value);
        }
        else
        {
            WriteCore(new ReadOnlySpan<byte>(in value));
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (GetType() != typeof(DeflateStream))
        {
            // DeflateStream is not sealed, and a derived type may have overridden Write(byte[], int, int) prior
            // to this Write(ReadOnlySpan<byte>) overload being introduced.  In that case, this Write(ReadOnlySpan<byte>) overload
            // should use the behavior of Write(byte[],int,int) overload.
            base.Write(buffer);
        }
        else
        {
            WriteCore(buffer);
        }
    }

    internal void WriteCore(ReadOnlySpan<byte> buffer)
    {
        EnsureCompressionMode();
        EnsureNotDisposed();

        Debug.Assert(_deflater != null);
        // Write compressed the bytes we already passed to the deflater:
        WriteDeflaterOutput();

        _deflater.SetInput(buffer);
        WriteDeflaterOutput();
        _wroteBytes = true;

    }

    private void WriteDeflaterOutput()
    {
        Debug.Assert(_deflater != null && _buffer != null);
        while (!_deflater.NeedsInput())
        {
            int compressedBytes = _deflater.GetDeflateOutput(_buffer);
            if (compressedBytes > 0)
            {
                _stream.Write(_buffer, 0, compressedBytes);
            }
        }
    }

    // This is called by Flush:
    private void FlushBuffers()
    {
        if (_wroteBytes)
        {
            // Compress any bytes left:
            WriteDeflaterOutput();

            Debug.Assert(_deflater != null && _buffer != null);
            // Pull out any bytes left inside deflater:
            bool flushSuccessful;
            do
            {
                int compressedBytes;
                flushSuccessful = _deflater.Flush(_buffer, out compressedBytes);
                if (flushSuccessful)
                {
                    _stream.Write(_buffer, 0, compressedBytes);
                }
                Debug.Assert(flushSuccessful == (compressedBytes > 0));
            } while (flushSuccessful);
        }

        // Always flush on the underlying stream
        _stream.Flush();
    }

    // This is called by Dispose:
    private void PurgeBuffers(bool disposing)
    {
        if (!disposing)
            return;

        if (_stream == null)
            return;

        if (_mode != CompressionMode.Compress)
            return;

        Debug.Assert(_deflater != null && _buffer != null);
        // Some deflaters (e.g. ZLib) write more than zero bytes for zero byte inputs.
        // This round-trips and we should be ok with this, but our legacy managed deflater
        // always wrote zero output for zero input and upstack code (e.g. ZipArchiveEntry)
        // took dependencies on it. Thus, make sure to only "flush" when we actually had
        // some input:
        if (_wroteBytes)
        {
            // Compress any bytes left
            WriteDeflaterOutput();

            // Pull out any bytes left inside deflater:
            bool finished;
            do
            {
                int compressedBytes;
                finished = _deflater.Finish(_buffer, out compressedBytes);

                if (compressedBytes > 0)
                    _stream.Write(_buffer, 0, compressedBytes);
            } while (!finished);
        }
        else
        {
            // In case of zero length buffer, we still need to clean up the native created stream before
            // the object get disposed because eventually ManagedZLib.ReleaseHandle will get called during
            // the dispose operation and although it frees the stream but it return error code because the
            // stream state was still marked as in use. The symptoms of this problem will not be seen except
            // if running any diagnostic tools which check for disposing safe handle objects
            bool finished;
            do
            {
                finished = _deflater.Finish(_buffer, out _);
            } while (!finished);
        }
    }

    private async ValueTask PurgeBuffersAsync()
    {
        // Same logic as PurgeBuffers, except with async counterparts.

        if (_stream == null)
            return;

        if (_mode != CompressionMode.Compress)
            return;

        Debug.Assert(_deflater != null && _buffer != null);
        // Some deflaters (e.g. ZLib) write more than zero bytes for zero byte inputs.
        // This round-trips and we should be ok with this, but our legacy managed deflater
        // always wrote zero output for zero input and upstack code (e.g. ZipArchiveEntry)
        // took dependencies on it. Thus, make sure to only "flush" when we actually had
        // some input.
        if (_wroteBytes)
        {
            // Compress any bytes left
            await WriteDeflaterOutputAsync(default).ConfigureAwait(false);

            // Pull out any bytes left inside deflater:
            bool finished;
            do
            {
                int compressedBytes;
                finished = _deflater.Finish(_buffer, out compressedBytes);

                if (compressedBytes > 0)
                    await _stream.WriteAsync(new ReadOnlyMemory<byte>(_buffer, 0, compressedBytes)).ConfigureAwait(false);
            } while (!finished);
        }
        else
        {
            // In case of zero length buffer, we still need to clean up the native created stream before
            // the object get disposed because eventually ManagedZLib.ReleaseHandle will get called during
            // the dispose operation and although it frees the stream, it returns an error code because the
            // stream state was still marked as in use. The symptoms of this problem will not be seen except
            // if running any diagnostic tools which check for disposing safe handle objects.
            bool finished;
            do
            {
                finished = _deflater.Finish(_buffer, out _); //To check - Hay unos out left que hay que quitar
            } while (!finished);
        }
    }

    protected override void Dispose(bool disposing) //Vivi> This maybe be the only dispose we need, after handling streams
                                                    //Not one for de/inflater
    {
        try
        {
            PurgeBuffers(disposing);
        }
        finally
        {
            // Close the underlying stream even if PurgeBuffers threw.
            // Stream.Close() may throw here (may or may not be due to the same error).
            // In this case, we still need to clean up internal resources, hence the inner finally blocks.
            try
            {
                if (disposing && !_leaveOpen)
                    _stream?.Dispose();
            }
            finally
            {
                _stream = null!;

                try
                {
                    _deflater?.Dispose();
                    //_inflater?.Dispose(); - TBD
                }
                finally
                {
                    _deflater = null;
                    _inflater = null;

                    byte[]? buffer = _buffer;
                    if (buffer != null)
                    {
                        _buffer = null;
                        if (!AsyncOperationIsActive)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }

                    base.Dispose(disposing);
                }
            }
        }
    }

    public override ValueTask DisposeAsync()
    {
        return GetType() == typeof(DeflateStream) ?
            Core() :
            base.DisposeAsync();

        async ValueTask Core()
        {
            // Same logic as Dispose(true), except with async counterparts.
            try
            {
                await PurgeBuffersAsync().ConfigureAwait(false);
            }
            finally
            {
                // Close the underlying stream even if PurgeBuffers threw.
                // Stream.Close() may throw here (may or may not be due to the same error).
                // In this case, we still need to clean up internal resources, hence the inner finally blocks.
                Stream stream = _stream;
                _stream = null!;
                try
                {
                    if (!_leaveOpen && stream != null)
                        await stream.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        _deflater?.Dispose();
                        //_inflater?.Dispose(); -TBD
                    }
                    finally
                    {
                        _deflater = null;
                        _inflater = null;

                        byte[]? buffer = _buffer;
                        if (buffer != null)
                        {
                            //Closest to buffer = null, since _buffer is no longer nullable 
                            _buffer = null;
                            if (!AsyncOperationIsActive)
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                    }
                }
            }
        }
    }

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? asyncCallback, object? asyncState) =>
        TaskToAsyncResult.Begin(WriteAsync(buffer, offset, count, CancellationToken.None), asyncCallback, asyncState);

    public override void EndWrite(IAsyncResult asyncResult)
    {
        EnsureCompressionMode();
        EnsureNotDisposed();
        TaskToAsyncResult.End(asyncResult);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsyncMemory(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (GetType() != typeof(DeflateStream))
        {
            // Ensure that existing streams derived from DeflateStream and that override WriteAsync(byte[],...)
            // get their existing behaviors when the newer Memory-based overload is used.
            return base.WriteAsync(buffer, cancellationToken);
        }
        else
        {
            return WriteAsyncMemory(buffer, cancellationToken);
        }
    }

    internal ValueTask WriteAsyncMemory(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        EnsureCompressionMode();
        EnsureNoActiveAsyncOperation();
        EnsureNotDisposed();

        return cancellationToken.IsCancellationRequested ?
            ValueTask.FromCanceled(cancellationToken) :
            Core(buffer, cancellationToken);

        async ValueTask Core(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            AsyncOperationStarting();
            try
            {
                await WriteDeflaterOutputAsync(cancellationToken).ConfigureAwait(false);

                Debug.Assert(_deflater != null);
                // Pass new bytes through deflater
                _deflater.SetInput(buffer.Span);

                await WriteDeflaterOutputAsync(cancellationToken).ConfigureAwait(false);

                _wroteBytes = true;
            }
            finally
            {
                AsyncOperationCompleting();
            }
        }
    }

    /// <summary>
    /// Writes the bytes that have already been deflated
    /// </summary>
    private async ValueTask WriteDeflaterOutputAsync(CancellationToken cancellationToken)
    {
        Debug.Assert(_deflater != null && _buffer != null);
        while (!_deflater.NeedsInput())
        {
            int compressedBytes = _deflater.GetDeflateOutput(_buffer);
            if (compressedBytes > 0)
            {
                await _stream.WriteAsync(new ReadOnlyMemory<byte>(_buffer, 0, compressedBytes), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override void CopyTo(Stream destination, int bufferSize)
    {
        ValidateCopyToArguments(destination, bufferSize);

        EnsureNotDisposed();
        if (!CanRead) throw new NotSupportedException();

        new CopyToStream(this, destination, bufferSize).CopyFromSourceToDestination();
    }

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        ValidateCopyToArguments(destination, bufferSize);

        EnsureNotDisposed();
        if (!CanRead) throw new NotSupportedException();
        EnsureNoActiveAsyncOperation();

        // Early check for cancellation
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }

        // Do the copy
        return new CopyToStream(this, destination, bufferSize, cancellationToken).CopyFromSourceToDestinationAsync();
    }

    private sealed class CopyToStream : Stream
    {
        private readonly DeflateStream _deflateStream;
        private readonly Stream _destination;
        private readonly CancellationToken _cancellationToken;
        private byte[] _arrayPoolBuffer;

        public CopyToStream(DeflateStream deflateStream, Stream destination, int bufferSize) :
            this(deflateStream, destination, bufferSize, CancellationToken.None)
        {
        }

        public CopyToStream(DeflateStream deflateStream, Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            Debug.Assert(deflateStream != null);
            Debug.Assert(destination != null);
            Debug.Assert(bufferSize > 0);

            _deflateStream = deflateStream;
            _destination = destination;
            _cancellationToken = cancellationToken;
            _arrayPoolBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        }

        public async Task CopyFromSourceToDestinationAsync()
        {
            _deflateStream.AsyncOperationStarting();
            try
            {
                Debug.Assert(_deflateStream._inflater != null);
                // Flush any existing data in the inflater to the destination stream.
                while (!_deflateStream._inflater.Finished())
                {
                    int bytesRead = _deflateStream._inflater.Inflate(_arrayPoolBuffer);
                    if (bytesRead > 0)
                    {
                        await _destination.WriteAsync(new ReadOnlyMemory<byte>(_arrayPoolBuffer, 0, bytesRead), _cancellationToken).ConfigureAwait(false);
                    }
                    else if (_deflateStream._inflater.NeedsInput())
                    {
                        // only break if we read 0 and ran out of input, if input is still available it may be another GZip payload
                        break;
                    }
                }

                // Now, use the source stream's CopyToAsync to push directly to our inflater via this helper stream
                await _deflateStream._stream.CopyToAsync(this, _arrayPoolBuffer.Length, _cancellationToken).ConfigureAwait(false);
                if (s_useStrictValidation && !_deflateStream._inflater.Finished())
                {
                    ThrowTruncatedInvalidData();
                }
            }
            finally
            {
                _deflateStream.AsyncOperationCompleting();

                ArrayPool<byte>.Shared.Return(_arrayPoolBuffer);
                _arrayPoolBuffer = null!;
            }
        }

        public void CopyFromSourceToDestination()
        {
            try
            {
                Debug.Assert(_deflateStream._inflater != null);
                // Flush any existing data in the inflater to the destination stream.
                while (!_deflateStream._inflater.Finished())
                {
                    int bytesRead = _deflateStream._inflater.Inflate(_arrayPoolBuffer);
                    if (bytesRead > 0)
                    {
                        _destination.Write(_arrayPoolBuffer, 0, bytesRead);
                    }
                    else if (_deflateStream._inflater.NeedsInput())
                    {
                        // only break if we read 0 and ran out of input, if input is still available it may be another GZip payload
                        break;
                    }
                }

                // Now, use the source stream's CopyToAsync to push directly to our inflater via this helper stream
                _deflateStream._stream.CopyTo(this, _arrayPoolBuffer.Length);
                if (s_useStrictValidation && !_deflateStream._inflater.Finished())
                {
                    ThrowTruncatedInvalidData();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(_arrayPoolBuffer);
                _arrayPoolBuffer = null!;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Debug.Assert(buffer != _arrayPoolBuffer);
            _deflateStream.EnsureNotDisposed();
            if (count <= 0)
            {
                return Task.CompletedTask;
            }
            else if (count > buffer.Length - offset)
            {
                // The buffer stream is either malicious or poorly implemented and returned a number of
                // bytes larger than the buffer supplied to it.
                return Task.FromException(new InvalidDataException("GenericInvalidData - Found invalid data while decoding."));
            }

            return WriteAsyncCore(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _deflateStream.EnsureNotDisposed();
            Memory<byte> memBuffer = buffer.ToArray(); //Change to just Memory for first iteration
            return WriteAsyncCore(memBuffer, cancellationToken);
        }
        // Vivi's notes> Changed from ReadOnlyMemory to just Memory for first iteration
        private async ValueTask WriteAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            Debug.Assert(_deflateStream._inflater is not null);

            // Feed the data from base stream into decompression engine.
            _deflateStream._inflater.SetInput(buffer);

            // While there's more decompressed data available, forward it to the buffer stream.
            while (!_deflateStream._inflater.Finished())
            {
                int bytesRead = _deflateStream._inflater.Inflate(new Span<byte>(_arrayPoolBuffer));
                if (bytesRead > 0)
                {
                    await _destination.WriteAsync(new ReadOnlyMemory<byte>(_arrayPoolBuffer, 0, bytesRead), cancellationToken).ConfigureAwait(false);
                }
                else if (_deflateStream._inflater.NeedsInput())
                {
                    // only break if we read 0 and ran out of input, if input is still available it may be another GZip payload
                    break;
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Debug.Assert(buffer != _arrayPoolBuffer);
            _deflateStream.EnsureNotDisposed();

            if (count <= 0)
            {
                return;
            }
            else if (count > buffer.Length - offset)
            {
                // The buffer stream is either malicious or poorly implemented and returned a number of
                // bytes larger than the buffer supplied to it.
                throw new InvalidDataException("GenericInvalidData - Found invalid data while decoding.");
            }

            Debug.Assert(_deflateStream._inflater != null);
            // Feed the data from base stream into the decompression engine.
            _deflateStream._inflater.SetInput(buffer, offset, count);

            // While there's more decompressed data available, forward it to the buffer stream.
            while (!_deflateStream._inflater.Finished())
            {
                int bytesRead = _deflateStream._inflater.Inflate(new Span<byte>(_arrayPoolBuffer));
                if (bytesRead > 0)
                {
                    _destination.Write(_arrayPoolBuffer, 0, bytesRead);
                }
                else if (_deflateStream._inflater.NeedsInput())
                {
                    // only break if we read 0 and ran out of input, if input is still available it may be another GZip payload
                    break;
                }
            }
        }

        public override bool CanWrite => true;
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }

    private bool AsyncOperationIsActive => _activeAsyncOperation != 0;

    private void EnsureNoActiveAsyncOperation()
    {
        if (AsyncOperationIsActive)
            ThrowInvalidBeginCall();
    }

    private void AsyncOperationStarting()
    {
        if (Interlocked.Exchange(ref _activeAsyncOperation, 1) != 0)
        {
            ThrowInvalidBeginCall();
        }
    }

    private void AsyncOperationCompleting() =>
        Volatile.Write(ref _activeAsyncOperation, 0);

    private static void ThrowInvalidBeginCall() =>
        throw new InvalidOperationException("InvalidBeginCall - Only one asynchronous reader or writer is allowed time at one time.");

    private static readonly bool s_useStrictValidation =
        AppContext.TryGetSwitch("System.IO.Compression.UseStrictValidation", out bool strictValidation) ? strictValidation : false;
}
