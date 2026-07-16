using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace ClaudeUsageMonitor.App.Startup;

/// <summary>
/// 뮤텍스로 단일 인스턴스 보장 + 네임드 파이프로 기존 인스턴스 활성화("대시보드 표시") 신호.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ClaudeUsageMonitor_SingleInstance";

    // 뮤텍스가 Local\(세션 로컬)이라 세션마다 첫 인스턴스가 생긴다. 파이프명도 세션 ID로 격리하지 않으면
    // 머신 전역이라, 두 번째 세션의 서버 생성이 ERROR_PIPE_BUSY로 실패해 busy-spin(코어 점유)했다.
    private static readonly string PipeName =
        "ClaudeUsageMonitor_Activate_" + Process.GetCurrentProcess().SessionId;

    private static readonly TimeSpan ServerRetryDelay = TimeSpan.FromMilliseconds(500);

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
                    // 파이프 오류(예: 다른 세션이 같은 이름 서버를 점유) — 짧게 물러난 뒤 재시도(busy-spin 방지)
                    try
                    {
                        await Task.Delay(ServerRetryDelay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
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
