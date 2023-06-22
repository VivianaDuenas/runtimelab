using BenchmarkDotNet.Running;
using System;


namespace Experiment.Benchmarks
{
    class programRun {
        static void Main(string[] args)
        {
            //para correr mi benchmark
            BenchmarkRunner.Run<ManagedZlibBenchmark>(); //la clase a testear
        }
    }   
}
