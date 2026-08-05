using AgentHost.Api.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace AgentHost.Api.Hubs;

/// <summary>
/// Binds the connection's authenticated principal into the scoped <see cref="ICallerContext"/> for
/// the duration of each hub method invocation.
///
/// Without this, anything downstream of a hub method that asks "who is calling?" sees nobody:
/// <see cref="CallerContext"/> falls back to <see cref="IHttpContextAccessor"/>, whose HttpContext
/// exists only for the initial negotiate request and is null during an invocation. That silently
/// broke role-gated approvals over the hub — <c>RunService.ApproveAsync</c> refuses a decision it
/// cannot attribute, so <c>RunHub.ApproveStep</c> failed closed on exactly the approvals the
/// feature exists for — and recorded "unknown" as the decider on the ones that did go through.
///
/// A filter rather than a per-method call so a new hub method cannot forget to do it. SignalR
/// creates one DI scope per invocation and exposes it as
/// <see cref="HubInvocationContext.ServiceProvider"/>; the hub and its dependencies are resolved
/// from that same scope, so binding here reaches them.
/// </summary>
public class CallerContextHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        invocationContext.ServiceProvider
            .GetService<ICallerContextBinder>()
            ?.Bind(invocationContext.Context.User);

        return await next(invocationContext);
    }
}
