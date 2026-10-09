using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Raft.Core;

namespace Raft.Host;

/// <summary>
/// P13-04, phase 13 decision 2: a node's effect lists, executed off its loop. The loop hands each list
/// over and takes its next input; one thread runs the lists in the order they were handed over, each
/// to its end, so every persist is durable before anything after it in that list or any later one
/// (node-interface §4: a send or a response waits for every persist the node emitted before it, which
/// is what the simulator's world has always done). The queue holds <see cref="Capacity"/> lists; when
/// it is full the loop waits, which was the host's behaviour for every list until now. A failed write
/// or sync stops the queue at once: no later list runs, so nothing is sent or answered after a persist
/// whose state is unknown (P10-07), and the host is told. Vacuity risks: lists run out of order, or on
/// two threads, release a send before an earlier persist is durable (S-loop-1); lists that run after a
/// failure answer for writes that are not durable (S-loop-2); a loop that still waits for each list is
/// the old host under a new name (S-loop-3, at the host).
/// </summary>
public sealed class EffectThread : IAsyncDisposable
{
    /// <summary>The lists the queue holds before the loop waits (decision 2).</summary>
    public const int Capacity = 1024;

    private readonly Channel<(IReadOnlyList<Effect> List, long Queued)> _lists;
    private readonly Action<IReadOnlyList<Effect>> _execute;
    private readonly Action<Exception> _failed;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _thread;
    private readonly bool _measure;
    private readonly object _samplesLock = new();
    private readonly List<double> _persistWaits = [];
    private readonly List<double> _responseWaits = [];
    private volatile bool _stopped;
    private long _waitTicks;
    private long _executeTicks;
    private long _fullTicks;
    private long _executed;

    /// <param name="execute">Runs one list to its end (the host's <see cref="DiskExecutor.Execute"/>).</param>
    /// <param name="failed">Told, once, on the queue's thread, when a list's write or sync fails; no list runs after it.</param>
    /// <param name="measure">Keep each list's wait in the queue, for a load run's record (P13-04).</param>
    public EffectThread(Action<IReadOnlyList<Effect>> execute, Action<Exception> failed, int capacity = Capacity, bool measure = false)
    {
        _execute = execute;
        _failed = failed;
        _measure = measure;
        _lists = Channel.CreateBounded<(IReadOnlyList<Effect>, long)>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = true });
        _thread = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// Hands a list over, in order after every list handed before it, and returns without waiting for
    /// it to run, unless the queue is full. False once the queue has stopped (a failure, or disposal):
    /// the list will not run.
    /// </summary>
    public bool Enqueue(IReadOnlyList<Effect> effects)
    {
        if (_stopped)
        {
            return false;
        }

        var item = (effects, Stopwatch.GetTimestamp());
        if (_lists.Writer.TryWrite(item))
        {
            return true;
        }

        var full = Stopwatch.GetTimestamp();
        try
        {
            _lists.Writer.WriteAsync(item, _stop.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
        {
            return false;
        }
        finally
        {
            Interlocked.Add(ref _fullTicks, Stopwatch.GetTimestamp() - full);
        }

        return true;
    }

    /// <summary>The queue's thread so far: blocked waiting for a list, executing lists, the lists executed; and the loop's time blocked on a full queue.</summary>
    public (double WaitMicros, double ExecuteMicros, long Lists, double FullMicros) Times =>
        (Bench.Micros(Interlocked.Read(ref _waitTicks)), Bench.Micros(Interlocked.Read(ref _executeTicks)), Interlocked.Read(ref _executed), Bench.Micros(Interlocked.Read(ref _fullTicks)));

    /// <summary>Each measured list's wait in the queue, from its hand-over to its start, in µs: those holding a persist, and those holding a client response (a list may hold both).</summary>
    public (double[] Persist, double[] Response) Waits
    {
        get
        {
            lock (_samplesLock)
            {
                return ([.. _persistWaits], [.. _responseWaits]);
            }
        }
    }

    /// <summary>Stops at once, as a killed process does: the list running finishes its current effect's call, and no later list runs.</summary>
    public async ValueTask DisposeAsync()
    {
        _stopped = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        _lists.Writer.TryComplete();
        await _thread.ConfigureAwait(false);
        _stop.Dispose();
    }

    private void Run()
    {
        var reader = _lists.Reader;
        while (!_stopped)
        {
            var waiting = Stopwatch.GetTimestamp();
            bool ready;
            try
            {
                ready = reader.WaitToReadAsync(_stop.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Interlocked.Add(ref _waitTicks, Stopwatch.GetTimestamp() - waiting);
            if (!ready)
            {
                return;
            }

            while (!_stopped && reader.TryRead(out var item))
            {
                var start = Stopwatch.GetTimestamp();
                if (_measure)
                {
                    Sample(item.List, Bench.Micros(start - item.Queued));
                }

                try
                {
                    _execute(item.List);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // P10-07, decision 7: a sync that fails leaves the file's state unknown (a retried sync can
                    // report success over lost pages), so nothing after it runs: not the rest of this list,
                    // and not any list queued behind it.
                    _stopped = true;
                    _lists.Writer.TryComplete();
                    _failed(e);
                    return;
                }

                Interlocked.Add(ref _executeTicks, Stopwatch.GetTimestamp() - start);
                Interlocked.Increment(ref _executed);
            }
        }
    }

    private void Sample(IReadOnlyList<Effect> list, double micros)
    {
        var (persist, response) = (false, false);
        foreach (var e in list)
        {
            persist |= e is PersistAppend;
            response |= e is ClientResponse;
        }

        if (!persist && !response)
        {
            return;
        }

        lock (_samplesLock)
        {
            if (persist && _persistWaits.Count < 1_000_000)
            {
                _persistWaits.Add(micros);
            }

            if (response && _responseWaits.Count < 1_000_000)
            {
                _responseWaits.Add(micros);
            }
        }
    }
}
