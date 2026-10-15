using Microsoft.Extensions.Configuration;

namespace ShopEasy.Shared.Resilience;

// Settings under "Resilience:RetryBudget".
public class RetryBudgetOptions
{
    public const string SectionName = "Resilience:RetryBudget";

    // Retries may add at most this share of extra requests on top of the original ones within the
    // window: 0.2 means that for every ten calls at most two retries are allowed. Without a limit,
    // a dependency that starts failing gets hit by every caller's retries as well, several times
    // the normal load, exactly when it is least able to cope.
    public double Ratio { get; set; } = 0.2;

    // Retries that are always allowed within the window, so that a quiet service (a handful of calls
    // a minute) can still retry a failed call. The effective allowance is the larger of this and
    // Ratio x the requests seen in the window.
    public int MinRetriesPerWindow { get; set; } = 10;

    public int WindowSeconds { get; set; } = 10;

    public static RetryBudgetOptions From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<RetryBudgetOptions>() ?? new RetryBudgetOptions();

    public void Validate()
    {
        if (Ratio is < 0 or > 1)
            throw new InvalidOperationException($"{SectionName}:{nameof(Ratio)} must be between 0 and 1, not {Ratio}.");
        if (MinRetriesPerWindow < 0)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MinRetriesPerWindow)} must not be negative, not {MinRetriesPerWindow}.");
        if (WindowSeconds is < 1 or > 600)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(WindowSeconds)} must be between 1 and 600, not {WindowSeconds}.");
    }
}

// Limits how much extra load the retries of one caller may put on one dependency. Every original
// request is recorded; a retry is only allowed while the retries of the last few seconds stay
// below the budget. Shared by all the calls to the same dependency in a process, so it sees the
// whole picture and not just one request's.
//
//   budget.RecordRequest();                // once per original call
//   if (budget.TryAcquireRetry()) { ... }  // before each retry
//
// Thread-safe. Counts are kept in one-second buckets, so the window slides without a timer.
public sealed class RetryBudget
{
    private readonly object _gate = new();
    private readonly double _ratio;
    private readonly int _minRetries;
    private readonly long[] _requests;
    private readonly long[] _retries;
    private readonly TimeProvider _clock;
    private long _lastSecond;

    public RetryBudget(RetryBudgetOptions options, TimeProvider? clock = null)
    {
        options.Validate();
        _ratio = options.Ratio;
        _minRetries = options.MinRetriesPerWindow;
        _requests = new long[options.WindowSeconds];
        _retries = new long[options.WindowSeconds];
        _clock = clock ?? TimeProvider.System;
        _lastSecond = _clock.GetUtcNow().ToUnixTimeSeconds();
    }

    public void RecordRequest()
    {
        lock (_gate)
        {
            Advance();
            _requests[Bucket(_lastSecond)]++;
        }
    }

    // True, and the retry is counted, when the budget allows another one
    public bool TryAcquireRetry()
    {
        lock (_gate)
        {
            Advance();
            var allowance = Math.Max(_minRetries, _ratio * _requests.Sum());
            if (_retries.Sum() + 1 > allowance) return false;

            _retries[Bucket(_lastSecond)]++;
            return true;
        }
    }

    private int Bucket(long second) => (int)(second % _requests.Length);

    // Moves to the current second, forgetting the seconds that have fallen out of the window
    private void Advance()
    {
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (now <= _lastSecond) return;

        var elapsed = Math.Min(now - _lastSecond, _requests.Length);
        for (var i = 1; i <= elapsed; i++)
        {
            var bucket = Bucket(_lastSecond + i);
            _requests[bucket] = 0;
            _retries[bucket] = 0;
        }

        _lastSecond = now;
    }
}
