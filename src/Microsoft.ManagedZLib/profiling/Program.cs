// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.ManagedZLib.Benchmarks;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Microsoft.ManagedZLib;

internal class Program
{

    static Stopwatch stopWatch = new Stopwatch();
    const string filename = "alice29.txt";
    static CompressedFile compressedFile = new( filename, System.IO.Compression.CompressionLevel.SmallestSize);
    static MemoryStream expectedStream = new();
    //System.IO.Compression.DeflateStream decompressor = new(compressedFile.CompressedDataStream, System.IO.Compression.CompressionMode.Decompress);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TestM()
    {
        DeflateStream decompressor = new(compressedFile.CompressedDataStream, CompressionMode.Decompress, leaveOpen:true);
        compressedFile.CompressedDataStream.Position = 0;
        expectedStream.Position = 0;
        decompressor.CopyTo(expectedStream);
        decompressor.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TestN()
    {
        System.IO.Compression.DeflateStream decompressor = new(compressedFile.CompressedDataStream, System.IO.Compression.CompressionMode.Decompress, leaveOpen:true);
        compressedFile.CompressedDataStream.Position = 0;
        expectedStream.Position = 0;
        decompressor.CopyTo(expectedStream);
        decompressor.Dispose();
    }


    private static void Main(string[] args)
    {
        int iter = 10_000_000;
        Console.WriteLine("In profiling ConsoleApp");
        Console.WriteLine($"For the file: {filename}");
        Console.WriteLine($"Uncompressed size: {compressedFile.UncompressedSize}");
        Console.WriteLine($"Compressed size: {compressedFile.CompressedSize}");
        compressedFile.CompressedDataStream.Position = 0;

        Console.WriteLine($"Environment.Version: {Environment.Version}");
        Console.WriteLine($"RuntimeInformation.FrameworkDescription: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"AssemblyFileVersion: {typeof(object).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version!}");

        for (int i = 0; i < 100; i++)
        {
            TestM();
        }

        stopWatch.Start();
        for (int i = 0; i < iter; i++)
        {
            TestM();
        }
        stopWatch.Stop();

        // Elapsed time as a TimeSpan value.
        TimeSpan ts = stopWatch.Elapsed;
        Console.WriteLine($"Total time elapsed (us): {ts.TotalMicroseconds}");
        double res = ts.TotalMicroseconds / iter;
        Console.WriteLine($"Time elapsed per iteration(us): {res}");
    }
}