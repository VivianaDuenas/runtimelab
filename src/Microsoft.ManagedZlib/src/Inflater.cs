// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;

namespace Microsoft.ManagedZLib
{
    /// <summary>
    /// Provides a wrapper around the ZLib decompression API.
    /// </summary>
    internal sealed class Inflater : IDisposable
    {
        private const int MinWindowBits = -15;              // WindowBits must be between -8..-15 to ignore the header, 8..15 for
        private const int MaxWindowBits = 47;               // zlib headers, 24..31 for GZip headers, or 40..47 for either Zlib or GZip

        private bool _nonEmptyInput;                        // Whether there is any non empty input
        private bool _finished;                             // Whether the end of the stream has been reached
        private bool _isDisposed;                           // Prevents multiple disposals
        private readonly int _windowBits;                   // The WindowBits parameter passed to Inflater construction
        private ManagedZLib.ZLibStreamHandle _zlibStream;    // The handle to the primary underlying zlib stream
        private MemoryHandle _inputBufferHandle;            // The handle to the buffer that provides input to _zlibStream
        private readonly long _uncompressedSize;
        private long _currentInflatedCount;

        private object SyncLock => this;                    // Used to make writing to unmanaged structures atomic

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

        // Vivi's note> Took AvailOut pointer out
        //This is important for the knowing the state of the output buffer
        // BUT further investigation needed for how (best way) to implement it in the managed version
        //public int AvailableOutput => (int)_zlibStream.AvailOut;

        /// <summary>
        /// Returns true if the end of the stream has been reached.
        /// </summary>
        public bool Finished() => _finished; //Este tipo de flags son importantes aunque maybe desde otra perspectiva(no ptrs)

        //Checking not to pass a null output buffer 
        public bool Inflate(byte[] bytes)
        {
            //Validating output buffer is not null
            return false;
        }

        public unsafe int Inflate(byte[] bytes, int offset, int length)
        {
            //El bufPtr de antes puede ser solo un int con la posicion
            // If Inflate is called on an invalid or unready inflater, return 0 to indicate no bytes have been read.
            if (length == 0)
                return 0;

            Debug.Assert(null != bytes, "Can't pass in a null output buffer!");
            fixed ( Span<byte> buffer = bytes)
            {
                return InflateVerified(buffer+offset, length); //Necesita una posicion (localidad) de inicio + length
            }
        }

        //Vivi's notes: We'll use span instead of pointer to bytes
        public unsafe int Inflate(Span<byte> destination)
        {
            // If Inflate is called on an invalid or unready inflater, return 0 to indicate no bytes have been read.
            if (destination.Length == 0)
                return 0;

            fixed (byte* bufPtr = &MemoryMarshal.GetReference(destination))
            {
                return InflateVerified(bufPtr, destination.Length);
            }
        }

        public unsafe int InflateVerified(byte[] bytes, int length)
        {
            // State is valid; attempt inflation
            try
            {
                int bytesRead = 0;
                if (_uncompressedSize == -1)
                {
                    ReadOutput(bufPtr, length, out bytesRead);
                }
                else
                {
                    if (_uncompressedSize > _currentInflatedCount)
                    {
                        length = (int)Math.Min(length, _uncompressedSize - _currentInflatedCount);
                        ReadOutput(bufPtr, length, out bytesRead);
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
            finally
            {
                // Before returning, make sure to release input buffer if necessary:
                if (0 == _zlibStream.AvailIn && IsInputBufferHandleAllocated)
                {
                    DeallocateInputBufferHandle();
                }
            }
        }

        private unsafe void ReadOutput(byte[] buffer, int length, out int bytesRead)
        {
            if (ReadInflateOutput(buffer, length, ManagedZLib.FlushCode.NoFlush, out bytesRead) == ManagedZLib.ErrorCode.StreamEnd)
            {
                if (!NeedsInput() && IsGzipStream() && IsInputBufferHandleAllocated)
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
            Debug.Assert(IsInputBufferHandleAllocated);

            lock (SyncLock)
            {
                byte[] nextInPtr = _zlibStream.NextIn;
                Span<byte> nextInPointer = nextInPtr;
                uint nextAvailIn = _zlibStream.AvailIn;

                //-------------------------------------------------Vivi's notes(ES):
                //Help> Se que C# span no soporta log aritmetica como los pointer de c++
                // Como traduzco esto? *(b+1)
                // No creo que esto sea equivalente> MemoryMarshal.GetReference(nextInPointer) + 1
                // lo dejare pa que ocmpile pero hay que checarlo para la logica

                // Check the leftover bytes to see if they start with he gzip header ID bytes
                if (MemoryMarshal.GetReference(nextInPointer) != ManagedZLib.GZip_Header_ID1 || (nextAvailIn > 1 && MemoryMarshal.GetReference(nextInPointer) + 1 != ManagedZLib.GZip_Header_ID2))
                {
                    return true;
                }
                
                // Trash our existing zstream.
                //Vivi's note: DISPOSE  OF ZSTREAM *dispose method not yet imlemented

                // Create a new zstream
                InflateInit(_windowBits);

                // SetInput on the new stream to the bits remaining from the last stream
                _zlibStream.NextIn = nextInPtr;
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
            Debug.Assert(!IsInputBufferHandleAllocated);

            SetInput(inputBuffer.AsMemory(startIndex, count));
        }

        public void SetInput(ReadOnlyMemory<byte> inputBuffer)
        {
            Debug.Assert(NeedsInput(), "We have something left in previous input!");
            Debug.Assert(!IsInputBufferHandleAllocated);

            if (inputBuffer.IsEmpty)
                return;

            lock (SyncLock)
            {
                _inputBufferHandle = inputBuffer.Pin();
                //Vivi's note> Como no se como manejar los handles - so far se asocian con arreglos de byte
                // pero entonces si se quedara asi, deberia aniadir un toarray memoryHandle o ver si uso eso
                // Resumen--------------------TBD----------------------------
                //_zlibStream.NextIn = _inputBufferHandle.ToArray; //No se como poner la memoria pinned en my byte array
                _zlibStream.AvailIn = (uint)inputBuffer.Length;
                _finished = false;
                _nonEmptyInput = true;
            }
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                    //Vivi's note: DISPOSE override OF ZSTREAM dispose method *not yet imlemented
                    // Vivi's note(ES): Queda por ver como implementar los handles 
                    //y por ende, cómo hacer el dispose correcto de ellos

                    if (IsInputBufferHandleAllocated)
                    DeallocateInputBufferHandle();

                _isDisposed = true;
            }
        }

        // Dispose wrapper
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~Inflater()
        {
            Dispose(false);
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
                error = ManagedZLib.CreateZLibStreamForInflate(out _zlibStream, windowBits);
            }
            catch (Exception exception) // could not load the ZLib dll
            {
                throw new ZLibException("ZLibErrorDLLLoadError - The underlying compression routine could not be loaded correctly.", exception);
            }

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
        private ManagedZLib.ErrorCode ReadInflateOutput(byte[] buffer, int length, ManagedZLib.FlushCode flushCode, out int bytesRead)
        {
            lock (SyncLock)
            {
                _zlibStream.NextOut = buffer; // Vivi's note> Checar luego porque no se si lo dejare de tipo byte[]
                _zlibStream.AvailOut = (uint)length;

                ManagedZLib.ErrorCode errC = Inflate(flushCode);
                bytesRead = length - (int)_zlibStream.AvailOut;

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

        /// <summary>
        /// Frees the GCHandle being used to store the input buffer
        /// </summary>
        private void DeallocateInputBufferHandle()
        {
            Debug.Assert(IsInputBufferHandleAllocated);

            lock (SyncLock)
            {
                _zlibStream.AvailIn = 0;
                _zlibStream.NextIn = ManagedZLib.ZNullPtr;
                _inputBufferHandle.Dispose();
            }
        }

        private unsafe bool IsInputBufferHandleAllocated => _inputBufferHandle.Pointer != default;
    }
}
