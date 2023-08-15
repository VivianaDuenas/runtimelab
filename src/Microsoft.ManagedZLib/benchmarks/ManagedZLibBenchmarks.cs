// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Running;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Microsoft.ManagedZLib.Benchmarks;

// Referring the dotnet/performance documentation (performance/docs/microbenchmark-design-guidelines.md)
// BenchmarkDotNet creates a type which derives from type with benchmarks. 
// So the type with benchmarks must not be sealed and it can NOT BE STATIC 
// and it has to BE PUBLIC. It also has to be a class (no structs support).
[EtwProfiler(performExtraBenchmarksRun:true)]
public class ManagedZLibBenchmark
{
    int firstNumber;
    int secondNumber;
    int nextNumber;

    [Params(20, 30, 40, 80, 100)]
    public int Number { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        firstNumber = 0; 
        secondNumber = 1; 
        nextNumber = 0;
    }

    [Benchmark]
    public int DecompressManaged()
    {
        
        // To return the first Fibonacci number  
        if (Number == 0)
            return firstNumber;
        for (int i = 2; i <= Number; i++)
        {
            nextNumber = firstNumber + secondNumber;
            firstNumber = secondNumber;
            secondNumber = nextNumber;
        }
        return secondNumber;
    }

    public class ProgramRun
    {
        public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(ProgramRun).Assembly).Run(args);
    }

}
