using System;
using System.Diagnostics;
using System.Threading;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// Everything that writes the SMC from this program goes through here, for two reasons.
    ///
    /// The first is other programs. MacHALDriver knows nothing about anybody else using the
    /// SMC, and a transaction of ours that lands in the middle of RPMac's leaves the SMC with
    /// its input buffer closed: it then rejects every command from every process until a
    /// power cycle. RPMac and its smccore helper serialise their own transactions on a
    /// documented named mutex, Global\AppleSmcAccess, so each write here is taken under the
    /// same one. If the mutex cannot be had in a moment the write is skipped -- one frame of
    /// a beacon missing is nothing, a wedged SMC is a reboot.
    ///
    /// The second is the SMC being dead already. A write to a wedged SMC does not fail
    /// politely: Apple's driver can spin in the kernel for a long time before giving up, and
    /// a loop of thirty such writes a second is a core pinned for as long as the loop runs
    /// (a flash that was meant to last two minutes ran for sixteen, because the ceiling is
    /// only looked at between writes). So a write that fails, or that takes far longer than
    /// a healthy one, counts against the SMC; a couple in a row open a breaker and nothing
    /// is written at all for a while. The wait doubles each time a probe after it fails,
    /// and a good write closes the breaker again.
    ///
    /// Callers already serialise on Backlight's lock, so this class only protects the state
    /// that other threads read -- whether the breaker is open.
    /// </summary>
    internal sealed class SmcGuard : IDisposable
    {
        /// <summary>The name RPMac, smccore and anything else that cooperates agree on.</summary>
        public const string MutexName = @"Global\AppleSmcAccess";

        // RPMac holds the mutex for one transaction at a time and polls often, so a wait this
        // long is almost always enough. Longer would stall a hotkey, which waits on the same
        // lock as the flasher; shorter would drop beacon frames needlessly.
        private const int MutexWaitMilliseconds = 200;

        // A healthy LKSB write returns in a few milliseconds. Anything this slow means the
        // driver is waiting on an SMC that is not answering.
        private const int SlowWriteMilliseconds = 250;

        // Consecutive bad writes that open the breaker. Two, not one: a single hiccup should
        // not silence the light, but a third spin through a dead SMC should never happen.
        private const int TripAfterFailures = 2;

        // How long nothing is written once the breaker opens, then doubled per failed probe.
        private const int FirstBackoffMilliseconds = 5000;
        private const int MaxBackoffMilliseconds = 300000;

        // The mutex is not there until somebody creates it, and creating it can be refused;
        // trying again on every one of thirty writes a second would be an exception each.
        private const int MutexRetryMilliseconds = 10000;

        private readonly object _sync = new object();
        private readonly string _mutexName;

        private Mutex _mutex;
        private long _mutexTriedAt = long.MinValue;
        private int _failures;
        private int _backoffMilliseconds = FirstBackoffMilliseconds;
        private long _openUntil;
        private bool _open;

        public SmcGuard()
            : this(MutexName)
        {
        }

        /// <summary>Only for a test that must not take the real mutex other programs use.</summary>
        internal SmcGuard(string mutexName)
        {
            _mutexName = mutexName;
        }

        /// <summary>
        /// True while the breaker is open: the SMC stopped answering and nothing is being
        /// written. Safe to ask from any thread.
        /// </summary>
        public bool IsBackingOff
        {
            get
            {
                lock (_sync)
                {
                    return _open && NowMilliseconds() < _openUntil;
                }
            }
        }

        /// <summary>
        /// Runs one SMC write under the global mutex and judges how it went.
        /// </summary>
        /// <returns>
        /// True if the write went through. False if it was skipped (the breaker is open, or
        /// somebody else is mid-transaction) or failed; <see cref="IsBackingOff"/> tells a
        /// caller whether to stop trying.
        /// </returns>
        public bool Write(Func<bool> write)
        {
            if (IsBackingOff)
            {
                return false;
            }

            Mutex mutex = GetMutex();
            bool held = false;

            try
            {
                if (mutex != null)
                {
                    try
                    {
                        held = mutex.WaitOne(MutexWaitMilliseconds);
                    }
                    catch (AbandonedMutexException)
                    {
                        // Its owner died mid-hold. The SMC keeps nothing between
                        // transactions, so there is nothing to repair; it is ours now.
                        held = true;
                    }
                    catch (Exception)
                    {
                        held = false;
                    }

                    if (!held)
                    {
                        // Another program is talking to the SMC. Not a fault in the SMC, and
                        // interleaving with it is exactly what must not happen.
                        return false;
                    }
                }

                long started = Stopwatch.GetTimestamp();
                bool ok;
                try
                {
                    ok = write();
                }
                catch (Exception)
                {
                    ok = false;
                }
                long elapsed = (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;

                if (ok && elapsed <= SlowWriteMilliseconds)
                {
                    Succeeded();
                    return true;
                }

                Failed();
                return false;
            }
            finally
            {
                if (held)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (Exception)
                    {
                        // Not ours to release after all; nothing to do about it.
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_mutex != null)
                {
                    _mutex.Close();
                    _mutex = null;
                }
            }
        }

        /// <summary>Milliseconds on a monotonic clock that does not wrap.</summary>
        internal static long NowMilliseconds()
        {
            return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        }

        private void Succeeded()
        {
            lock (_sync)
            {
                _failures = 0;
                _open = false;
                _backoffMilliseconds = FirstBackoffMilliseconds;
            }
        }

        private void Failed()
        {
            lock (_sync)
            {
                _failures++;
                if (_failures < TripAfterFailures)
                {
                    return;
                }

                _open = true;
                _openUntil = NowMilliseconds() + _backoffMilliseconds;
                _backoffMilliseconds = Math.Min(_backoffMilliseconds * 2, MaxBackoffMilliseconds);

                // One failed probe after the wait is enough to close the door again.
                _failures = TripAfterFailures - 1;
            }
        }

        /// <summary>
        /// The shared mutex, or null if it cannot be had -- in which case writes go ahead
        /// without it, exactly as they did before it existed, and the breaker and the lower
        /// write rate are what protect the SMC. Created if nobody has yet: whoever is first
        /// to need it makes it, which is how RPMac and smccore do it too.
        /// </summary>
        private Mutex GetMutex()
        {
            lock (_sync)
            {
                if (_mutex != null)
                {
                    return _mutex;
                }

                long now = NowMilliseconds();
                if (_mutexTriedAt != long.MinValue && now - _mutexTriedAt < MutexRetryMilliseconds)
                {
                    return null;
                }
                _mutexTriedAt = now;

                try
                {
                    bool created;
                    _mutex = new Mutex(false, _mutexName, out created);
                }
                catch (Exception)
                {
                    _mutex = null;
                }
                return _mutex;
            }
        }
    }
}
