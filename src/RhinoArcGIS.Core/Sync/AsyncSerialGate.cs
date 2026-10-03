using System;
using System.Threading;
using System.Threading.Tasks;

namespace RhinoArcGIS.Core.Sync
{
    /// <summary>
    /// Runs asynchronous operations one at a time while allowing callers to await their turn.
    /// </summary>
    /// <remarks>
    /// Hosts use this at the outer coordination boundary so UI, automation, and peer entry points
    /// cannot mutate the same Rhino document and ArcGIS workspace concurrently.
    /// </remarks>
    public sealed class AsyncSerialGate
    {
        readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        /// <summary>Whether an operation is running or waiting its turn.</summary>
        public bool IsBusy => _semaphore.CurrentCount == 0;

        public async Task<T> RunAsync<T>(Func<Task<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
