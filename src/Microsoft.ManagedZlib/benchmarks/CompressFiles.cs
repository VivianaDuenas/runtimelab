using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Experiment.Benchmarks
{
    class CompressFiles
    {
        
        private string zipName = @".\resultZip.zip";
        public void MakeFile(string filename, string message)
        {
            //Crea o appendea al final del archivo
            FileStream fs = new FileStream(filename, FileMode.Create, FileAccess.ReadWrite);

            if (fs.CanWrite)
            {
                //byte[] buffer = Encoding.Default.GetBytes("Hello World");
                byte[] buffer = Encoding.Default.GetBytes(message);
                fs.Write(buffer,0,buffer.Length) ; //Desde donde hasta donde 
                //fs.Write(buffer);
            }
            fs.Flush();
            fs.Close();
        }
        public void ZipIt (string directoryPath)
        {
            //Si existe, llora
            // if (File.Exists(zipName))
            // {
            //     File.Delete(zipName);
            // }
            ZipFile.CreateFromDirectory(directoryPath,zipName);
        }
        public void UnzipIt(string ZipFilePath, string ExtractDestination)
        {

            ZipFile.CreateFromDirectory(ZipFilePath, ExtractDestination);
        }
        /*
        public static void Main(string[] args)
        {
            //Comentar varias linea Ctrl+K+C
            //Descomentar varias lineas comentadas Ctrl+K+U
            CompressFiles compressObj1 = new CompressFiles();

            DateTime dateTime = DateTime.Now;
            string message = dateTime.ToString() + " - Nos vamos sintiendo: ";
            Console.WriteLine("Como nos sentimos hoy?");
            message = message + Console.ReadLine();
            message = message + "\r\n";
            string directoryName = "otherfolder"; // verbatim pero pa que
            // Normalizes the path.
            string filename = "feelings.txt";
            filename = Path.GetFullPath(directoryName)+ @"\"+filename;
            compressObj1.MakeFile(filename,message); //Ya no es static
            Console.WriteLine("Tu log esta en prueba2>bin>Debug>net6.0");

            compressObj1.ZipIt(directoryName);

            //string zipName = @".\resultZip.zip";
            //ZipIt(filename,zipName);
            //Si existe, llora
            //if (File.Exists(zipName))
            //{
            //    File.Delete(zipName);
            //}
            //ZipFile.CreateFromDirectory(directoryName, zipName, CompressionLevel.Optimal, true); 
        }*/
    }

}



