using BenchmarkDotNet.Running;
using System;


namespace Microsoft.ManagedZLib.Benchmarks
{
    class programRun {
        static void Main(string[] args)
        {
            //para correr mi benchmark
            BenchmarkRunner.Run<ManagedZLibBenchmark>(); //la clase a testear
        }
    }   
}
