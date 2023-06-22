using BenchmarkDotNet.Attributes;
using System.IO; //para path


namespace Experiment.Benchmarks
{
    // Referring the dotnet/performance documentation (performance/docs/microbenchmark-design-guidelines.md)
    /* 
    BenchmarkDotNet creates a type which derives from type with benchmarks. 
    So the type with benchmarks must not be sealed and it can NOT BE STATIC 
    and it has to BE PUBLIC. It also has to be a class (no structs support).
    */
    public class ManagedZlibBenchmark{

       
        //private string message = "prueba Benchmarks";
        private static readonly CompressFiles compressObj = new CompressFiles();
        // Lo siguiente deberia estar en System.IO en FileUtils sino hay que aniadir la clase
        public static string GetTestFilePath() 
            => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        private static readonly string _rootDirPath = GetTestFilePath(); // no necesita creacion de obj porque es static
        private static readonly string _inputDirPath = Path.Combine(_rootDirPath, "inputdir"); //creando dir porque de alguna manera al correrlo no encuentra el que quiero
        private static readonly string _testDirPath = Path.Combine(_inputDirPath, "testdir");
        private static readonly string _testFilePath = Path.Combine(_inputDirPath, "file.txt");
        //private static readonly string directoryPath = _testDirPath;

        //[GlobalSetup]
        //public void Setup()
        //{
        //    string directoryName = "otherfolder";
        //    string filename = "feels.txt";
        //    filename = Path.GetFullPath(directoryName) + @"\" + filename;
        //    compressObj.MakeFile(filename, message);
        //}
        [GlobalSetup]
        public void Setup()
        {
            Directory.CreateDirectory(_testDirPath); // Creates all segments: root/inputdir/testdir
            File.Create(_testFilePath).Dispose();
            //Si existe, llora
            File.Delete(_testDirPath);
            // if (File.Exists(_testDirPath))
            // {
            //     File.Delete(_testDirPath);
            // }
        }

        [GlobalCleanup]
        public void Cleanup() => Directory.Delete(_rootDirPath, recursive: true);

        [Benchmark]
        public void zipProccess() { 
            //Sigue checando si el arhcivo existe dentro de ZipIt pero porque 
            //Cuando lo puse en el GlobalSetUp, tiro una excepcion de que el zip ya
            //existiaaaaa
            //Como si no lo hubiera checado y en ese caso eliminado en el globalsetup
            compressObj.ZipIt(_testDirPath);
            File.Delete(_testDirPath);
            //CHALLENGE: intentar quitar checeo sin que truene o se queje de que no se hace el chequeo
        }
        
    }

}