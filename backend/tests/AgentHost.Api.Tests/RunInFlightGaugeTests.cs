using System.Diagnostics.Metrics;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// La jauge <c>agenthost.run.in_flight</c> — la profondeur de la file d'exécution.
///
/// <b>Pourquoi ce fichier existe.</b> <c>RunQueued</c> était défini et appelé <b>nulle part</b> :
/// la jauge ne faisait que décroître, et tout tableau de bord d'exploitation affichait une
/// profondeur de file NÉGATIVE. Le défaut ne se voyait pas à la lecture — les deux côtés du compteur
/// existaient, correctement étiquetés — et aucun test ne l'attrapait, parce que tous les tests
/// existants utilisent un instrument sans collecteur : il s'exécute, il n'écrit nulle part, et
/// personne ne regarde la somme.
///
/// Il a fallu lire la <b>vraie sortie</b> de <c>/metrics</c> sur un backend réel pour le voir :
/// <c>agenthost_run_in_flight -1</c> après un seul run. Ces tests branchent donc un
/// <see cref="MeterListener"/> — le même mécanisme que l'exporteur — pour que la somme soit
/// observée, et non seulement émise.
/// </summary>
public class RunInFlightGaugeTests
{
    [Fact]
    public async Task A_run_that_starts_and_finishes_leaves_the_queue_at_zero()
    {
        using var observer = new InFlightObserver();
        var sut = CreateSut(observer.Metrics);
        var run = NewRun(RunStatus.Pending);

        await sut.TransitionAsync(run, RunStatus.Queued);
        Assert.Equal(1, observer.Depth);

        await sut.TransitionAsync(run, RunStatus.Provisioning);
        await sut.TransitionAsync(run, RunStatus.Preparing);
        await sut.TransitionAsync(run, RunStatus.Running);
        // Les états intermédiaires ne touchent pas la jauge : le run est toujours en vol.
        Assert.Equal(1, observer.Depth);

        await sut.TransitionAsync(run, RunStatus.Finalizing);
        await sut.TransitionAsync(run, RunStatus.Succeeded);
        Assert.Equal(0, observer.Depth);
    }

    [Fact]
    public async Task The_queue_depth_never_goes_negative()
    {
        using var observer = new InFlightObserver();
        var sut = CreateSut(observer.Metrics);

        // Trois runs complets, exactement ce qu'un backend fait de sa journée.
        for (var i = 0; i < 3; i++)
        {
            var run = NewRun(RunStatus.Pending);
            await sut.TransitionAsync(run, RunStatus.Queued);
            await sut.TransitionAsync(run, RunStatus.Cancelled);
            // C'est l'assertion qui aurait attrapé le défaut : sans l'incrément, on serait à -1,
            // puis -2, puis -3.
            Assert.True(observer.Depth >= 0, $"Queue depth went negative: {observer.Depth}");
        }

        Assert.Equal(0, observer.Depth);
    }

    [Fact]
    public async Task A_run_that_never_reaches_the_queue_is_not_counted()
    {
        using var observer = new InFlightObserver();
        var sut = CreateSut(observer.Metrics);
        var run = NewRun(RunStatus.Pending);

        // Refusé avant lancement : il n'a jamais occupé la file, et l'y compter puis l'en retirer
        // ferait osciller la jauge pour rien.
        await sut.TransitionAsync(run, RunStatus.Rejected);

        Assert.Equal(0, observer.Depth);
    }

    // ---- helpers ----

    /// <summary>
    /// Observe la jauge comme le ferait l'exporteur Prometheus : un <see cref="MeterListener"/>
    /// abonné au seul instrument qui nous intéresse, dont il cumule les mesures.
    /// </summary>
    private sealed class InFlightObserver : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ServiceProvider _provider;
        private long _depth;

        public AgentHostMetrics Metrics { get; }

        public long Depth => Interlocked.Read(ref _depth);

        public InFlightObserver()
        {
            _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var factory = _provider.GetRequiredService<IMeterFactory>();
            Metrics = new AgentHostMetrics(factory);

            // Le Meter de CETTE fabrique, par identité et non par nom.
            //
            // Un MeterListener est global au processus : filtrer sur le nom ferait entrer les
            // mesures de toutes les autres classes de test qui construisent un AgentHostMetrics —
            // xUnit les exécute en parallèle — et la somme observée ne serait plus celle de ce
            // test. `IMeterFactory` mémorise ses Meters par nom, donc l'instance rendue ici est
            // exactement celle qu'AgentHostMetrics a obtenue.
            var meter = factory.Create(AgentHostMetrics.MeterName);

            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) &&
                    instrument.Name == "agenthost.run.in_flight")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
                Interlocked.Add(ref _depth, measurement));
            _listener.Start();
        }

        public void Dispose()
        {
            _listener.Dispose();
            _provider.Dispose();
        }
    }

    private static RunStateMachine CreateSut(AgentHostMetrics metrics)
    {
        var runRepo = new Mock<IRunRepository>();
        runRepo.Setup(r => r.UpdateAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var eventBus = new Mock<IEventBus>();
        eventBus.Setup(e => e.PublishAsync(It.IsAny<RunEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var webhooks = new Mock<IWebhookDispatcher>();
        webhooks.Setup(w => w.DispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new RunStateMachine(runRepo.Object, eventBus.Object, webhooks.Object, Serilog.Log.Logger, metrics);
    }

    private static Run NewRun(RunStatus status) => new()
    {
        Id = UlidGenerator.NewUlid(),
        OrgId = "org1",
        ProjectId = "proj1",
        AgentId = "agent1",
        AgentVersionId = "ver1",
        Status = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
