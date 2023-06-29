// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Security;

using ZErrorCode = Microsoft.ManagedZLib.ManagedZLib.ErrorCode;
using ZFlushCode = Microsoft.ManagedZLib.ManagedZLib.FlushCode;

namespace Microsoft.ManagedZLib
{
    /// <summary>
    /// Provides a wrapper around the ZLib compression API.
    /// </summary>
    internal sealed class Deflater : IDisposable
    {
        private readonly ManagedZLib.ZLibStreamHandle _zlibStream;
        private ManagedZLib.BufferHandle _inputBufferHandle;
        private bool _isDisposed;
        private const int minWindowBits = -15;  // WindowBits must be between -8..-15 to write no header, 8..15 for a
        private const int maxWindowBits = 31;   // zlib header, or 24..31 for a GZip header

        // Note, DeflateStream or the deflater do not try to be thread safe.
        // The lock is just used to make writing to unmanaged structures atomic to make sure
        // that they do not get inconsistent fields that may lead to an unmanaged memory violation.
        // To prevent *managed* buffer corruption or other weird behaviour users need to synchronise
        // on the stream explicitly.
        private object SyncLock => this;

        internal Deflater(CompressionLevel compressionLevel, int windowBits)
        {
            Debug.Assert(windowBits >= minWindowBits && windowBits <= maxWindowBits);
            ManagedZLib.CompressionLevel zlibCompressionLevel;
            int memLevel;

            switch (compressionLevel)
            {
                // See the note in ManagedZLib.CompressionLevel for the recommended combinations.

                case CompressionLevel.Optimal:
                    zlibCompressionLevel = ManagedZLib.CompressionLevel.DefaultCompression;
                    memLevel = ManagedZLib.Deflate_DefaultMemLevel;
                    break;

                case CompressionLevel.Fastest:
                    zlibCompressionLevel = ManagedZLib.CompressionLevel.BestSpeed;
                    memLevel = ManagedZLib.Deflate_DefaultMemLevel;
                    break;

                case CompressionLevel.NoCompression:
                    zlibCompressionLevel = ManagedZLib.CompressionLevel.NoCompression;
                    memLevel = ManagedZLib.Deflate_NoCompressionMemLevel;
                    break;

                case CompressionLevel.SmallestSize:
                    zlibCompressionLevel = ManagedZLib.CompressionLevel.BestCompression;
                    memLevel = ManagedZLib.Deflate_DefaultMemLevel;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(compressionLevel));
            }

            ManagedZLib.CompressionStrategy strategy = ManagedZLib.CompressionStrategy.DefaultStrategy;

            ZErrorCode errC;
            try
            {
                errC = ManagedZLib.CreateZLibStreamForDeflate(out _zlibStream, zlibCompressionLevel,
                                                             windowBits, memLevel, strategy);
            }
            catch (Exception cause)
            {
                throw new ZLibException("ZLibErrorDLLLoadError - The underlying compression routine could not be loaded correctly.", cause);
            }

            switch (errC)
            {
                case ZErrorCode.Ok:
                    return;

                case ZErrorCode.MemError:
                    throw new ZLibException("ZLibErrorNotEnoughMemory - The underlying compression routine could not reserve sufficient memory.",
                        "deflateInit2_", (int)errC, _zlibStream.GetErrorMessage());

                case ZErrorCode.VersionError:
                    throw new ZLibException("ZLibErrorVersionMismatch - The version of the underlying compression routine does not match expected version.",
                        "deflateInit2_", (int)errC, _zlibStream.GetErrorMessage());

                case ZErrorCode.StreamError:
                    throw new ZLibException("ZLibErrorIncorrectInitParameters - The underlying compression routine received incorrect initialization parameters.",
                        "deflateInit2_", (int)errC, _zlibStream.GetErrorMessage());

                default:
                    throw new ZLibException("ZLibErrorUnexpected - The underlying compression routine returned an unexpected error code.", "deflateInit2_",
                        (int)errC, _zlibStream.GetErrorMessage());
            }
        }

        ~Deflater()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing) {
                    //_zlibStream.Dispose(); //Vivi's note: DISPOSE  OF ZSTREAM *dispose method not yet imlemented
                }

                DeallocateInputBufferHandle();
                _isDisposed = true;
            }
        }

        public bool NeedsInput() => 0 == _zlibStream.AvailIn;

        internal void SetInput(ReadOnlyMemory<byte> inputBuffer)
        {
            Debug.Assert(NeedsInput(), "We have something left in previous input!");
            if (0 == inputBuffer.Length)
            {
                return;
            }

            lock (SyncLock)
            {
                //Vivi's note(ES) > Aun hay que ver cómo será la estructura para el Handle
                //Aqui como que pide la referencia al arreglo para que lo tenga el handle (de manera segura con Memory) y el ZStream (NextIn)
                // _inputBufferHandle.tempHandle = inputBuffer.Pin();                                             

                _zlibStream.NextIn = _inputBufferHandle.Buffer; 
                _zlibStream.AvailIn = (uint)inputBuffer.Length;
            }
        }
        //Vivi's notes> This overloading might be repetitive
        internal void SetInput(Span<byte> inputBuffer, int count)
        {
            Debug.Assert(NeedsInput(), "We have something left in previous input!");
            Debug.Assert(inputBuffer != null);

            if (count == 0)
            {
                return;
            }

            lock (SyncLock)
            {
                _zlibStream.NextIn = inputBuffer.ToArray(); //Vivi's notes> We're picking 
                _zlibStream.AvailIn = (uint)count;
            }
        }

        internal int GetDeflateOutput(byte[] outputBuffer)
        {
            Debug.Assert(null != outputBuffer, "Can't pass in a null output buffer!");
            Debug.Assert(!NeedsInput(), "GetDeflateOutput should only be called after providing input");

            try
            {
                int bytesRead;
                ReadDeflateOutput(outputBuffer, ZFlushCode.NoFlush, out bytesRead);
                return bytesRead;
            }
            finally
            {
                // Before returning, make sure to release input buffer if necessary:
                if (0 == _zlibStream.AvailIn)
                {
                    DeallocateInputBufferHandle();
                }
            }
        }

        private ZErrorCode ReadDeflateOutput(byte[] outputBuffer, ZFlushCode flushCode, out int bytesRead)
        {
            Debug.Assert(outputBuffer?.Length > 0);

            lock (SyncLock)
            {
                _zlibStream.NextOut = outputBuffer;
                _zlibStream.AvailOut = (uint)outputBuffer.Length;

                ZErrorCode errC = Deflate(flushCode);
                bytesRead = outputBuffer.Length - (int)_zlibStream.AvailOut;

                return errC;
            }
        }

        internal bool Finish(byte[] outputBuffer, out int bytesRead)
        {
            Debug.Assert(null != outputBuffer, "Can't pass in a null output buffer!");
            Debug.Assert(outputBuffer.Length > 0, "Can't pass in an empty output buffer!");

            ZErrorCode errC = ReadDeflateOutput(outputBuffer, ZFlushCode.Finish, out bytesRead);
            return errC == ZErrorCode.StreamEnd;
        }

        /// <summary>
        /// Returns true if there was something to flush. Otherwise False.
        /// </summary>
        internal bool Flush(byte[] outputBuffer, out int bytesRead)
        {
            Debug.Assert(null != outputBuffer, "Can't pass in a null output buffer!");
            Debug.Assert(outputBuffer.Length > 0, "Can't pass in an empty output buffer!");
            Debug.Assert(NeedsInput(), "We have something left in previous input!");


            // Note: we require that NeedsInput() == true, i.e. that 0 == _zlibStream.AvailIn.
            // If there is still input left we should never be getting here; instead we
            // should be calling GetDeflateOutput.

            return ReadDeflateOutput(outputBuffer, ZFlushCode.SyncFlush, out bytesRead) == ZErrorCode.Ok;
        }

        private void DeallocateInputBufferHandle()
        {
            lock (SyncLock)
            {
                _zlibStream.AvailIn = 0;
                Array.Clear(_zlibStream.NextIn, 0, _zlibStream.NextIn.Length); // Vivi's notes(ES)> Aqui habia un _zlibStream = ZNullPtr.IntPtr.Zero
                //_inputBufferHandle.Dispose(); Vivi's note> Because we haven't decided on a struct yet
                // We aren't 100% if it's going to need a dispose()
                // Since we're expecting for everything to be "managed" the possibility to not having it and reuse
                //other managed structs is still open
            }
        }

        private ZErrorCode Deflate(ZFlushCode flushCode)
        {
            ZErrorCode errC;
            try
            {
                errC = _zlibStream.Deflate(flushCode);
            }
            catch (Exception cause)
            {
                throw new ZLibException("ZLibErrorDLLLoadError - The underlying compression routine could not be loaded correctly.", cause);
            }

            switch (errC)
            {
                case ZErrorCode.Ok:
                case ZErrorCode.StreamEnd:
                    return errC;

                case ZErrorCode.BufError:
                    return errC;  // This is a recoverable error

                case ZErrorCode.StreamError:
                    throw new ZLibException("ZLibErrorInconsistentStream - The stream state of the underlying compression routine is inconsistent.",
                        "deflate", (int)errC, _zlibStream.GetErrorMessage());

                default:
                    throw new ZLibException("ZLibErrorUnexpected - The underlying compression routine returned an unexpected error code.", "deflate",
                        (int)errC, _zlibStream.GetErrorMessage());
            }
        }
    }
}
