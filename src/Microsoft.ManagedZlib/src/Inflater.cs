// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using static Microsoft.ManagedZLib.ManagedZLib;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Microsoft.ManagedZLib;


/// <summary>
/// Provides a wrapper around the ZLib decompression API.
/// </summary>
internal sealed class Inflater
{
    // ------- Buffers
    private readonly OutputBuffer _output;
    private readonly InputBuffer _input;

    private IHuffmanTree? _literalLengthTree;// Literals
    private IHuffmanTree? _distanceTree;     // Distance
    private IHuffmanTree? _codeLengthTree;   // Length

    private int _literalLengthCodeCount; // Literals
    private int _distanceCodeCount;      // Distance
    private int _codeLengthCodeCount;    // Length

    private InflaterState _state;
    private BlockType _blockType;
    private int _finalByte;                             // Whether the end byte of the block has been reached
    private readonly byte[] _blockLengthBuffer = new byte[4]; //For LEN and NLEN(3.2.2 section in RFC1951) for uncompressed blocks
    private int _blockLength;

    // For decoding a compressed block
    // Alphabets used: Literals, length and distance
    // Extra bits for merging literal and length's alphabet
    private int _length; 
    private int _distanceCode;
    private int _extraBits;

    private int _loopCounter;
    private int _lengthCode;

    private int _codeArraySize;
    private readonly long _uncompressedSize;
    private long _currentInflatedCount;


    private readonly byte[] _codeList; // temporary array (with possibility of become a Span o Memory)
                                       // to store the code length for literal/Length and distance
    private readonly byte[] _codeLengthTreeCodeLength;

    private readonly bool _deflate64; //32k or 64k(true) or else(possible enum)



    private const int MinWindowBits = -15;              // WindowBits must be between -8..-15 to ignore the header, 8..15 for
    private const int MaxWindowBits = 47;               // zlib headers, 24..31 for GZip headers, or 40..47 for either Zlib or GZip

    private bool _nonEmptyInput;                        // Whether there is any non empty input
    private bool _finished;                             // Whether the end of the stream has been reached
    private bool _isDisposed;                           // Prevents multiple disposals
    private readonly int _windowBits;                   // The WindowBits parameter passed to Inflater construction
    private ZLibStreamHandle _zlibStream;    // The handle to the primary underlying zlib stream -- Vivi's note: TBD if necessary

    //Vivi's note> This structure, if necessary, will be re-design because before it was implemented
    // for pointer handling. - On the meantime there's a rough struct replacing the old one in ManagedZLib- 
    // I suspect is not necessary at all and was just for aligning c#'s behavior to the c library
    // [Commented old code] private ManagedZLib.BufferHandle _inputBufferHandle = default;            // The handle to the buffer that provides input to _zlibStream
    //private readonly long _uncompressedSize;
    //private long _currentInflatedCount;

    private object SyncLock => this;                    // Used to make writing to unmanaged structures atomic
                                                        
    public int AvailableOutput => _output.AvailableBytes;//This could be:  if we decide to make a struct instead of classes
                                                         //public int AvailableOutput => (int)_zlibStream.AvailOut;

    //-------------------- Bellow const tables used in decoding:
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
        _input = new InputBuffer();
        // Initializing window size according the type of deflate (window limits - 32k or 64k)
        _output = _deflate64? new OutputBuffer() : new OutputBuffer(windowBits);
        //Vivi's notes> Review if it's really necessary to reserve this much like an array of bytes
        _codeList = new byte[IHuffmanTree.MaxLiteralTreeElements + IHuffmanTree.MaxDistTreeElements];
        _codeLengthTreeCodeLength = new byte[IHuffmanTree.NumberOfCodeLengthTreeElements];

        Debug.Assert(windowBits >= MinWindowBits && windowBits <= MaxWindowBits);
        _finished = false;
        _nonEmptyInput = false;
        _isDisposed = false;
        _windowBits = windowBits;
        InflateInit(windowBits);
        _state = InflaterState.ReadingBFinal; // BFINAL - First bit of the block
        _uncompressedSize = uncompressedSize;
    }

    // Possibility of branching out from 32K to 64K as the output window limit depending on the bool
    //Maybe it should be an enum since these are all the possible types in archie
    //{ Stored = 0x0, Deflate = 0x8, Deflate64 = 0x9, BZip2 = 0xC, LZMA = 0xE }
    internal Inflater(bool deflate64, int windowBits, long uncompressedSize = -1) : this(windowBits, uncompressedSize)
    {
        _deflate64= deflate64;
    }

    /// <summary>
    /// Returns true if the end of the stream has been reached.
    /// </summary>
    public bool Finished() =>  _state == InflaterState.Done || _state == InflaterState.VerifyingFooter;

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
        int countCopied = 0;
        do
        {
            int bytesRead = 0;
            if (_uncompressedSize == -1)
            {
                // -----------------------------Note - in progress ----------------------------
                //Read Output will be de wrapper for error checking and _output.CopyTo()
                bytesRead = ReadOutput(bufferBytes, bytesRead); 
            }
            else
            {
                if (_uncompressedSize > _currentInflatedCount)
                {
                    int newLength = (int)Math.Min(bufferBytes.Length, _uncompressedSize - _currentInflatedCount);
                    bufferBytes = bufferBytes.Slice(newLength);
                    bytesRead = ReadOutput(bufferBytes, bytesRead); //Vivi's notes> Here you would pass a slice of the Span
                    _currentInflatedCount += bytesRead;
                }
                else
                {
                    //Done reading input
                    _state = InflaterState.Done;
                    _output.ClearBytesUsed();
                }
            } //Pending to add the decode() -------------------------- in progress ------------
        } while (!Finished() && Decode()) ; //Will return 0 when more input is need

        return countCopied;
    }

    private int ReadOutput(Span<byte> buffer, int bytesRead)
    {
        //Vivi's notes> Here we will pass a Span and take away "length" parameter
        if (ReadInflateOutput(buffer, bytesCopied FlushCode.NoFlush, out bytesRead) == ErrorCode.StreamEnd)
        {
            if (!_input.NeedsInput() && IsGzipStream())
            {
                _finished = ResetStreamForLeftoverInput();
            }
            else
            {
                _finished = true;
            }
        }
        return bytesRead;
    }

    /// <summary>
    /// Wrapper around the ZLib inflate function, configuring the stream appropriately.
    /// </summary>
    private ErrorCode ReadInflateOutput(Span<byte> buffer, int length, FlushCode flushCode, out int bytesRead)
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
    private ErrorCode Inflate(ManagedZLib.FlushCode flushCode)
    {
        ErrorCode errC;
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
            case ErrorCode.Ok:           // progress has been made inflating
            case ErrorCode.StreamEnd:    // The end of the input stream has been reached
                return errC;

            case ErrorCode.BufError:     // No room in the output buffer - inflate() can be called again with more space to continue
                return errC;

            case ErrorCode.MemError:     // Not enough memory to complete the operation
                throw new ZLibException("ZLibErrorNotEnoughMemory - The underlying compression routine could not reserve sufficient memory.", 
                    "inflate_", (int)errC, _zlibStream.GetErrorMessage());

            case ErrorCode.DataError:    // The input data was corrupted (input stream not conforming to the zlib format or incorrect check value)
                throw new InvalidDataException("UnsupportedCompression - The archive entry was compressed using an unsupported compression method.");

            case ErrorCode.StreamError:  //the stream structure was inconsistent (for example if next_in or next_out was NULL),
                throw new ZLibException("ZLibErrorInconsistentStream - The stream state of the underlying compression routine is inconsistent.",
                    "inflate_", (int)errC, _zlibStream.GetErrorMessage());

            default:
                throw new ZLibException("ZLibErrorUnexpected - The underlying compression routine returned an unexpected error code.", 
                    "inflate_", (int)errC, _zlibStream.GetErrorMessage());
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
        Debug.Assert(!_input.NeedsInput());
        Debug.Assert(IsGzipStream());

        lock (SyncLock)
        {
            byte[] nextIn = _zlibStream.NextIn;
            uint nextAvailIn = _zlibStream.AvailIn;

            // Check the leftover bytes to see if they start with he gzip header ID bytes
            if (nextIn[0] != GZip_Header_ID1 || (nextAvailIn > 1 && nextIn[1] != GZip_Header_ID2))
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

    /// <summary>
    /// It would have created a ZStream to handle inflation
    /// BUT now it creates the output buffers instead
    /// </summary>
    [MemberNotNull(nameof(_zlibStream))]
    private void InflateInit(int windowBits)
    {
        // Vivi's notes(ES)> Lit en el codigo de C, init solo llama  Init2_ 
        //--------------I'll have to check how to do this error checking because I do thing 
        //CreateZLibStreamForInflate id not necessary anymore, at least if everything is going to be done 
        //in the input/output classes

        ErrorCode error;
        try
        {
            //Vivi'a notes> Instead of calling ManagedZLib.CreateZLibStreamForInflate(out _zlibStream, windowBits);
            //It should be just called - InflateInit2_
            error = CreateZLibStreamForInflate(out _zlibStream, windowBits);
        }
        catch (Exception exception) // could not load the ZLib dll ------ Vivi's notes> Not useful anymore to a managed implementation
        {
            throw new ZLibException("ZLibErrorDLLLoadError - The underlying compression routine could not be loaded correctly.", exception);
        }
        // --------- Error checker----- Vivi's notes> This is basically (now) a wrapper for erro checking
        switch (error)
        {
            case ErrorCode.Ok:           // Successful initialization
                return;

            case ErrorCode.MemError:     // Not enough memory
                throw new ZLibException("ZLibErrorNotEnoughMemory - The underlying compression routine could not reserve sufficient memory.",
                    "inflateInit2_", (int)error, _zlibStream.GetErrorMessage());

            case ErrorCode.VersionError: //zlib library is incompatible with the version assumed
                throw new ZLibException("ZLibErrorVersionMismatch - The version of the underlying compression routine does not match expected version.",
                    "inflateInit2_", (int)error, _zlibStream.GetErrorMessage());

            case ErrorCode.StreamError:  // Parameters are invalid
                throw new ZLibException("ZLibErrorIncorrectInitParameters - The underlying compression routine received incorrect initialization parameters.",
                    "inflateInit2_", (int)error, _zlibStream.GetErrorMessage());

            default:
                throw new ZLibException("ZLibErrorUnexpected - The underlying compression routine returned an unexpected error code.",
                    "inflateInit2_", (int)error, _zlibStream.GetErrorMessage());
        }
    }

    internal bool IsGzipStream() => _windowBits >= 24 && _windowBits <= 31;

    public bool NonEmptyInput() => _nonEmptyInput;

    //With sanity checks
    public void SetInput(byte[] inputBuffer, int startIndex, int count)
    {
        Debug.Assert(_input.NeedsInput(), "We have something left in previous input!");
        Debug.Assert(inputBuffer != null);
        Debug.Assert(startIndex >= 0 && count >= 0 && count + startIndex <= inputBuffer.Length);

        SetInput(inputBuffer.AsMemory(startIndex, count));
    }

    public void SetInput(Memory<byte> inputBuffer)
    {
        Debug.Assert(_input.NeedsInput(), "We have something left in previous input!");

        if (inputBuffer.IsEmpty)
            return;

        lock (SyncLock)
        {
            _input.SetInput(inputBuffer);
            //Updating the ZStream AvailIn, probably redundant but still deciding on final structure for handling I/O
            _zlibStream.AvailIn = (uint)inputBuffer.Length;
            _finished = false;
            _nonEmptyInput = true;
        }
    }

    // -------------------- We the end of a block is reached ---------------------
    private bool DecodeBlock(out bool end_of_block_code_seen)
    {
        end_of_block_code_seen = false;

        int freeBytes = _output.FreeBytes;   // it is a little bit faster than frequently accessing the property
        while (freeBytes > 65536 && _deflate64==true)
        {
            // With Deflate64 we can have up to a 64kb length, so we ensure at least that much space is available
            // in the OutputWindow to avoid overwriting previous unflushed output data.

            int symbol;
            switch (_state)
            {
                case InflaterState.DecodeTop:
                    // decode an element from the literal tree

                    Debug.Assert(_literalLengthTree != null);
                    // TODO: optimize this!!!
                    symbol = _literalLengthTree.GetNextSymbol(_input);
                    if (symbol < 0)
                    {
                        // running out of input
                        return false;
                    }

                    if (symbol < 256)
                    {
                        // literal
                        _output.Write((byte)symbol);
                        --freeBytes;
                    }
                    else if (symbol == 256)
                    {
                        // end of block
                        end_of_block_code_seen = true;
                        // Reset state
                        _state = InflaterState.ReadingBFinal;
                        return true;
                    }
                    else
                    {
                        // length/distance pair
                        symbol -= 257;     // length code started at 257
                        if (symbol < 8)
                        {
                            symbol += 3;   // match length = 3,4,5,6,7,8,9,10
                            _extraBits = 0;
                        }
                        else if (!_deflate64 && symbol == 28) //deflateType is 64k
                        {
                            // extra bits for code 285 is 0
                            symbol = 258;             // code 285 means length 258
                            _extraBits = 0;
                        }
                        else
                        {
                            if ((uint)symbol >= ExtraLengthBits.Length)
                            {
                                throw new InvalidDataException("GenericInvalidData - Found invalid data while decoding.");
                            }
                            _extraBits = ExtraLengthBits[symbol];
                            Debug.Assert(_extraBits != 0, "We handle other cases separately!");
                        }
                        _length = symbol;
                        goto case InflaterState.HaveInitialLength;
                    }
                    break;

                case InflaterState.HaveInitialLength:
                    if (_extraBits > 0)
                    {
                        _state = InflaterState.HaveInitialLength;
                        int bits = _input.GetBits(_extraBits);
                        if (bits < 0)
                        {
                            return false;
                        }

                        if (_length < 0 || _length >= LengthBase.Length)
                        {
                            throw new InvalidDataException(SR.GenericInvalidData);
                        }
                        _length = LengthBase[_length] + bits;
                    }
                    _state = InflaterState.HaveFullLength;
                    goto case InflaterState.HaveFullLength;

                case InflaterState.HaveFullLength:
                    if (_blockType == BlockType.Dynamic)
                    {
                        Debug.Assert(_distanceTree != null);
                        _distanceCode = _distanceTree.GetNextSymbol(_input);
                    }
                    else
                    {
                        // get distance code directly for static block
                        _distanceCode = _input.GetBits(5);
                        if (_distanceCode >= 0)
                        {
                            _distanceCode = StaticDistanceTreeTable[_distanceCode];
                        }
                    }

                    if (_distanceCode < 0)
                    {
                        // running out input
                        return false;
                    }

                    _state = InflaterState.HaveDistCode;
                    goto case InflaterState.HaveDistCode;

                case InflaterState.HaveDistCode:
                    // To avoid a table lookup we note that for distanceCode > 3,
                    // extra_bits = (distanceCode-2) >> 1
                    int offset;
                    if (_distanceCode > 3)
                    {
                        _extraBits = (_distanceCode - 2) >> 1;
                        int bits = _input.GetBits(_extraBits);
                        if (bits < 0)
                        {
                            return false;
                        }
                        offset = DistanceBasePosition[_distanceCode] + bits;
                    }
                    else
                    {
                        offset = _distanceCode + 1;
                    }

                    _output.WriteLengthDistance(_length, offset);
                    freeBytes -= _length;
                    _state = InflaterState.DecodeTop;
                    break;

                default:
                    Debug.Fail("check why we are here!");
                    throw new InvalidDataException("UnknownState - Decoder is in some unknown state. This might be caused by corrupted data.");
            }
        }

        return true;
    }

}
