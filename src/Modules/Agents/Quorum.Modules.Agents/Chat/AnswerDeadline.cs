namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// The total time an answer may take, which stops running while the answer waits for a person (a confirmation, a question):
/// that wait belongs to the user, not to the model or the tools. A single <see cref="CancellationTokenSource"/> cannot be paused,
/// so each stretch of work (a model request, a tool call) asks for its own <see cref="Watch"/>, which fires when what is left of
/// the time runs out and is moved when the clock is paused or resumed.
/// </summary>
internal sealed class AnswerDeadline
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _total;
    private readonly long _started;
    private readonly List<Watch> _watches = [];
    private TimeSpan _paused;
    private long _pausedSince;
    private int _pauses;

    public AnswerDeadline(TimeSpan total, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _total = total;
        _clock = clock;
        _started = clock.GetTimestamp();
    }

    public bool IsUnbounded => _total == Timeout.InfiniteTimeSpan;

    /// <summary>What is left of the total time, never negative; <see cref="Timeout.InfiniteTimeSpan"/> when there is no limit.</summary>
    public TimeSpan Remaining
    {
        get
        {
            if (IsUnbounded)
            {
                return Timeout.InfiniteTimeSpan;
            }

            lock (_gate)
            {
                var left = _total - Elapsed();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary>The time ran out. It never has while paused: nothing is being spent.</summary>
    public bool IsExpired
    {
        get
        {
            if (IsUnbounded)
            {
                return false;
            }

            lock (_gate)
            {
                return _pauses == 0 && _total - Elapsed() <= TimeSpan.Zero;
            }
        }
    }

    /// <summary>Stops the clock until the returned scope is disposed. Pauses nest: the clock runs again when the last one ends.</summary>
    public IDisposable Pause()
    {
        lock (_gate)
        {
            if (_pauses++ == 0)
            {
                _pausedSince = _clock.GetTimestamp();
            }
        }

        return new PauseScope(this);
    }

    /// <summary>A token that is cancelled when <paramref name="outer"/> is, or when the total time runs out.</summary>
    public Watch CreateWatch(CancellationToken outer)
    {
        var watch = new Watch(this, outer);
        if (!IsUnbounded)
        {
            lock (_gate)
            {
                _watches.Add(watch);
            }

            watch.Reschedule();
        }

        return watch;
    }

    // Called with the gate held.
    private TimeSpan Elapsed()
    {
        var spent = _clock.GetElapsedTime(_started) - _paused;
        return _pauses > 0 ? spent - _clock.GetElapsedTime(_pausedSince) : spent;
    }

    private void Resume()
    {
        List<Watch> watches;
        lock (_gate)
        {
            if (--_pauses > 0)
            {
                return;
            }

            _paused += _clock.GetElapsedTime(_pausedSince);
            watches = [.. _watches];
        }

        foreach (var watch in watches)
        {
            watch.Reschedule();
        }
    }

    private void Forget(Watch watch)
    {
        lock (_gate)
        {
            _watches.Remove(watch);
        }
    }

    private sealed class PauseScope(AnswerDeadline deadline) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                deadline.Resume();
            }
        }
    }

    /// <summary>Cancels <see cref="Token"/> when the answer runs out of total time. Dispose it when the work it guards is done.</summary>
    internal sealed class Watch : IDisposable
    {
        private readonly AnswerDeadline _deadline;
        private readonly CancellationTokenSource _source;
        private readonly ITimer? _timer;

        internal Watch(AnswerDeadline deadline, CancellationToken outer)
        {
            _deadline = deadline;
            _source = CancellationTokenSource.CreateLinkedTokenSource(outer);
            _timer = deadline.IsUnbounded ? null : deadline._clock.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => _source.Token;

        /// <summary>The token was cancelled because the time ran out, not because the outer token was.</summary>
        public bool TimedOut { get; private set; }

        internal void Reschedule()
        {
            try
            {
                _timer?.Change(_deadline.Remaining, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // The work ended while the deadline was being moved.
            }
        }

        public void Dispose()
        {
            _deadline.Forget(this);
            _timer?.Dispose();
            _source.Dispose();
        }

        private void Fire()
        {
            // Paused: resuming schedules the time that is left again.
            if (_deadline.IsPaused)
            {
                return;
            }

            if (!_deadline.IsExpired)
            {
                Reschedule();
                return;
            }

            try
            {
                TimedOut = true;
                _source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The work ended first.
            }
        }
    }

    private bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _pauses > 0;
            }
        }
    }
}
