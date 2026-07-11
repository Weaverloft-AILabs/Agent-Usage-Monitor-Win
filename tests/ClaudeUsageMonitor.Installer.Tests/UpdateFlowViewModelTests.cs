using Xunit;

namespace ClaudeUsageMonitor.Installer.Tests;

/// <summary>공용 진행 상태머신(UpdateFlowViewModel) — 단계 전환·취소 게이팅·PendingRestart·오류 카드.</summary>
public class UpdateFlowViewModelTests
{
    private static UpdateFlowTexts Texts => new()
    {
        WindowTitle = "t",
        StepLabels = ["다운로드", "보안 검사", "설치", "완료"],
        ProgressHeadlines = ["h0", "h1", "h2", "h3"],
        DoneHeadline = "done",
        DoneSecondary = "sec",
        DoneButton = "ok",
        ErrorHeadline = "err",
        SlowHintStepIndex = -1, // 테스트에서 15초 지연 힌트 비활성
        LastCancellableStepIndex = 1,
        CreepTargets = [0, 0, 0, 0], // 크리프 루프 비활성 (결정적 게이지 값)
    };

    private static UpdateFlowViewModel NewViewModel(Func<IUpdateFlow?>? factory) =>
        new(Texts, "v0.0.0", logPath: "log", repoUrl: "repo", releasesPageUrl: "releases")
        {
            FlowFactory = factory,
        };

    /// <summary>Progress&lt;T&gt;의 컨텍스트 마샬링을 인라인 실행으로 만들어 단정 시점을 결정적으로.</summary>
    private static async Task RunOnImmediateContext(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateContext());
        try
        {
            await body();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class ImmediateContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class ScriptedFlow(Func<IProgress<UpdateFlowProgress>, CancellationToken, Task<UpdateFlowResult>> body)
        : IUpdateFlow
    {
        public Task<UpdateFlowResult> RunAsync(IProgress<UpdateFlowProgress> progress, CancellationToken ct)
            => body(progress, ct);
    }

    [Fact]
    public async Task Steps_Transition_Timeline_And_Headlines() => await RunOnImmediateContext(async () =>
    {
        var vm = NewViewModel(null);
        var flow = new ScriptedFlow((progress, _) =>
        {
            progress.Report(new UpdateFlowProgress(0, 30));
            Assert.Equal(UpdateFlowState.Progress, vm.State);
            Assert.Equal("Active", vm.DownloadStepState);
            Assert.Equal("h0", vm.HeadlineText);
            Assert.Equal("30%", vm.ProgressValueText);

            progress.Report(new UpdateFlowProgress(1));
            Assert.Equal("Done", vm.DownloadStepState);
            Assert.Equal("Active", vm.ScanStepState);
            Assert.Equal("2/4 단계", vm.ProgressValueText);

            progress.Report(new UpdateFlowProgress(2));
            Assert.Equal("Active", vm.InstallStepState);
            return Task.FromResult(new UpdateFlowResult(true));
        });
        vm.FlowFactory = () => flow;

        await vm.StartFlowAsync();

        Assert.Equal(UpdateFlowState.Done, vm.State);
        Assert.Equal(100, vm.GaugePct);
    });

    [Fact]
    public async Task Cancel_Is_Revoked_After_Last_Cancellable_Step() => await RunOnImmediateContext(async () =>
    {
        var vm = NewViewModel(null);
        var flow = new ScriptedFlow((progress, _) =>
        {
            progress.Report(new UpdateFlowProgress(0, 0));
            Assert.True(vm.CanCancel);
            progress.Report(new UpdateFlowProgress(1));
            Assert.True(vm.CanCancel);
            progress.Report(new UpdateFlowProgress(2)); // 디스크 쓰기 단계 — 취소 회수
            Assert.False(vm.CanCancel);
            return Task.FromResult(new UpdateFlowResult(true));
        });
        vm.FlowFactory = () => flow;

        await vm.StartFlowAsync();
        Assert.False(vm.CanCancel);
    });

    [Fact]
    public async Task PendingRestart_Raises_Event_And_Suppresses_Close_Guard() => await RunOnImmediateContext(async () =>
    {
        var vm = NewViewModel(() => new ScriptedFlow((progress, _) =>
        {
            progress.Report(new UpdateFlowProgress(2));
            return Task.FromResult(new UpdateFlowResult(true, PendingRestart: true));
        }));
        var restartRequested = false;
        vm.PendingRestartRequested += () => restartRequested = true;

        await vm.StartFlowAsync();

        Assert.True(restartRequested);
        Assert.True(vm.SuppressCloseGuard);
        // 진행 헤드라인("설치 중 — 재시작") 그대로 프로세스와 함께 소멸하는 설계 — Done으로 바꾸지 않는다
        Assert.Equal(UpdateFlowState.Progress, vm.State);
    });

    [Fact]
    public async Task Failure_Result_Shows_Error_Card() => await RunOnImmediateContext(async () =>
    {
        var failure = new InstallFailure(InstallFailureClass.Network, "detail-x", "advice-y");
        var vm = NewViewModel(() => new ScriptedFlow((_, _) =>
            Task.FromResult(new UpdateFlowResult(false, failure))));

        await vm.StartFlowAsync();

        Assert.Equal(UpdateFlowState.Error, vm.State);
        Assert.Equal("detail-x", vm.ErrorDetail);
        Assert.Equal("advice-y", vm.ErrorAdvice);
    });

    [Fact]
    public async Task User_Cancel_Returns_To_Ready() => await RunOnImmediateContext(async () =>
    {
        var vm = NewViewModel(null);
        var flow = new ScriptedFlow(async (progress, ct) =>
        {
            progress.Report(new UpdateFlowProgress(0, 10));
            vm.CancelCommand.Execute(null); // 사용자 취소
            await Task.Delay(Timeout.Infinite, ct); // 취소로 즉시 OCE
            return new UpdateFlowResult(true);
        });
        vm.FlowFactory = () => flow;

        await vm.StartFlowAsync();

        Assert.Equal(UpdateFlowState.Ready, vm.State);
        Assert.Equal(0, vm.GaugePct);
        Assert.Equal("Todo", vm.DownloadStepState);
    });

    [Fact]
    public async Task Null_Factory_Or_Null_Flow_Is_NoOp() => await RunOnImmediateContext(async () =>
    {
        var vm = NewViewModel(null);
        await vm.StartFlowAsync();
        Assert.Equal(UpdateFlowState.Ready, vm.State);

        vm.FlowFactory = () => null; // 게이트 차단/업데이트 소멸
        await vm.StartFlowAsync();
        Assert.Equal(UpdateFlowState.Ready, vm.State);
    });

    [Fact]
    public async Task Unhandled_Flow_Exception_Becomes_Error_Card_Not_Crash() => await RunOnImmediateContext(async () =>
    {
        var vm = NewViewModel(() => new ScriptedFlow((_, _) =>
            throw new InvalidOperationException("boom")));

        await vm.StartFlowAsync();

        Assert.Equal(UpdateFlowState.Error, vm.State);
        Assert.Contains("boom", vm.ErrorDetail);
    });

    [Fact]
    public void Direct_Done_And_Failure_Entry_For_Restart_Continuity()
    {
        var vm = NewViewModel(null);
        vm.ShowDone();
        Assert.Equal(UpdateFlowState.Done, vm.State);
        Assert.Equal(100, vm.GaugePct);

        vm.ShowFailure(new InstallFailure(InstallFailureClass.Unknown, "d", "a"));
        Assert.Equal(UpdateFlowState.Error, vm.State);
    }
}
