// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Microsoft.ManagedZLib;


/// <summary>
/// Provides a wrapper around the ZLib decompression API.
/// </summary>
internal sealed class Inflater
{

    private const int MinWindowBits = -15;              // WindowBits must be between -8..-15 to ignore the header, 8..15 for
    private const int MaxWindowBits = 47;               // zlib headers, 24..31 for GZip headers, or 40..47 for either Zlib or GZip

    private bool _nonEmptyInput;                        // Whether there is any non empty input
    private bool _finished;                             // Whether the end of the stream has been reached
    private bool _isDisposed;                           // Prevents multiple disposals
    private readonly int _windowBits;                   // The WindowBits parameter passed to Inflater construction
    private ManagedZLib.ZLibStreamHandle _zlibStream;    // The handle to the primary underlying zlib stream -- Vivi's note: TBD if necessary

    //Vivi's note> This structure, if necessary, will be re-design because before it was implemented
    // for pointer handling. - On the meantime there's a rough struct replacing the old one in ManagedZLib- 
    // I suspect is not necessary at all and was just for aligning c#'s behavior to the c library
    // [Commented old code] private ManagedZLib.BufferHandle _inputBufferHandle = default;            // The handle to the buffer that provides input to _zlibStream
    private readonly long _uncompressedSize;
    private long _currentInflatedCount;

    private object SyncLock => this;                    // Used to make writing to unmanaged structures atomic
    public int AvailableOutput => (int)_zlibStream.AvailOut; //Vivi's notes> If in ZStream we decide to have Spans, this might not be needed anymore

    // const tables used in decoding:

    // The base length for length code 257 - 285.
    // The formula to get the real length for a length code is lengthBase[code - 257] + (value stored in extraBits)
    private static ReadOnlySpan<byte> LengthBase => new byte[]
    {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51,
            59, 67, 83, 99, 115, 131, 163, 195, 227, 3
    };

    // Extra bits for length code 257 - 285.
    private static ReadOnlySpan<byte> ExtraLengthBits => new byte[]
    {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3,
            3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 16
    }; //Vivi's notes> This come from RFC1951 - Extra bits table

    // The base distance for distance code 0 - 31
    // The real distance for a distance code is  distanceBasePosition[code] + (value stored in extraBits)
    private static ReadOnlySpan<ushort> DistanceBasePosition => new ushort[]
    {
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513,
            769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577, 32769, 49153
    }; //Vivi's notes> This come from RFC1951
    private static ReadOnlySpan<ushort> ExtraDistancePosotionBits => new ushort[]
    {
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513,
            769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577, 32769, 49153
    };
    // code lengths for code length alphabet is stored in following order
    private static ReadOnlySpan<byte> CodeOrder => new byte[] { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    private static ReadOnlySpan<byte> StaticDistanceTreeTable => new byte[]
    {
            0x00, 0x10, 0x08, 0x18, 0x04, 0x14, 0x0c, 0x1c, 0x02, 0x12, 0x0a, 0x1a,
            0x06, 0x16, 0x0e, 0x1e, 0x01, 0x11, 0x09, 0x19, 0x05, 0x15, 0x0d, 0x1d,
            0x03, 0x13, 0x0b, 0x1b, 0x07, 0x17, 0x0f, 0x1f
    };

    /// <summary>
    /// Initialized the Inflater with the given windowBits size
    /// </summary>
    internal Inflater(int windowBits, long uncompressedSize = -1)
    {
        Debug.Assert(windowBits >= MinWindowBits && windowBits <= MaxWindowBits);
        _finished = false;
        _nonEmptyInput = false;
        _isDisposed = false;
        _windowBits = windowBits;
        InflateInit(windowBits);
        _uncompressedSize = uncompressedSize;
    }

    /// <summary>
    /// Returns true if the end of the stream has been reached.
    /// </summary>
    public bool Finished() => _finished; //Este tipo de flags son importantes aunque maybe desde otra perspectiva(no ptrs)

    // Vivi's notes> If there's s need of a subset of the buffer,
    // instead of passing a length and offset along with the buffer (like before)
    // You would just slice it from he caller like this: spanUsed.Slice(offset, length)
    public int Inflate(Span<byte> buffer) 
    {
        int bytesRead = InflateVerified(buffer);

        // If Inflate is called on an invalid or unready inflater, return 0 to indicate no bytes have been read.
        if (buffer.Length == 0)
            return 0;

        //Vivi's notes> Sanity checks
        Debug.Assert(buffer != null, "Can't pass in a null output buffer!");
        Debug.Assert(bytesRead == 0 || bytesRead == 1);

        return bytesRead;//bytesRead != 0 in the caller that expects a bool
    }

    public int InflateVerified(Span<byte> bufferBytes)
    {
        // State is valid; attempt inflation
        // -- Vivi's notes: This State thing (Enum) is in ManagedZLib and I'm not sure if is needed
        // It might be informative but was mainly involved in the use of pointer before
            int bytesRead = 0;
            if (_uncompressedSize == -1)
            {
                //Vivi's notes> Here we could pass a Span and take away the length
                ReadOutput(bufferBytes, bufferBytes.Length, out bytesRead);
            }
            else
            {
                if (_uncompressedSize > _currentInflatedCount)
                {
                    int newLength = (int)Math.Min(bufferBytes.Length, _uncompressedSize - _currentInflatedCount);
                    ReadOutput(bufferBytes, newLength, out bytesRead); //Vivi's notes> Here you would pass a slice of the Span
                    _currentInflatedCount += bytesRead;
                }
                else
                {
                    _finished = true;
                    _zlibStream.AvailIn = 0;
                }
            }
            return bytesRead;
    }

    private void ReadOutput(Span<byte> buffer, int length, out int bytesRead)
    {
        //Vivi's notes> Here we will pass a Span and take away the *length parameter
        if (ReadInflateOutput(buffer, length, ManagedZLib.FlushCode.NoFlush, out bytesRead) == ManagedZLib.ErrorCode.StreamEnd)
        {
            if (!NeedsInput() && IsGzipStream())
            {
                _finished = ResetStreamForLeftoverInput();
            }
            else
            {
                _finished = true;
            }
        }
    }

    /// <summary>
    /// If this stream has some input leftover that hasn't been processed then we should
    /// check if it is another GZip file concatenated with this one.
    ///
    /// Returns false if the leftover input is another GZip data stream.
    /// </summary>
    private bool ResetStreamForLeftoverInput()
    {
        Debug.Assert(!NeedsInput());
        Debug.Assert(IsGzipStream());

        lock (SyncLock)
        {
            byte[] nextIn = _zlibStream.NextIn;
            uint nextAvailIn = _zlibStream.AvailIn;

            // Check the leftover bytes to see if they start with he gzip header ID bytes
            if (nextIn[0] != ManagedZLib.GZip_Header_ID1 || (nextAvailIn > 1 && nextIn[1] != ManagedZLib.GZip_Header_ID2))
            {
                return true;
            }

            // Trash our existing zstream.
            //_zlibStream.Dispose(); //Vivi's note: DISPOSE  OF ZSTREAM *dispose method not yet implemented

            // Create a new zstream
            InflateInit(_windowBits); //Vivi's notes: method TBD - I imagine here is where the ZStream gets modified  
                                      // So bellow, the zLibStream vars are expected to be updated in terms of that

            // SetInput on the new stream to the bits remaining from the last stream
            _zlibStream.NextIn = nextIn;
            _zlibStream.AvailIn = nextAvailIn;
            _finished = false;
        }

        return false;
    }

    internal bool IsGzipStream() => _windowBits >= 24 && _windowBits <= 31;

    public bool NeedsInput() => _zlibStream.AvailIn == 0;

    public bool NonEmptyInput() => _nonEmptyInput;

    public void SetInput(byte[] inputBuffer, int startIndex, int count)
    {
        Debug.Assert(NeedsInput(), "We have something left in previous input!");
        Debug.Assert(inputBuffer != null);
        Debug.Assert(startIndex >= 0 && count >= 0 && count + startIndex <= inputBuffer.Length);

        SetInput(inputBuffer.AsMemory(startIndex, count));
    }

    public void SetInput(ReadOnlyMemory<byte> inputBuffer)
    {
        Debug.Assert(NeedsInput(), "We have something left in previous input!");

        if (inputBuffer.IsEmpty)
            return;

        lock (SyncLock)
        {
            _zlibStream.AvailIn = (uint)inputBuffer.Length;
            _finished = false;
            _nonEmptyInput = true;
        }
    }

    /// <summary>
    /// Creates the ZStream that will handle inflation.
    /// </summary>
    [MemberNotNull(nameof(_zlibStream))]
    private void InflateInit(int windowBits)
    {
        ManagedZLib.ErrorCode error;
        try
        {
            //Vivi'a notes> Instead of calling ManagedZLib.CreateZLibStreamForInflate(out _zlibStream, windowBits);
            //It should be just called - InflateInit2_
            error = ManagedZLib.CreateZLibStreamForInflate(out _zlibStream, windowBits);
        }
        catch (Exception exception) // could not load the ZLib dll ------ Vivi's notes> Not useful anymore to a managed implementation
        {
            throw new ZLibException("ZLibErrorDLLLoadError - The underlying compression routine could not be loaded correctly.", exception);
        }
        // --------- Error checker----- Vivi's notes> This is basically (now) a wrapper for erro checking
        switch (error)
        {
            case ManagedZLib.ErrorCode.Ok:           // Successful initialization
                return;

            case ManagedZLib.ErrorCode.MemError:     // Not enough memory
                throw new ZLibException("ZLibErrorNotEnoughMemory - The underlying compression routine could not reserve sufficient memory.",
                    "inflateInit2_", (int)error, _zlibStream.GetErrorMessage());

            case ManagedZLib.ErrorCode.VersionError: //zlib library is incompatible with the version assumed
                throw new ZLibException("ZLibErrorVersionMismatch - The version of the underlying compression routine does not match expected version.",
                    "inflateInit2_", (int)error, _zlibStream.GetErrorMessage());

            case ManagedZLib.ErrorCode.StreamError:  // Parameters are invalid
                throw new ZLibException("ZLibErrorIncorrectInitParameters - The underlying compression routine received incorrect initialization parameters.",
                    "inflateInit2_", (int)error, _zlibStream.GetErrorMessage());

            default:
                throw new ZLibException("ZLibErrorUnexpected - The underlying compression routine returned an unexpected error code.",
                    "inflateInit2_", (int)error, _zlibStream.GetErrorMessage());
        }
    }

    /// <summary>
    /// Wrapper around the ZLib inflate function, configuring the stream appropriately.
    /// </summary>
    private ManagedZLib.ErrorCode ReadInflateOutput(Span<byte> buffer, int length, ManagedZLib.FlushCode flushCode, out int bytesRead)
    {
        lock (SyncLock)
        {
            _zlibStream.NextOut = buffer.ToArray(); // Vivi's note> Check later if it's necessary to change the byte[] type
            _zlibStream.AvailOut = (uint)buffer.Length;

            ManagedZLib.ErrorCode errC = Inflate(flushCode); //Vivi's notes: Entry to managedZLib
            bytesRead = buffer.Length - AvailableOutput;

            return errC;
        }
    }

    /// <summary>
    /// Wrapper around the ZLib inflate function
    /// </summary>
    private ManagedZLib.ErrorCode Inflate(ManagedZLib.FlushCode flushCode)
    {
        ManagedZLib.ErrorCode errC;
        try
        {
            errC = _zlibStream.Inflate(flushCode);
        }
        catch (Exception cause) // could not load the Zlib DLL correctly
        {
            throw new ZLibException("ZLibErrorDLLLoadError - The underlying compression routine could not be loaded correctly.", cause);
        }
        switch (errC)
        {
            case ManagedZLib.ErrorCode.Ok:           // progress has been made inflating
            case ManagedZLib.ErrorCode.StreamEnd:    // The end of the input stream has been reached
                return errC;

            case ManagedZLib.ErrorCode.BufError:     // No room in the output buffer - inflate() can be called again with more space to continue
                return errC;

            case ManagedZLib.ErrorCode.MemError:     // Not enough memory to complete the operation
                throw new ZLibException("ZLibErrorNotEnoughMemory - The underlying compression routine could not reserve sufficient memory.", 
                    "inflate_", (int)errC, _zlibStream.GetErrorMessage());

            case ManagedZLib.ErrorCode.DataError:    // The input data was corrupted (input stream not conforming to the zlib format or incorrect check value)
                throw new InvalidDataException("UnsupportedCompression - The archive entry was compressed using an unsupported compression method.");

            case ManagedZLib.ErrorCode.StreamError:  //the stream structure was inconsistent (for example if next_in or next_out was NULL),
                throw new ZLibException("ZLibErrorInconsistentStream - The stream state of the underlying compression routine is inconsistent.",
                    "inflate_", (int)errC, _zlibStream.GetErrorMessage());

            default:
                throw new ZLibException("ZLibErrorUnexpected - The underlying compression routine returned an unexpected error code.", 
                    "inflate_", (int)errC, _zlibStream.GetErrorMessage());
        }
    }


    // Vivi's notes: We are not allocating memory as before,
    // so no need for the boolean IsInputBufferHandleAllocated var that was here before
    // Also, we're planning to use managed structs so deallocating data shouldn't be necessary
}
