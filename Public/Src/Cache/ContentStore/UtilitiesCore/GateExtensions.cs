﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace BuildXL.Cache.ContentStore.UtilitiesCore
{
    /// <nodoc />
    public static class GateExtensions
    {
        /// <summary>
        /// Convenience extension to allow tracing the time it takes to Wait a semaphore 
        /// </summary>
        public static async Task<TResult> GatedOperationAsync<TResult>(this SemaphoreSlim gate, Func<TimeSpan, int, Task<TResult>> func, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            await gate.WaitAsync(token);

            try
            {
                var currentCount = gate.CurrentCount;
                return await func(sw.Elapsed, currentCount);
            }
            finally
            {
                gate.Release();
            }
        }

    }
}
