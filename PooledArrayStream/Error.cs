// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace PooledIO
{
    /// <summary>
    ///     Provides centralized methods for creating exceptions for System.IO.FileSystem.
    /// </summary>
    internal static class Error
    {
        internal static Exception GetStreamIsClosed()
        {
            return new ObjectDisposedException(null, SR.ObjectDisposed_StreamClosed);
        }

        internal static Exception GetWriteNotSupported()
        {
            return new NotSupportedException(SR.NotSupported_UnwritableStream);
        }
    }
}
