// Source-shared module: compiled privately into each consuming plugin.
namespace WolfMoss.ATAS.PriceMapping;

using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

using WolfMoss.ATAS.PriceMapping.Core;

internal sealed partial class ReflectionIbOptionGatewayClient : IIbOptionGatewayClient
{
    private static long _diagnosticNextInstance;
    private readonly long _diagnosticInstance = Interlocked.Increment(ref _diagnosticNextInstance);
    private long _diagnosticSends;
    private long _diagnosticCancels;
    private int _diagnosticLines;
    private int _diagnosticConsumers;
    public IbPerformanceSnapshot PerformanceSnapshot => new(_diagnosticInstance,
        Interlocked.Read(ref _diagnosticSends), Interlocked.Read(ref _diagnosticCancels),
        Volatile.Read(ref _diagnosticLines), Volatile.Read(ref _diagnosticConsumers),
        (_readerLoop is { IsCompleted: false } ? 1 : 0) + (_outbound.IsRunning ? 1 : 0));

    // Called only where the existing subscription mutation lock is already held.
    private void UpdateDiagnosticOccupancy()
    {
        Volatile.Write(ref _diagnosticLines, _subscriptionsByConId.Values.Count(static s => s.RequestId != 0));
        Volatile.Write(ref _diagnosticConsumers, _subscriptionsByConId.Values
            .SelectMany(static s => s.Consumers.Keys).Distinct().Count());
    }
    private static readonly TimeSpan LineLimitObservationLifetime = TimeSpan.FromMinutes(1);
    private readonly IbGatewayConnectionOptions _options;
    private readonly Assembly? _assembly = EmbeddedDependencyResolver.TryLoadIbApiAssembly();
    private readonly object _sync = new();
    private readonly IbOutboundDispatcher _outbound;
    private readonly Task _previousDisposal;
    internal readonly OptionFlowLineBudget FlowBudget = new();
    private Task? _initialization;
    private readonly ConcurrentDictionary<int, ContractRequest> _contractRequests = new();
    private readonly ConcurrentDictionary<int, SecurityDefinitionRequest> _securityRequests = new();
    private readonly ConcurrentDictionary<int, SharedSubscription> _subscriptionsByRequest = new();
    private readonly Dictionary<long, SharedSubscription> _subscriptionsByConId = new();
    private readonly Dictionary<(string Ticker, DateOnly Expiration), ChainDefinition> _chains = new();
    private readonly Dictionary<OptionContractKey, OptionContractDescriptor?> _resolvedContracts = new();
    private readonly SharedAsyncRequests<(string Ticker, DateOnly Expiration), IReadOnlyList<decimal>> _chainFlights = new();
    private readonly SharedAsyncRequests<OptionContractKey, OptionContractDescriptor?> _contractFlights = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private object? _client;
    private object? _reader;
    private object? _signal;
    private Task? _readerLoop;
    private readonly IbRequestIdSequence _requestIds = new();
    private IDisposable? _sessionReservation;
    private long _nextConsumerId;
    private volatile bool _disposed;
    private Task? _disposal;
    private int _connectionFaulted;
    private int _observedMarketDataLineCeiling = int.MaxValue;
    private DateTime _lineLimitObservedUtc = DateTime.MinValue;

    public ReflectionIbOptionGatewayClient(IbGatewayConnectionOptions options, Task? previousDisposal = null)
    {
        _options = options;
        _previousDisposal = previousDisposal ?? Task.CompletedTask;
        _outbound = new IbOutboundDispatcher(() => Interlocked.Increment(ref _diagnosticSends));
    }

    public bool IsAvailable => GetType("IBApi.EClientSocket") != null;

    public bool IsConnected
    {
        get
        {
            var client = Volatile.Read(ref _client);
            return client != null && ReadBoolean(client, "IsConnected");
        }
    }

    public int ActiveMarketDataLines => Volatile.Read(ref _diagnosticLines);

    internal bool ConnectionFaulted => Volatile.Read(ref _connectionFaulted) != 0;

    public string AvailabilityMessage
        => IsAvailable ? "IB API 已嵌入" : "官方 IB API 源码未嵌入";

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (ConnectionFaulted)
        {
            throw new IbOptionGatewayException(
                "CONNECTION_CLOSED", "IB Gateway 连接已断开，正在重建连接");
        }

        if (IsConnected && _connected.Task.IsCompletedSuccessfully)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            Task initialization;
            lock (_sync)
            {
                ThrowIfDisposed();
                initialization = _initialization ??= Task.Run(async () =>
                {
                    await _previousDisposal.ConfigureAwait(false);
                    _lifetime.Token.ThrowIfCancellationRequested();
                    InitializeSocket();
                });
            }
            await initialization.WaitAsync(timeout.Token).ConfigureAwait(false);
            await _connected.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

            if (!IsConnected)
            {
                throw new IbOptionGatewayException(
                    "CONNECTION_CLOSED",
                    "IB Gateway Socket 已关闭，正在重建连接");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _connectionFaulted, 1);
            throw new IbOptionGatewayException(
                "CONNECT_TIMEOUT",
                $"IB Gateway 连接超时 {_options.Host}:{_options.Port}");
        }
    }

    public Task<IReadOnlyList<decimal>> GetAvailableStrikesAsync(
        OptionUnderlyingProfile profile, DateOnly expiration, CancellationToken cancellationToken)
        => _chainFlights.GetAsync((profile.Ticker, expiration),
            () => GetAvailableStrikesCoreAsync(profile, expiration, _lifetime.Token), cancellationToken);

    private async Task<IReadOnlyList<decimal>> GetAvailableStrikesCoreAsync(
        OptionUnderlyingProfile profile,
        DateOnly expiration,
        CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var key = (profile.Ticker, expiration);

        lock (_sync)
        {
            if (_chains.TryGetValue(key, out var cached))
                return cached.Strikes;
        }

        var underlying = CreateUnderlyingContract(profile);
        var details = await RequestContractDetailsAsync(
                underlying,
                cancellationToken)
            .ConfigureAwait(false);
        var selected = details.FirstOrDefault(item => GetInt64(
            GetProperty(item, "Contract")!, "ConId") > 0)
            ?? throw new IbOptionGatewayException(
                "NO_CONTRACTS",
                $"IB 未返回 {profile.Ticker} 标的合约");
        var underlyingContract = GetProperty(selected, "Contract")!;
        var underlyingConId = GetInt64(underlyingContract, "ConId");
        var definition = await RequestSecurityDefinitionAsync(
                profile,
                underlyingConId,
                expiration,
                cancellationToken)
            .ConfigureAwait(false);

        lock (_sync)
            _chains[key] = definition;

        return definition.Strikes;
    }

    public async Task<IReadOnlyList<OptionContractDescriptor>> ResolveContractsAsync(
        OptionUnderlyingProfile profile,
        DateOnly expiration,
        IReadOnlyList<decimal> strikes,
        CancellationToken cancellationToken)
    {
        await GetAvailableStrikesAsync(profile, expiration, cancellationToken)
            .ConfigureAwait(false);
        ChainDefinition chain;

        lock (_sync)
            chain = _chains[(profile.Ticker, expiration)];

        var requests = new List<Task<OptionContractDescriptor?>>(strikes.Count * 2);

        foreach (var strike in strikes)
        {
            requests.Add(ResolveOptionContractAsync(
                profile, chain, expiration, strike, OptionRight.Call, cancellationToken));
            requests.Add(ResolveOptionContractAsync(
                profile, chain, expiration, strike, OptionRight.Put, cancellationToken));
        }

        var resolved = await Task.WhenAll(requests).ConfigureAwait(false);
        return resolved
            .Where(static contract => contract.HasValue)
            .Select(static contract => contract!.Value)
            .ToArray();
    }

    public Task<IIbOptionSubscriptionLease> SubscribeAsync(
        IReadOnlyList<OptionContractDescriptor> contracts,
        IbOptionSubscriptionRequirements requirements,
        Action<IbOptionMarketDataUpdate> onUpdate,
        int marketDataLineBudget,
        CancellationToken cancellationToken)
        => SubscribeCoreAsync(
            contracts,
            requirements,
            onUpdate,
            marketDataLineBudget,
            requireAllContracts: true,
            cancellationToken);

    public Task<IIbOptionSubscriptionLease> SubscribeAvailableAsync(
        IReadOnlyList<OptionContractDescriptor> contracts,
        IbOptionSubscriptionRequirements requirements,
        Action<IbOptionMarketDataUpdate> onUpdate,
        int marketDataLineBudget,
        CancellationToken cancellationToken)
        => SubscribeCoreAsync(
            contracts,
            requirements,
            onUpdate,
            marketDataLineBudget,
            requireAllContracts: false,
            cancellationToken);

    private async Task<IIbOptionSubscriptionLease> SubscribeCoreAsync(
        IReadOnlyList<OptionContractDescriptor> contracts,
        IbOptionSubscriptionRequirements requirements,
        Action<IbOptionMarketDataUpdate> onUpdate,
        int marketDataLineBudget,
        bool requireAllContracts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var consumerId = Interlocked.Increment(ref _nextConsumerId);
        var attached = new List<long>(contracts.Count);
        var sends = new List<Task>();

        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                var effectiveLineBudget = GetEffectiveLineBudgetNoLock(
                    marketDataLineBudget,
                    DateTime.UtcNow);
                var allocation = OptionMarketDataLineAllocator.Allocate(
                    contracts,
                    _subscriptionsByConId.Keys.ToArray(),
                    effectiveLineBudget);
                var selectedContracts = allocation.Contracts;

                if (requireAllContracts && !allocation.IsComplete)
                {
                    throw new IbOptionGatewayException(
                        "LINE_LIMIT",
                        $"期权行情线预算不足：当前 {_subscriptionsByConId.Count}，新增 {allocation.RequiredNewLineCount}，可用预算 {effectiveLineBudget}/{marketDataLineBudget}");
                }

                if (selectedContracts.Count == 0)
                {
                    throw new IbOptionGatewayException(
                        "LINE_LIMIT",
                        $"期权行情线暂无可用容量：{_subscriptionsByConId.Count}/{effectiveLineBudget}（设置 {marketDataLineBudget}）");
                }

                foreach (var contract in selectedContracts)
                {
                    if (!_subscriptionsByConId.TryGetValue(contract.ConId, out var shared))
                    {
                        shared = new SharedSubscription(contract) { HistoryOwner = this };
                        _subscriptionsByConId.Add(contract.ConId, shared);
                    }

                    shared.Consumers[consumerId] = onUpdate;
                    shared.Demands[consumerId] = requirements;
                    shared.RefreshCallbacks();

                    var combined = shared.Requirements.Union(requirements);
                    attached.Add(contract.ConId);

                    if (combined != shared.Requirements || shared.RequestId == 0)
                        sends.Add(StartOrReplaceSubscription(shared, combined));
                }
                UpdateDiagnosticOccupancy();
            }

            await Task.WhenAll(sends).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new SubscriptionLease(this, consumerId, attached, onUpdate);
        }
        catch
        {
            ReleaseConsumer(consumerId, attached);
            throw;
        }
    }

    private async Task UpdateConsumerAsync(SubscriptionLease lease,
        IReadOnlyList<OptionContractDescriptor> contracts,
        IbOptionSubscriptionRequirements requirements, int budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sends = new List<Task>();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!lease.IsOwned) throw new ObjectDisposedException(nameof(SubscriptionLease));
            var wanted = contracts.Select(static c => c.ConId).ToHashSet();
            var afterRelease = _subscriptionsByConId.Values
                .Where(s => wanted.Contains(s.Contract.ConId)
                    || s.Consumers.Keys.Any(id => id != lease.ConsumerId))
                .Select(static s => s.Contract.ConId).ToArray();
            var allocation = OptionMarketDataLineAllocator.Allocate(contracts, afterRelease,
                GetEffectiveLineBudgetNoLock(budget, DateTime.UtcNow));
            if (!allocation.IsComplete)
                throw new IbOptionGatewayException("LINE_LIMIT", "换档后行情线超出共享预算");
            ReleaseConsumer(lease.ConsumerId, lease.ContractIds.Where(id => !wanted.Contains(id)).ToArray());
            lease.ReplaceIds(allocation.Contracts.Select(static c => c.ConId).ToArray());
            foreach (var contract in allocation.Contracts)
            {
                if (!_subscriptionsByConId.TryGetValue(contract.ConId, out var shared))
                    _subscriptionsByConId.Add(contract.ConId, shared = new SharedSubscription(contract) { HistoryOwner = this });
                shared.Consumers[lease.ConsumerId] = lease.Callback;
                shared.Demands[lease.ConsumerId] = requirements;
                shared.RefreshCallbacks();
                var combined = shared.Demands.Values.Aggregate(default(IbOptionSubscriptionRequirements),
                    static (sum, item) => sum.Union(item));
                if (combined != shared.Requirements || shared.RequestId == 0)
                    sends.Add(StartOrReplaceSubscription(shared, combined));
            }
            UpdateDiagnosticOccupancy();
        }
        await Task.WhenAll(sends).WaitAsync(token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposal != null) return new ValueTask(_disposal);
            _disposed = true;
            foreach (var subscription in _subscriptionsByConId.Values)
            {
                RecordHistoryEnd(subscription, "CONNECTION_CLOSED");
                Volatile.Write(ref subscription.Callbacks, Array.Empty<Action<IbOptionMarketDataUpdate>>());
            }
            _subscriptionsByConId.Clear();
            _subscriptionsByRequest.Clear();
            UpdateDiagnosticOccupancy();
            _disposal = Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        var closed = new IbOptionGatewayException("CONNECTION_CLOSED", "IB Gateway 已释放");
        _connected.TrySetException(closed);
        foreach (var request in _contractRequests.Values) request.Completion.TrySetException(closed);
        foreach (var request in _securityRequests.Values) request.Completion.TrySetException(closed);
        TryInvoke(_client, "eDisconnect");
        TryInvoke(_signal, "issueSignal");
        await _outbound.DisposeAsync().ConfigureAwait(false);
        if (_initialization != null)
        {
            try { await _initialization.ConfigureAwait(false); }
            catch { /* Failed initialization must not prevent retirement. */ }
        }
        TryInvoke(_client, "eDisconnect");
        TryInvoke(_signal, "issueSignal");

        if (_readerLoop != null)
        {
            try
            {
                await _readerLoop
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }
        }

        _lifetime.Dispose();
        Interlocked.Exchange(ref _sessionReservation, null)?.Dispose();
    }

    private void InitializeSocket()
    {
        if (_assembly == null) throw new IbOptionGatewayException("IBAPI_MISSING", AvailabilityMessage);
        try { _sessionReservation = IbSessionReservation.Reserve(_options.Host, _options.Port, _options.ClientId); }
        catch (InvalidOperationException)
        { throw new IbOptionGatewayException("CLIENT_ID_IN_USE", "IB Client ID 已被其他插件占用，请为本插件配置不同的 ID"); }
        (_client, _signal) = IbSocketRuntime.Create(_assembly, HandleCallback);
        IbSocketRuntime.Connect(_client, _options.Host, _options.Port, _options.ClientId);
        _reader = IbSocketRuntime.StartReader(_assembly, _client, _signal);
        _readerLoop = Task.Run(ReadMessages, _lifetime.Token);
    }

    private void ReadMessages()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                InvokeRequired(_signal!, "waitForSignal");

                if (_lifetime.IsCancellationRequested)
                    return;

                InvokeRequired(_reader!, "processMsgs");
            }
        }
        catch when (!_lifetime.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _connectionFaulted, 1);
            _connected.TrySetException(new IbOptionGatewayException(
                "CONNECTION_CLOSED", "IB Gateway reader 已停止"));
        }
    }

    private void HandleCallback(MethodInfo method, object?[] args)
    {
        try
        {
            switch (method.Name)
            {
                case "nextValidId":
                    _connected.TrySetResult();
                    break;
                case "contractDetails":
                    OnContractDetails(args);
                    break;
                case "contractDetailsEnd":
                    CompleteContractDetails(args);
                    break;
                case "securityDefinitionOptionParameter":
                    OnSecurityDefinition(args);
                    break;
                case "securityDefinitionOptionParameterEnd":
                    CompleteSecurityDefinition(args);
                    break;
                case "tickSize":
                    OnTickSize(args);
                    break;
                case "tickString":
                    OnTickString(args);
                    break;
                case "marketDataType":
                    OnMarketDataType(args);
                    break;
                case "error":
                    OnError(args);
                    break;
                case "connectionClosed":
                    RecordHistoryConnectionEnd();
                    Interlocked.Exchange(ref _connectionFaulted, 1);
                    _connected.TrySetException(new IbOptionGatewayException(
                        "CONNECTION_CLOSED", "IB Gateway 已断开"));
                    break;
            }
        }
        catch
        {
            // A malformed callback must not stop the IB reader thread.
        }
    }

    private Task<OptionContractDescriptor?> ResolveOptionContractAsync(
        OptionUnderlyingProfile profile, ChainDefinition chain, DateOnly expiration, decimal strike,
        OptionRight right, CancellationToken cancellationToken)
        => _contractFlights.GetAsync(new OptionContractKey(profile.Ticker.ToUpperInvariant(),
                expiration, strike, right, chain.TradingClass.ToUpperInvariant()),
            () => ResolveOptionContractCoreAsync(profile, chain, expiration, strike, right, _lifetime.Token),
            cancellationToken);

    private async Task<OptionContractDescriptor?> ResolveOptionContractCoreAsync(
        OptionUnderlyingProfile profile,
        ChainDefinition chain,
        DateOnly expiration,
        decimal strike,
        OptionRight right,
        CancellationToken cancellationToken)
    {
        var cacheKey = new OptionContractKey(
            profile.Ticker.ToUpperInvariant(),
            expiration,
            strike,
            right,
            chain.TradingClass.ToUpperInvariant());

        lock (_sync)
        {
            if (_resolvedContracts.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var contract = CreateOptionContract(profile, chain, expiration, strike, right);
        IReadOnlyList<object> details;

        try
        {
            details = await RequestContractDetailsAsync(contract, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IbOptionGatewayException exception)
            when (string.Equals(exception.Code, "NO_CONTRACTS",
                StringComparison.Ordinal))
        {
            // reqSecDefOptParams returns independent expiration and strike sets.
            // IB documents that not every combination is necessarily a valid
            // contract, so one missing Call/Put must not discard the whole ladder.
            lock (_sync)
                _resolvedContracts[cacheKey] = null;

            return null;
        }

        var selected = details
            .Select(item => (Details: item, Contract: GetProperty(item, "Contract")))
            .FirstOrDefault(item => item.Contract != null
                && GetInt64(item.Contract, "ConId") > 0
                && string.Equals(GetString(item.Contract, "TradingClass"),
                    chain.TradingClass, StringComparison.OrdinalIgnoreCase));

        if (selected.Contract == null)
        {
            lock (_sync)
                _resolvedContracts[cacheKey] = null;

            return null;
        }

        var multiplierText = GetString(selected.Contract, "Multiplier");
        var multiplier = decimal.TryParse(multiplierText,
            NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedMultiplier)
            && parsedMultiplier > 0m
                ? parsedMultiplier
                : chain.Multiplier;
        var descriptor = new OptionContractDescriptor(
            GetInt64(selected.Contract, "ConId"),
            profile.Ticker,
            expiration,
            strike,
            right,
            GetString(selected.Contract, "TradingClass"),
            GetString(selected.Contract, "Exchange", "SMART"),
            multiplier,
            GetString(selected.Details!, "TradingHours"),
            GetString(selected.Details!, "TimeZoneId", "America/New_York"));

        lock (_sync)
            _resolvedContracts[cacheKey] = descriptor;

        return descriptor;
    }

    private async Task<IReadOnlyList<object>> RequestContractDetailsAsync(
        object contract,
        CancellationToken cancellationToken)
    {
        var requestId = NextRequestId();
        var request = new ContractRequest();
        _contractRequests[requestId] = request;
        using var registration = cancellationToken.Register(
            () => request.Completion.TrySetCanceled(cancellationToken));

        try
        {
            await _outbound.Enqueue(() => InvokeRequired(_client!, "reqContractDetails", requestId, contract),
                cancellationToken).ConfigureAwait(false);
            return await request.Completion.Task
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _contractRequests.TryRemove(requestId, out _);
        }
    }

    private async Task<ChainDefinition> RequestSecurityDefinitionAsync(
        OptionUnderlyingProfile profile,
        long underlyingConId,
        DateOnly expiration,
        CancellationToken cancellationToken)
    {
        var requestId = NextRequestId();
        var request = new SecurityDefinitionRequest(profile, expiration);
        _securityRequests[requestId] = request;
        using var registration = cancellationToken.Register(
            () => request.Completion.TrySetCanceled(cancellationToken));

        try
        {
            await _outbound.Enqueue(() => InvokeRequired(
                _client!,
                "reqSecDefOptParams",
                requestId,
                profile.Ticker,
                string.Empty,
                profile.SecurityType,
                checked((int)underlyingConId)), cancellationToken).ConfigureAwait(false);
            return await request.Completion.Task
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _securityRequests.TryRemove(requestId, out _);
        }
    }

    private void OnContractDetails(object?[] args)
    {
        if (args.Length >= 2
            && TryInt32(args[0], out var requestId)
            && args[1] != null
            && _contractRequests.TryGetValue(requestId, out var request))
        {
            lock (request.Items)
                request.Items.Add(args[1]!);
        }
    }

    private void CompleteContractDetails(object?[] args)
    {
        if (args.Length >= 1
            && TryInt32(args[0], out var requestId)
            && _contractRequests.TryRemove(requestId, out var request))
        {
            lock (request.Items)
                request.Completion.TrySetResult(request.Items.ToArray());
        }
    }

    private void OnSecurityDefinition(object?[] args)
    {
        if (args.Length < 7
            || !TryInt32(args[0], out var requestId)
            || !_securityRequests.TryGetValue(requestId, out var request))
        {
            return;
        }

        var tradingClass = Convert.ToString(args[3], CultureInfo.InvariantCulture) ?? string.Empty;
        var multiplierText = Convert.ToString(args[4], CultureInfo.InvariantCulture) ?? string.Empty;

        if (!string.Equals(tradingClass, request.Profile.PreferredTradingClass,
                StringComparison.OrdinalIgnoreCase)
            || !ContainsString(args[5], request.Expiration.ToString("yyyyMMdd",
                CultureInfo.InvariantCulture)))
        {
            return;
        }

        var strikes = Enumerate(args[6])
            .Select(value => TryDecimal(value, out var strike) ? strike : 0m)
            .Where(static strike => strike > 0m)
            .Distinct()
            .OrderBy(static strike => strike)
            .ToArray();
        var multiplier = decimal.TryParse(multiplierText,
            NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0m
                ? parsed
                : 100m;
        request.Candidates.Add(new ChainDefinition(tradingClass, multiplier, strikes));
    }

    private void CompleteSecurityDefinition(object?[] args)
    {
        if (args.Length < 1
            || !TryInt32(args[0], out var requestId)
            || !_securityRequests.TryRemove(requestId, out var request))
        {
            return;
        }

        var candidate = request.Candidates
            .Where(static value => value.Strikes.Count > 0)
            .OrderByDescending(static value => value.Strikes.Count)
            .FirstOrDefault();

        if (candidate == null)
        {
            request.Completion.TrySetException(new IbOptionGatewayException(
                "NO_CONTRACTS",
                $"{request.Profile.Ticker} {request.Expiration:yyyy-MM-dd} 没有可用 0DTE 合约"));
        }
        else
        {
            request.Completion.TrySetResult(candidate);
        }
    }

    private Task StartOrReplaceSubscription(
        SharedSubscription subscription,
        IbOptionSubscriptionRequirements requirements)
    {
        if (subscription.RequestId != 0)
        {
            RecordHistoryEnd(subscription, "SUBSCRIPTION_REPLACED");
            _subscriptionsByRequest.TryRemove(subscription.RequestId, out _);
            QueueCancel(subscription.RequestId);
        }

        var requestId = NextRequestId();
        subscription.RequestId = requestId;
        subscription.Requirements = requirements;
        _subscriptionsByRequest[requestId] = subscription;
        var contract = CreateContractFromDescriptor(subscription.Contract);
        var options = CreateEmptyTagValueList();
        var send = _outbound.Enqueue(() => InvokeRequired(
            _client!,
            "reqMktData",
            requestId,
            contract,
            requirements.GenericTickList,
            false,
            false,
            options), _lifetime.Token,
            () => _subscriptionsByRequest.ContainsKey(requestId));
        ObserveSend(send);
        return send;
    }

    private void OnTickSize(object?[] args)
    {
        if (args.Length < 3
            || !TryInt32(args[0], out var requestId)
            || !TryInt32(args[1], out var field)
            || field is not (27 or 28)
            || !_subscriptionsByRequest.TryGetValue(requestId, out var subscription)
            || !TryDecimal(args[2], out var size)
            || size < 0m)
        {
            return;
        }

        var relevant = subscription.Contract.Right == OptionRight.Call
            ? field == 27
            : field == 28;

        if (!relevant)
            return;

        PublishReceived(subscription, new IbOptionMarketDataUpdate(
            subscription.Contract,
            DateTime.UtcNow,
            decimal.ToInt64(decimal.Truncate(size)),
            null,
            null,
            subscription.IsDelayed), requestId);
    }

    private void OnTickString(object?[] args)
    {
        if (args.Length < 3
            || !TryInt32(args[0], out var requestId)
            || !TryInt32(args[1], out var field)
            || field is not (48 or 77)
            || !_subscriptionsByRequest.TryGetValue(requestId, out var subscription)
            || args[2] is not string text)
        {
            return;
        }

        if (!TryParseRealtimeVolume(subscription.Contract, text, out var sample))
        {
            RecordHistoryEnd(subscription, "INVALID_SAMPLE");
            return;
        }

        PublishReceived(subscription, new IbOptionMarketDataUpdate(
            subscription.Contract,
            DateTime.UtcNow,
            null,
            field == 77 ? sample : null,
            field == 48 ? sample : null,
            subscription.IsDelayed), requestId);
    }

    private void OnMarketDataType(object?[] args)
    {
        if (args.Length >= 2
            && TryInt32(args[0], out var requestId)
            && TryInt32(args[1], out var marketDataType)
            && _subscriptionsByRequest.TryGetValue(requestId, out var subscription))
        {
            subscription.IsDelayed = marketDataType is 3 or 4;
        }
    }

    private void OnError(object?[] args)
    {
        if (!IbErrorCallbackParser.TryParse(args, out var callback))
            return;

        if (callback.ErrorCode == 326)
        {
            Interlocked.Exchange(ref _connectionFaulted, 1);
            _connected.TrySetException(new IbOptionGatewayException("CLIENT_ID_IN_USE",
                "IB 326: Client ID 已占用，请配置不同 ID；不会自动接管其他客户端"));
            return;
        }

        if (callback.ErrorCode == 1100)
        {
            RecordHistoryConnectionEnd();
            Interlocked.Exchange(ref _connectionFaulted, 1);
        }

        if (callback.ErrorCode is not (100 or 101 or 200 or 321 or 354))
            return;

        var category = callback.ErrorCode switch
        {
            354 => "NO_PERMISSION",
            101 => "LINE_LIMIT",
            100 => "PACING",
            _ => "NO_CONTRACTS"
        };
        var exception = new IbOptionGatewayException(
            category,
            $"IB {callback.ErrorCode}: {callback.Message}");

        if (_contractRequests.TryRemove(callback.RequestId, out var contract))
            contract.Completion.TrySetException(exception);

        if (_securityRequests.TryRemove(callback.RequestId, out var security))
            security.Completion.TrySetException(exception);

        if (_subscriptionsByRequest.TryRemove(
                callback.RequestId, out var subscription))
        {
            lock (_sync)
            {
                if (subscription.RequestId != callback.RequestId) return;
                subscription.RequestId = 0;
                UpdateDiagnosticOccupancy();

                if (callback.ErrorCode == 101)
                    ObserveLineLimitNoLock(DateTime.UtcNow);
            }

            PublishSubscriptionError(subscription, category, exception.Message);
        }
        else if (callback.RequestId < 0 && callback.ErrorCode is 100 or 101)
        {
            if (callback.ErrorCode == 101)
            {
                lock (_sync)
                    ObserveLineLimitNoLock(DateTime.UtcNow);
            }

            foreach (var active in _subscriptionsByRequest.Values.Distinct())
                PublishSubscriptionError(active, category, exception.Message);
        }
    }

    private int GetEffectiveLineBudgetNoLock(int configuredBudget, DateTime utcNow)
    {
        configuredBudget = FlowBudget.EffectiveBudget(configuredBudget);
        if (_observedMarketDataLineCeiling == int.MaxValue)
            return configuredBudget;

        if (utcNow - _lineLimitObservedUtc >= LineLimitObservationLifetime)
        {
            _observedMarketDataLineCeiling = int.MaxValue;
            _lineLimitObservedUtc = DateTime.MinValue;
            return configuredBudget;
        }

        return Math.Min(configuredBudget, _observedMarketDataLineCeiling);
    }

    private void ObserveLineLimitNoLock(DateTime utcNow)
    {
        var activeLines = _subscriptionsByConId.Values.Count(
            static value => value.RequestId != 0);
        _observedMarketDataLineCeiling = Math.Min(
            _observedMarketDataLineCeiling,
            activeLines);
        _lineLimitObservedUtc = utcNow;
    }

    private static void PublishSubscriptionError(
        SharedSubscription subscription,
        string category,
        string message)
        => Publish(subscription, new IbOptionMarketDataUpdate(
            subscription.Contract,
            DateTime.UtcNow,
            null,
            null,
            null,
            subscription.IsDelayed,
            category,
            message));

    private void ReleaseConsumer(long consumerId, IReadOnlyList<long> conIds)
    {
        lock (_sync)
        {
            foreach (var conId in conIds)
            {
                if (!_subscriptionsByConId.TryGetValue(conId, out var shared))
                    continue;

                shared.Consumers.Remove(consumerId);
                shared.Demands.Remove(consumerId);
                shared.RefreshCallbacks();

                if (shared.Consumers.Count != 0)
                {
                    var demand = shared.Demands.Values.Aggregate(
                        default(IbOptionSubscriptionRequirements), static (sum, item) => sum.Union(item));
                    if (demand != shared.Requirements && shared.RequestId != 0)
                        _ = StartOrReplaceSubscription(shared, demand);
                    continue;
                }

                RecordHistoryEnd(shared, "SUBSCRIPTION_ENDED");
                _subscriptionsByConId.Remove(conId);
                if (shared.RequestId != 0)
                {
                    _subscriptionsByRequest.TryRemove(shared.RequestId, out _);
                    QueueCancel(shared.RequestId);
                }
            }
            UpdateDiagnosticOccupancy();
        }
    }

    private void QueueCancel(int requestId)
    {
        ObserveSend(_outbound.Enqueue(() =>
        {
            Interlocked.Increment(ref _diagnosticCancels);
            InvokeRequired(_client!, "cancelMktData", requestId);
        }, _lifetime.Token));
    }

    private async void ObserveSend(Task send)
    {
        try { await send.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch
        {
            // Queue saturation or a failed cancel must not leak server-side lines.
            // Retire this connection; consumers reconnect through the normal backoff.
            Interlocked.Exchange(ref _connectionFaulted, 1);
            _ = DisposeAsync();
        }
    }

    private static void Publish(
        SharedSubscription subscription,
        IbOptionMarketDataUpdate update)
        => PublishReceived(subscription, update, subscription.RequestId);

    private static void PublishReceived(SharedSubscription subscription, IbOptionMarketDataUpdate update, int receivedRequestId)
    {
        // One record per shared reception, before chart fanout. Disabled path creates nothing.
        if (WolfMoss.MarketData.MarketEventHub.Current is { } recorder)
        {
            try { subscription.HistoryOwner?.RecordMarketInput(subscription, update, recorder, receivedRequestId); }
            catch { recorder.CaptureFailed(); /* Optional history must never stop realtime callbacks. */ }
        }
        var consumers = Volatile.Read(ref subscription.Callbacks);

        foreach (var consumer in consumers)
        {
            try
            {
                consumer(update);
            }
            catch
            {
            }
        }
    }

    private object CreateUnderlyingContract(OptionUnderlyingProfile profile)
    {
        var contract = Activator.CreateInstance(GetType("IBApi.Contract")!)!;
        SetProperty(contract, "Symbol", profile.Ticker);
        SetProperty(contract, "SecType", profile.SecurityType);
        SetProperty(contract, "Exchange", profile.Exchange);
        SetProperty(contract, "Currency", profile.Currency);
        return contract;
    }

    private object CreateOptionContract(
        OptionUnderlyingProfile profile,
        ChainDefinition chain,
        DateOnly expiration,
        decimal strike,
        OptionRight right)
    {
        var contract = Activator.CreateInstance(GetType("IBApi.Contract")!)!;
        SetProperty(contract, "Symbol", profile.Ticker);
        SetProperty(contract, "SecType", "OPT");
        SetProperty(contract, "Exchange", "SMART");
        SetProperty(contract, "Currency", profile.Currency);
        SetProperty(contract, "LastTradeDateOrContractMonth",
            expiration.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        SetProperty(contract, "Strike", decimal.ToDouble(strike));
        SetProperty(contract, "Right", right == OptionRight.Call ? "C" : "P");
        SetProperty(contract, "Multiplier",
            chain.Multiplier.ToString("0.####", CultureInfo.InvariantCulture));
        SetProperty(contract, "TradingClass", chain.TradingClass);
        return contract;
    }

    private object CreateContractFromDescriptor(OptionContractDescriptor descriptor)
    {
        var contract = Activator.CreateInstance(GetType("IBApi.Contract")!)!;
        SetProperty(contract, "ConId", checked((int)descriptor.ConId));
        SetProperty(contract, "Symbol", descriptor.Ticker);
        SetProperty(contract, "SecType", "OPT");
        SetProperty(contract, "Exchange", descriptor.Exchange);
        SetProperty(contract, "Currency", "USD");
        SetProperty(contract, "LastTradeDateOrContractMonth",
            descriptor.Expiration.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        SetProperty(contract, "Strike", decimal.ToDouble(descriptor.StrikeUsd));
        SetProperty(contract, "Right", descriptor.Right == OptionRight.Call ? "C" : "P");
        SetProperty(contract, "Multiplier",
            descriptor.Multiplier.ToString("0.####", CultureInfo.InvariantCulture));
        SetProperty(contract, "TradingClass", descriptor.TradingClass);
        return contract;
    }

    private object CreateEmptyTagValueList()
    {
        var tagValueType = GetType("IBApi.TagValue")!;
        return Activator.CreateInstance(typeof(List<>).MakeGenericType(tagValueType))!;
    }

    private Type? GetType(string fullName) => _assembly?.GetType(fullName);

    private int NextRequestId() => _requestIds.Next();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static bool TryParseRealtimeVolume(
        OptionContractDescriptor contract,
        string text,
        out OptionCumulativeSample sample)
    {
        sample = default;
        var parts = text.Split(';');

        if (parts.Length < 5
            || !long.TryParse(parts[3], NumberStyles.Number,
                CultureInfo.InvariantCulture, out var totalVolume)
            || !decimal.TryParse(parts[4], NumberStyles.Number,
                CultureInfo.InvariantCulture, out var vwap)
            || totalVolume < 0
            || vwap < 0m)
        {
            return false;
        }

        DateTime sampleUtc;
        decimal? lastTradePrice = null;
        long? lastTradeSize = null;

        if (decimal.TryParse(parts[0], NumberStyles.Number,
                CultureInfo.InvariantCulture, out var parsedPrice)
            && parsedPrice >= 0m)
        {
            lastTradePrice = parsedPrice;
        }

        if (decimal.TryParse(parts[1], NumberStyles.Number,
                CultureInfo.InvariantCulture, out var parsedSize)
            && parsedSize > 0m
            && parsedSize <= long.MaxValue)
        {
            lastTradeSize = decimal.ToInt64(decimal.Truncate(parsedSize));
        }

        if (long.TryParse(parts[2], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var unixMilliseconds))
        {
            try
            {
                sampleUtc = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
        else return false; // Never invent a source timestamp from callback receive time.

        sample = new OptionCumulativeSample(
            contract.ConId,
            sampleUtc,
            totalVolume,
            vwap,
            contract.Multiplier,
            lastTradePrice,
            lastTradeSize);
        return true;
    }

    private static object? GetProperty(object target, string property)
        => target.GetType().GetProperty(property)?.GetValue(target);

    private static string GetString(object target, string property, string fallback = "")
        => Convert.ToString(GetProperty(target, property), CultureInfo.InvariantCulture)
           ?? fallback;

    private static long GetInt64(object target, string property)
        => Convert.ToInt64(GetProperty(target, property), CultureInfo.InvariantCulture);

    private static bool ReadBoolean(object target, string property)
    {
        var propertyInfo = target.GetType().GetProperty(property);

        if (propertyInfo != null)
            return Convert.ToBoolean(propertyInfo.GetValue(target), CultureInfo.InvariantCulture);

        var method = target.GetType().GetMethod(property, Type.EmptyTypes);
        return method != null
               && Convert.ToBoolean(method.Invoke(target, null), CultureInfo.InvariantCulture);
    }

    private static void SetProperty(object target, string property, object value)
    {
        var propertyInfo = target.GetType().GetProperty(property)
            ?? throw new MissingMemberException(target.GetType().FullName, property);
        var converted = propertyInfo.PropertyType.IsInstanceOfType(value)
            ? value
            : Convert.ChangeType(value, propertyInfo.PropertyType, CultureInfo.InvariantCulture);
        propertyInfo.SetValue(target, converted);
    }

    private static void InvokeRequired(object target, string method, params object?[] args)
        => IbSocketRuntime.Invoke(target, method, args);

    private static void TryInvoke(object? target, string method, params object?[] args)
    {
        if (target == null)
            return;

        try
        {
            InvokeRequired(target, method, args);
        }
        catch
        {
        }
    }

    private static bool TryInt32(object? value, out int result)
    {
        try
        {
            result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            result = 0;
            return false;
        }
    }

    private static bool TryDecimal(object? value, out decimal result)
        => decimal.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out result);

    private static bool ContainsString(object? collection, string expected)
        => Enumerate(collection).Any(value => string.Equals(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            expected,
            StringComparison.Ordinal));

    private static IEnumerable<object?> Enumerate(object? value)
    {
        if (value is not IEnumerable enumerable)
            yield break;

        foreach (var item in enumerable)
            yield return item;
    }

    private sealed class ContractRequest
    {
        public List<object> Items { get; } = new();

        public TaskCompletionSource<IReadOnlyList<object>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SecurityDefinitionRequest(
        OptionUnderlyingProfile profile,
        DateOnly expiration)
    {
        public OptionUnderlyingProfile Profile { get; } = profile;

        public DateOnly Expiration { get; } = expiration;

        public List<ChainDefinition> Candidates { get; } = new();

        public TaskCompletionSource<ChainDefinition> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ChainDefinition(
        string TradingClass,
        decimal Multiplier,
        IReadOnlyList<decimal> Strikes);

    private readonly record struct OptionContractKey(
        string Ticker,
        DateOnly Expiration,
        decimal Strike,
        OptionRight Right,
        string TradingClass);

    private sealed class SharedSubscription(OptionContractDescriptor contract)
    {
        public ReflectionIbOptionGatewayClient? HistoryOwner;
        public IbHistoryObservation? History;
        public OptionContractDescriptor Contract { get; } = contract;

        public Dictionary<long, Action<IbOptionMarketDataUpdate>> Consumers { get; } = new();
        public Dictionary<long, IbOptionSubscriptionRequirements> Demands { get; } = new();
        public Action<IbOptionMarketDataUpdate>[] Callbacks = Array.Empty<Action<IbOptionMarketDataUpdate>>();
        public void RefreshCallbacks() => Volatile.Write(ref Callbacks, Consumers.Values.ToArray());

        public IbOptionSubscriptionRequirements Requirements { get; set; }

        public int RequestId { get; set; }

        public bool IsDelayed { get; set; }
    }

    private sealed class SubscriptionLease(
        ReflectionIbOptionGatewayClient owner,
        long consumerId,
        IReadOnlyList<long> conIds,
        Action<IbOptionMarketDataUpdate> callback) : IIbOptionSubscriptionLease
    {
        private ReflectionIbOptionGatewayClient? _owner = owner;
        private IReadOnlyList<long> _conIds = conIds.ToArray();
        public bool IsOwned => Volatile.Read(ref _owner) != null;
        public long ConsumerId => consumerId;
        public Action<IbOptionMarketDataUpdate> Callback => callback;
        public void ReplaceIds(IReadOnlyList<long> ids) => Volatile.Write(ref _conIds, ids);

        public int ContractCount => ContractIds.Count;

        public IReadOnlyList<long> ContractIds => Volatile.Read(ref _conIds);

        public Task UpdateAsync(IReadOnlyList<OptionContractDescriptor> contracts,
            IbOptionSubscriptionRequirements requirements, int marketDataLineBudget, CancellationToken token)
            => (Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(SubscriptionLease)))
                .UpdateConsumerAsync(this, contracts, requirements, marketDataLineBudget, token);

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value == null) return;
            lock (value._sync) value.ReleaseConsumer(consumerId, ContractIds);
        }
    }
}
