using System.IO;
using System.IO.Pipes;

namespace ClaudeUsageMonitor.App.Startup;

/// <summary>
/// 뮤텍스로 단일 인스턴스 보장 + 네임드 파이프로 기존 인스턴스 활성화("대시보드 표시") 신호.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ClaudeUsageMonitor_SingleInstance";
    private const string PipeName = "ClaudeUsageMonitor_Activate";

    private Mutex? _mutex;
    private CancellationTokenSource? _serverCts;

    /// <returns>이 프로세스가 첫 인스턴스이면 true.</returns>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return createdNew;
    }

    /// <summary>첫 인스턴스에서 실행 — 후속 실행의 활성화 신호 수신.</summary>
    public void StartServer(Action onActivate)
    {
        _serverCts = new CancellationTokenSource();
        var token = _serverCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var message = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (message == "show")
                    {
                        onActivate();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // 파이프 오류 — 재시도
                }
            }
        }, token);
    }

    /// <summary>두 번째 인스턴스에서 실행 — 기존 인스턴스에 표시 신호 후 종료.</summary>
    public static void SignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("show");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            // 기존 인스턴스가 응답하지 않음 — 조용히 무시
        }
    }

    public void Dispose()
    {
        _serverCts?.Cancel();
        _serverCts?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
