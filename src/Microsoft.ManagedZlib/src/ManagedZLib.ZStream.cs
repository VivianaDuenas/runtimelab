// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.ManagedZLib;

internal static partial class ZLibNative
{
    /// <summary>
    /// ZLib stream descriptor data structure
    /// Do not construct instances of <code>ZStream</code> explicitly.
    /// Always use <code>ZLibNative.DeflateInit2_</code> or <code>ZLibNative.InflateInit2_</code> instead.
    /// Those methods will wrap this structure into a <code>SafeHandle</code> and thus make sure that it is always disposed correctly.
    /// </summary>

}