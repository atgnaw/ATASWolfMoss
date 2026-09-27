using System.Text.Json;
using IbOptionFlowProbe;
using WolfMoss.ATAS.PriceMapping;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("IbOptionFlowProbe 0.5 — 独立采集、历史报价对齐与方向稳健度实验\ncapture --live --config <配置.json>\ninspect <运行目录>\ncheck-runtime（仅检查嵌入 API，不连接）\nanalyze <运行目录>\ndemo <输出目录>（生成模拟数据报告）\nbenchmark <输出目录>（10 分钟真实磁盘模拟压测）");
        return 0;
    }
    if (args.Length == 1 && args[0] == "check-runtime")
    {
        await using var gateway = new ReflectionIbOptionGatewayClient(new("127.0.0.1", 4001, 2290));
        Console.WriteLine(gateway.IsAvailable ? "PASS: 嵌入 IB API 已加载；没有连接 Gateway。" : "FAIL: 嵌入 IB API 无法加载。");
        return gateway.IsAvailable ? 0 : 1;
    }
    if (args[0] == "analyze" && args.Length == 2)
    { await AnalysisRunner.RunAsync(args[1]); return 0; }
    if (args[0] == "demo" && args.Length == 2)
    { await AnalysisRunner.RunAsync(await ProbeDemo.CreateAsync(args[1])); return 0; }
    if (args[0] == "benchmark" && args.Length == 2)
    { await ProbeBenchmark.RunAsync(args[1]); return 0; }
    if (args[0] == "inspect" && args.Length == 2)
    {
        var result = await CaptureInspector.InspectAsync(args[1]);
        Console.WriteLine(JsonSerializer.Serialize(result, ProbeJson.Options));
        return result.AbnormalEnd ? 2 : 0;
    }
    if (args.Length != 4 || args[0] != "capture" || args[1] != "--live" || args[2] != "--config")
        throw new ArgumentException("参数无效；真实连接必须显式使用 capture --live --config <文件>。");
    var config = JsonSerializer.Deserialize<ProbeConfig>(await File.ReadAllTextAsync(args[3]), ProbeJson.Options)
        ?? throw new ArgumentException("空配置。");
    config.Validate();
    Console.WriteLine($"目标 {config.Host}:{config.Port} Client ID {config.ClientId}；ATM 上下各 {config.StrikesEachSide} 档，需要 {config.RequiredMarketDataLines} 条期权行情线；{config.DurationMinutes} 分钟。独立 ID 不增加账户行情额度。");
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var capture = await CaptureRunner.RunAsync(config, cancellation.Token);
    Console.WriteLine(capture.Directory);
    try { await AnalysisRunner.RunAsync(capture.Directory); }
    catch (Exception ex)
    {
        Console.Error.WriteLine(capture.Summary.Complete
            ? "采集已完整完成，原始文件已保存；但离线报告生成失败。无需重新采集。"
            : "采集未完整完成，已有原始文件已保存；离线报告也生成失败。");
        if (ex is ArgumentException) Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine($"修复后可重试：IbOptionFlowProbe.exe analyze \"{capture.Directory}\"");
        return capture.Summary.Complete ? 3 : 4;
    }
    return capture.Summary.Complete ? 0 : 2;
}
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }
catch (JsonException) { Console.Error.WriteLine("配置格式错误或包含未知字段。"); return 1; }
catch (IOException) { Console.Error.WriteLine("文件读写失败；检查路径、空间和权限。未输出底层异常或敏感内容。"); return 1; }
catch (UnauthorizedAccessException) { Console.Error.WriteLine("文件访问被拒绝。"); return 1; }
catch { Console.Error.WriteLine("验证程序失败；未输出底层异常对象。"); return 1; }
