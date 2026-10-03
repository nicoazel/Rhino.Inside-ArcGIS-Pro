using System;
using System.Threading;
using System.Threading.Tasks;
using RhinoArcGIS.Core.Sync;
using Xunit;

namespace RhinoArcGIS.Core.Tests
{
    public class AsyncSerialGateTests
    {
        [Fact]
        public async Task Concurrent_operations_never_overlap()
        {
            var gate = new AsyncSerialGate();
            var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int active = 0;
            int maximumActive = 0;
            var activityGate = new object();

            async Task<int> Run(int value, Task wait, TaskCompletionSource<bool> entered)
            {
                return await gate.RunAsync(async () =>
                {
                    var nowActive = Interlocked.Increment(ref active);
                    lock (activityGate) maximumActive = Math.Max(maximumActive, nowActive);
                    entered.TrySetResult(true);
                    try
                    {
                        await wait;
                        return value;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                });
            }

            var first = Run(1, releaseFirst.Task, firstEntered);
            await firstEntered.Task;
            var second = Run(2, Task.CompletedTask, secondEntered);

            var prematureSecond = await Task.WhenAny(secondEntered.Task, Task.Delay(100));
            Assert.NotSame(secondEntered.Task, prematureSecond);

            releaseFirst.TrySetResult(true);
            Assert.Equal(new[] { 1, 2 }, await Task.WhenAll(first, second));
            Assert.Equal(1, maximumActive);
        }

        [Fact]
        public async Task Faulted_operation_releases_the_next_waiter()
        {
            var gate = new AsyncSerialGate();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gate.RunAsync<int>(() => Task.FromException<int>(new InvalidOperationException("expected"))));

            var result = await gate.RunAsync(() => Task.FromResult(42));
            Assert.Equal(42, result);
        }
    }
}
