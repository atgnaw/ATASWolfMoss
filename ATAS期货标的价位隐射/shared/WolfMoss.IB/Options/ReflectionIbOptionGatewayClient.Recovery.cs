namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

internal sealed partial class ReflectionIbOptionGatewayClient
{
    private DateTime _nextRecoveryScanUtc;

    // No unbounded permission retries. Recover only transient pacing/line failures,
    // at most three sends per affected contract until the subscription is recreated.
    internal async Task RepairSubscriptionsAsync(DateTime now, int budget, CancellationToken token)
    {
        List<Task>? sends = null;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (now < _nextRecoveryScanUtc) return;
            _nextRecoveryScanUtc = now.AddSeconds(5);
            var available = GetEffectiveLineBudgetNoLock(budget, now)
                - _subscriptionsByConId.Values.Count(s => s.RequestId != 0);
            foreach (var subscription in _subscriptionsByConId.Values)
            {
                // Replace the existing line, using the same paced cancel-before-request path.
                // Never infer realtime from ordinary price ticks, or retry permission failures.
                if (subscription.RequestId != 0 && subscription.Consumers.Count > 0
                    && subscription.Requirements.BuyStrength && subscription.Strength?.NeedsMarketTypeRefresh(now) == true
                    && subscription.StrengthRecoveryAttempts < 3 && now >= subscription.StrengthRetryAfterUtc)
                {
                    subscription.StrengthRecoveryAttempts++;
                    subscription.StrengthRetryAfterUtc = now.AddSeconds(30);
                    (sends ??= new()).Add(StartOrReplaceSubscription(subscription, subscription.Requirements));
                    continue;
                }
                if (subscription.RequestId != 0 || subscription.Consumers.Count == 0
                    || subscription.FailureCode is not (100 or 101) || subscription.RecoveryAttempts >= 3
                    || now < subscription.RetryAfterUtc || available <= 0) continue;
                subscription.RecoveryAttempts++;
                (sends ??= new()).Add(StartOrReplaceSubscription(subscription, subscription.Requirements));
                available--;
            }
            UpdateDiagnosticOccupancy();
        }
        if (sends != null)
            await IbTaskOwnership.Own(Task.WhenAll(sends)).WaitAsync(token).ConfigureAwait(false);
    }
}
