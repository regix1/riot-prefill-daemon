#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RiotPrefill.Test;

internal sealed class CacheStatusClock : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ClockTimer> _timers = new();
    private DateTimeOffset _utcNow;

    internal CacheStatusClock(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync) return _utcNow;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ClockTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
            timer.ChangeUnderLock(dueTime, period);
        }
        return timer;
    }

    internal void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));

        List<(TimerCallback Callback, object? State)> callbacks = new();
        lock (_sync)
        {
            _utcNow += elapsed;
            foreach (var timer in _timers.ToArray())
            {
                if (timer.TryFireUnderLock(_utcNow, out var callback, out var state))
                    callbacks.Add((callback, state));
            }
        }

        foreach (var callback in callbacks)
            callback.Callback(callback.State);
    }

    private sealed class ClockTimer : ITimer
    {
        private readonly CacheStatusClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAt;
        private TimeSpan _period;
        private bool _disposed;

        internal ClockTimer(CacheStatusClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_clock._sync)
            {
                if (_disposed) return false;
                ChangeUnderLock(dueTime, period);
                return true;
            }
        }

        internal void ChangeUnderLock(TimeSpan dueTime, TimeSpan period)
        {
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _clock._utcNow + dueTime;
            _period = period;
        }

        internal bool TryFireUnderLock(DateTimeOffset utcNow, out TimerCallback callback, out object? state)
        {
            callback = _callback;
            state = _state;
            if (_disposed || !_dueAt.HasValue || _dueAt.Value > utcNow) return false;

            _dueAt = _period == Timeout.InfiniteTimeSpan ? null : utcNow + _period;
            return true;
        }

        public void Dispose()
        {
            lock (_clock._sync)
            {
                if (_disposed) return;
                _disposed = true;
                _clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
