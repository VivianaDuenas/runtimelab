// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Microsoft.ManagedZLib.Benchmarks;

class CompressFiles
{
    private readonly string zipName = @".\resultZip.zip";
    public void MakeFile(string filename, string message)
    {
        //Creates a file
        FileStream fs = new (filename, FileMode.Create, FileAccess.ReadWrite);

        if (fs.CanWrite)
        {
            byte[] buffer = Encoding.Default.GetBytes(message);
            fs.Write(buffer,0,buffer.Length);
        }
        fs.Flush();
        fs.Close();
    }
    public void ZipIt (string directoryPath)
    {
        ZipFile.CreateFromDirectory(directoryPath,zipName);
    }
    public void UnzipIt(string ZipFilePath, string ExtractDestination)
    {

        ZipFile.CreateFromDirectory(ZipFilePath, ExtractDestination);
    }
}



