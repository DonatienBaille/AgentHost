using AgentHost.Api.Domain;
using AgentHost.Api.Hubs;
using AgentHost.Api.Repositories;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace AgentHost.Api.Services;

public interface IEventBus
{
    Task PublishAsync(RunEvent evt, CancellationToken ct = default);
}

/// <summary>
/// Publishes run events: persists to `run_events` (assigning the next `seq` for the run
/// atomically) then broadcasts to the SignalR group `run-{runId}` (spec section 7.2).
/// </summary>
public class SignalREventBus : IEventBus
{
    private readonly IHubContext<RunHub> _runHubContext;
    private readonly IRunEventRepository _eventRepository;
    private readonly ILogger _logger;

    public SignalREventBus(
        IHubContext<RunHub> runHubContext,
        IRunEventRepository eventRepository,
        ILogger logger)
    {
        _runHubContext = runHubContext;
        _eventRepository = eventRepository;
        _logger = logger;
    }

    public async Task PublishAsync(RunEvent evt, CancellationToken ct = default)
    {
        try
        {
            await _eventRepository.InsertAsync(evt, ct);

            await _runHubContext.Clients
                .Group($"run-{evt.RunId}")
                .SendAsync("RunEvent", evt, cancellationToken: ct);

            _logger.Debug("Event {EventType} published for run {RunId}",
                evt.EventType, evt.RunId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error publishing event for run {RunId}", evt.RunId);
        }
    }
}
