using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Moq;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// Covers the run lifecycle transition table (spec section 8.2), including the terminal-state
/// invariant from section 8.1.
/// </summary>
public class RunStateMachineTests
{
    private static RunStateMachine CreateSut(out Mock<IRunRepository> runRepo, out Mock<IEventBus> eventBus)
    {
        runRepo = new Mock<IRunRepository>();
        runRepo.Setup(r => r.UpdateAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        eventBus = new Mock<IEventBus>();
        eventBus.Setup(e => e.PublishAsync(It.IsAny<RunEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return new RunStateMachine(runRepo.Object, eventBus.Object, Serilog.Log.Logger);
    }

    private static Run NewRun(RunStatus status) => new()
    {
        Id = "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        OrgId = "org1",
        ProjectId = "proj1",
        AgentId = "agent1",
        AgentVersionId = "ver1",
        Status = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task PendingToQueued_IsValid()
    {
        var sut = CreateSut(out var runRepo, out var eventBus);
        var run = NewRun(RunStatus.Pending);

        await sut.TransitionAsync(run, RunStatus.Queued);

        Assert.Equal(RunStatus.Queued, run.Status);
        runRepo.Verify(r => r.UpdateAsync(run, It.IsAny<CancellationToken>()), Times.Once);
        eventBus.Verify(e => e.PublishAsync(It.Is<RunEvent>(ev => ev.EventType == "run.status_changed"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueuedToRunning_IsInvalid_MustGoThroughProvisioningAndPreparing()
    {
        var sut = CreateSut(out _, out _);
        var run = NewRun(RunStatus.Queued);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.TransitionAsync(run, RunStatus.Running));
    }

    [Theory]
    [InlineData(RunStatus.Pending)]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.Provisioning)]
    [InlineData(RunStatus.Preparing)]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.AwaitingApproval)]
    [InlineData(RunStatus.AwaitingInput)]
    [InlineData(RunStatus.Finalizing)]
    public async Task AnyNonTerminalState_CanTransitionToCancelled(RunStatus from)
    {
        var sut = CreateSut(out _, out _);
        var run = NewRun(from);

        await sut.TransitionAsync(run, RunStatus.Cancelled);

        Assert.Equal(RunStatus.Cancelled, run.Status);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    [InlineData(RunStatus.TimedOut)]
    [InlineData(RunStatus.BudgetExceeded)]
    [InlineData(RunStatus.Rejected)]
    [InlineData(RunStatus.InfraError)]
    public async Task TerminalState_CannotTransitionToAnything(RunStatus terminalStatus)
    {
        var sut = CreateSut(out _, out _);
        var run = NewRun(terminalStatus);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.TransitionAsync(run, RunStatus.Running));
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    [InlineData(RunStatus.TimedOut)]
    [InlineData(RunStatus.BudgetExceeded)]
    [InlineData(RunStatus.Rejected)]
    [InlineData(RunStatus.InfraError)]
    public void IsTerminal_MatchesSpecList(RunStatus status)
    {
        Assert.True(status.IsTerminal());
    }

    [Theory]
    [InlineData(RunStatus.Pending)]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.Provisioning)]
    [InlineData(RunStatus.Preparing)]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.AwaitingApproval)]
    [InlineData(RunStatus.AwaitingInput)]
    [InlineData(RunStatus.Finalizing)]
    public void IsTerminal_NonTerminalStatesReturnFalse(RunStatus status)
    {
        Assert.False(status.IsTerminal());
    }

    [Fact]
    public async Task FullHappyPath_PendingToSucceeded()
    {
        var sut = CreateSut(out _, out _);
        var run = NewRun(RunStatus.Pending);

        await sut.TransitionAsync(run, RunStatus.Queued);
        await sut.TransitionAsync(run, RunStatus.Provisioning);
        await sut.TransitionAsync(run, RunStatus.Preparing);
        await sut.TransitionAsync(run, RunStatus.Running);
        await sut.TransitionAsync(run, RunStatus.Finalizing);
        await sut.TransitionAsync(run, RunStatus.Succeeded);

        Assert.Equal(RunStatus.Succeeded, run.Status);
    }

    [Fact]
    public async Task RunningToAwaitingApproval_ThenBackToRunning_IsValid()
    {
        var sut = CreateSut(out _, out _);
        var run = NewRun(RunStatus.Running);

        await sut.TransitionAsync(run, RunStatus.AwaitingApproval);
        Assert.Equal(RunStatus.AwaitingApproval, run.Status);

        await sut.TransitionAsync(run, RunStatus.Running);
        Assert.Equal(RunStatus.Running, run.Status);
    }

    [Fact]
    public async Task RunningToAwaitingInput_ThenBackToRunning_IsValid()
    {
        var sut = CreateSut(out _, out _);
        var run = NewRun(RunStatus.Running);

        await sut.TransitionAsync(run, RunStatus.AwaitingInput);
        Assert.Equal(RunStatus.AwaitingInput, run.Status);

        await sut.TransitionAsync(run, RunStatus.Running);
        Assert.Equal(RunStatus.Running, run.Status);
    }

    [Fact]
    public async Task FinalizingToFailed_IsValid()
    {
        var sut = CreateSut(out _, out _);
        var run = NewRun(RunStatus.Finalizing);

        await sut.TransitionAsync(run, RunStatus.Failed);

        Assert.Equal(RunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task PreparingToFinalizing_IsInvalid()
    {
        var sut = CreateSut(out _, out _);
        var run = NewRun(RunStatus.Preparing);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.TransitionAsync(run, RunStatus.Finalizing));
    }

    [Theory]
    [InlineData(RunStatus.Pending)]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.Provisioning)]
    [InlineData(RunStatus.Preparing)]
    [InlineData(RunStatus.Running)]
    public void IsValidTransition_ToBudgetExceeded_AllowedFromAnyNonTerminalState(RunStatus from)
    {
        var sut = CreateSut(out _, out _);
        Assert.True(sut.IsValidTransition(from, RunStatus.BudgetExceeded));
    }
}
