namespace WolfMoss.ATAS.PriceMapping.Core;

public sealed record IbPerformanceSnapshot(long InstanceId, long SendAttempts, long CancelAttempts,
    int ActiveLines, int Consumers, int ReaderTasks);
