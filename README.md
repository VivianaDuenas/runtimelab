# .NET Runtime - Managed Zlib

This branch contains a template for standalone experiments, which means that experiments that are a library, which doesn't depend on runtime changes. This minimal template allows such experiments to avoid the overhead of having all the runtime, libraries and installer code.

This branch contains an experimental porting and comparison of the C Mark Adler's Zlib library used in System.IO.Compression to a C#. This is an exploration on whether we would benefit from switching our dependencies from madler/zlib to an own managed version. 
The evaluation of this experiment will be based on performance information using dotnet benchmark and madler/zlib  as baseline,  to see the main areas of opportunity of performance improvement. Because the idea is to implement a managed version, porting madler/zlib, without losing functionality in the way, this experiment covers a set of unit tests. It is necessary to have enough of the design for the information gathered to be reliable. 

## Useful links
• General expected functionality: [zlib 1.2.13 Manual](https://www.zlib.net/manual.html) 
• Specifications for the algorithms: [RFC 1951](https://datatracker.ietf.org/doc/html/rfc1951) | [RFC150](https://datatracker.ietf.org/doc/html/rfc1950) | [RFC1952 ](https://datatracker.ietf.org/doc/html/rfc1952)
                     RFC1951 for Deflate Algorithm in Deflate Stream. 
                     RFC150 and RFC1952 for ZLibStream and GzipStream that work on top of DeflateStream.

## .NET Foundation

.NET Runtime is a [.NET Foundation](https://www.dotnetfoundation.org/projects) project.

This project has adopted the code of conduct defined by the [Contributor Covenant](http://contributor-covenant.org/) to clarify expected behavior in our community. For more information, see the [.NET Foundation Code of Conduct](http://www.dotnetfoundation.org/code-of-conduct).

## License

.NET (including the runtime repo) is licensed under the [MIT](LICENSE.TXT) license.
