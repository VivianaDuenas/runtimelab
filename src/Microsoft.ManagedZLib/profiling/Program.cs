// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.ManagedZLib.Benchmarks;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Microsoft.ManagedZLib;

internal class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TestM(MemoryStream expectedStream, CompressedFile compressedFile, DeflateStream decompressor)
    {
        //Console.WriteLine($"Iteration: {i}");
        decompressor.CopyTo(expectedStream);
        compressedFile.CompressedDataStream.Position = 0;
        expectedStream.Position = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NthFibonacciNumber(int number)
    {
        int firstNumber = 0, secondNumber = 1, nextNumber = 0;
        // To return the first Fibonacci number  
        if (number == 0)
            return firstNumber;
        for (int i = 2; i <= number; i++)
        {
            nextNumber = firstNumber + secondNumber;
            firstNumber = secondNumber;
            secondNumber = nextNumber;
        }
        return secondNumber;
    }

    private static void Main(string[] args)
    {
        int iter = 200_000_000;
        Stopwatch stopWatch = new Stopwatch();

        //Decrement the Nth Number by 1. This is because the series starts with 0
        int NthNumber=80;

        for (int i = 0; i < 1000; i++)
        {
            NthFibonacciNumber(NthNumber);
        }
        stopWatch.Start();
        for (int i = 0; i < iter; i++)
        {
            NthNumber = 40;
            NthFibonacciNumber(NthNumber);
        }
        stopWatch.Stop();

        // Elapsed time as a TimeSpan value.
        TimeSpan ts = stopWatch.Elapsed;
        Console.WriteLine($"Total time elapsed (ns): {ts.TotalNanoseconds}");
        double res = ts.TotalNanoseconds / iter;
        Console.WriteLine($"Time elapsed per iteration(ns): {res}");
    }
}