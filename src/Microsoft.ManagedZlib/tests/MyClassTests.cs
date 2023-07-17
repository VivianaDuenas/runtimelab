using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.ManagedZLib.Tests;

public class MyClassTests
{
    public Stream CreateStream(Stream stream, CompressionMode mode) => new DeflateStream(stream, mode);//For test1
    public Stream CreateStream(Stream stream, CompressionMode mode, bool leaveOpen) => new DeflateStream(stream, mode, leaveOpen);
    public Stream CreateStream(Stream stream, CompressionLevel level) => new DeflateStream(stream, level);
    public Stream CreateStream(Stream stream, CompressionLevel level, bool leaveOpen) => new DeflateStream(stream, level, leaveOpen);
    public Stream BaseStream(Stream stream) => ((DeflateStream)stream).BaseStream;
    protected string CompressedTestFile(string uncompressedPath) => Path.Combine("DeflateTestData", Path.GetFileName(uncompressedPath));
    
    public static IEnumerable<object[]> UncompressedTestFiles()
    {
        yield return new object[] { Path.Combine("UncompressedTestFiles", "TestDocument.doc") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "TestDocument.docx") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "TestDocument.pdf") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "TestDocument.txt") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "alice29.txt") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "asyoulik.txt") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "cp.html") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "fields.c") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "grammar.lsp") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "kennedy.xls") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "lcet10.txt") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "plrabn12.txt") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "ptt5") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "sum") };
        //yield return new object[] { Path.Combine("UncompressedTestFiles", "xargs.1") };
    }

    [Theory]
    [MemberData(nameof(UncompressedTestFiles))]
    public async Task Read(string testFile)
    {
        LocalMemoryStream uncompressedStream = await LocalMemoryStream.readAppFileAsync(testFile);
        LocalMemoryStream compressedStream = await LocalMemoryStream.readAppFileAsync(CompressedTestFile(testFile));
        using Stream decompressor = CreateStream(compressedStream, CompressionMode.Decompress);
        var decompressorOutput = new MemoryStream();

        int _bufferSize = 1024;
        var bytes = new byte[_bufferSize];
        bool finished = false;
        int retCount;
        while (!finished)
        {
            retCount = decompressor.Read(bytes, 0, _bufferSize);

            if (retCount != 0)
                decompressorOutput.Write(bytes, 0, retCount);
            else
                finished = true;
        }
        decompressor.Dispose();
        decompressorOutput.Position = 0;
        uncompressedStream.Position = 0;

        byte[] uncompressedStreamBytes = uncompressedStream.ToArray();
        byte[] decompressorOutputBytes = decompressorOutput.ToArray();

        Assert.Equal(uncompressedStreamBytes.Length, decompressorOutputBytes.Length);
        for (int i = 0; i < uncompressedStreamBytes.Length; i++)
        {
            Assert.Equal(uncompressedStreamBytes[i], decompressorOutputBytes[i]);
        }
    }

    [Fact]
    public void Test()
    {
        using MemoryStream original = new MemoryStream();

        byte[] originalBytes = new byte[] { 0x4C, 0x4C, 0x4F, 0x52, 0x41 };
        byte[] compressedBytes = new byte[10];
        byte[] finalBytes = new byte[10];
        original.Write(originalBytes);

        MemoryStream originalData = new();
        originalData.Write(originalBytes);
        originalData.Position = 0;

        MemoryStream compressedDestination = new();
        using (System.IO.Compression.DeflateStream compressor = new System.IO.Compression.DeflateStream(compressedDestination, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            originalData.CopyTo(compressor);
        }
        compressedDestination.Position = 0;
        Console.WriteLine($"CompressedDestination size: {compressedDestination.Length}");
        compressedDestination.ReadAtLeast(compressedBytes, 5, throwOnEndOfStream: false);
        compressedDestination.Position = 0;

        MemoryStream uncompressedDestination = new();
        using (var decompressor = new DeflateStream(compressedDestination, CompressionMode.Decompress, leaveOpen: true))
        {
            decompressor.CopyTo(uncompressedDestination);
        } //Cannot access a close stream error*
        uncompressedDestination.Position = 0;
        //Console.WriteLine($"UncompressedDestination size: {uncompressedDestination.Length}");
        //uncompressedDestination.ReadAtLeast(finalBytes, 5, throwOnEndOfStream: false);

        //Console.WriteLine($"Uncompressed: {Encoding.ASCII.GetString(originalBytes)}");
        //Console.WriteLine($"Compressed: ");
        //Print(compressedBytes);
        //Console.WriteLine($"Final: {Encoding.ASCII.GetString(finalBytes)}");

        originalData.Position = 0;

        byte[] uncompressedStreamBytes = originalData.ToArray();
        byte[] decompressorOutputBytes = uncompressedDestination.ToArray();

        Assert.Equal(uncompressedStreamBytes.Length, decompressorOutputBytes.Length);
        for (int i = 0; i < uncompressedStreamBytes.Length; i++)
        {
            Assert.Equal(uncompressedStreamBytes[i], decompressorOutputBytes[i]);
        }
    }

    private void Print(byte[] arr)
    {
        foreach (byte b in arr)
        {
            Console.Write($"{b}, ");
        }
        Console.WriteLine();
    }
}
