using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Coverage of the AgentMemoryHub's group membership over a real SignalR connection.
///
/// <c>LeaveProjectMemory</c> exists because the frontend had no way to stop listening: switching
/// projects, or tearing the memory page down, left the connection in the old project's group and
/// every subsequent broadcast triggered a pointless refetch of the *new* project. These tests pin
/// that the membership really is dropped server-side, not just ignored client-side.
///
/// Transport is forced to long polling for the same reason as RunHubTests: TestServer has no real
/// socket, and long polling exercises the same hub dispatch path.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AgentMemoryHubTests : IAsyncLifetime
{
    private readonly AgentHostApiFactory _factory;

    private HttpClient _owner = default!;
    private AuthResponse _auth = default!;
    private Project _project = default!;

    public AgentMemoryHubTests(AgentHostApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        (_owner, _auth, _project, _) = await TestData.CreateFullFixtureAsync(_factory, TestData.Suffix());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task JoinProjectMemory_ThenUpdate_BroadcastsToTheGroup()
    {
        await using var connection = await ConnectAsync(_auth.Token);
        var updates = new UpdateSink(connection);

        await connection.InvokeAsync("JoinProjectMemory", _project.Id);
        await connection.InvokeAsync("UpdateMemory", _project.Id, NoteUpdate("first"));

        Assert.True(await updates.WaitAsync(TimeSpan.FromSeconds(10)),
            "A member of the group should receive the memoryUpdated broadcast");
    }

    [Fact]
    public async Task LeaveProjectMemory_StopsTheBroadcastsReachingTheConnection()
    {
        await using var connection = await ConnectAsync(_auth.Token);

        // Join, prove broadcasts arrive, then leave and prove they stop. Doing both halves on one
        // connection is what makes this a test of the departure rather than of the transport.
        await connection.InvokeAsync("JoinProjectMemory", _project.Id);
        var whileJoined = new UpdateSink(connection);
        await connection.InvokeAsync("UpdateMemory", _project.Id, NoteUpdate("while joined"));
        Assert.True(await whileJoined.WaitAsync(TimeSpan.FromSeconds(10)));

        await connection.InvokeAsync("LeaveProjectMemory", _project.Id);

        var afterLeaving = new UpdateSink(connection);
        // UpdateMemory itself still works — leaving a group is not a loss of authorization, only of
        // subscription — so the write goes through and the broadcast simply has no one to reach.
        await connection.InvokeAsync("UpdateMemory", _project.Id, NoteUpdate("after leaving"));
        Assert.False(await afterLeaving.WaitAsync(TimeSpan.FromSeconds(1)),
            "The connection left the group, so it must not receive further memoryUpdated broadcasts");
    }

    [Fact]
    public async Task LeaveProjectMemory_ForAGroupNeverJoined_IsSilentlyHarmless()
    {
        var otherAuth = await TestData.RegisterAsync(_factory.CreateClient());

        await using var connection = await ConnectAsync(otherAuth.Token);
        var errors = new ErrorSink(connection);

        // Another tenant's project id, and one that does not exist at all. Neither is an error:
        // giving up a membership only ever touches your own connection, so the hub must not turn
        // this into an existence oracle by answering differently for the two.
        await connection.InvokeAsync("LeaveProjectMemory", _project.Id);
        await connection.InvokeAsync("LeaveProjectMemory", UlidGenerator.NewUlid());

        Assert.False(await errors.WaitAsync(TimeSpan.FromMilliseconds(500)),
            "Leaving a group must never report anything back to the caller");
    }

    // ---- helpers ----

    private static MemoryUpdate NoteUpdate(string text) => new()
    {
        NewNotes = new List<Note>
        {
            new() { Id = UlidGenerator.NewUlid(), AgentName = "test", Text = text, Timestamp = DateTime.UtcNow },
        },
    };

    private async Task<HubConnection> ConnectAsync(string accessToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/memory"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true)
            .Build();

        await connection.StartAsync();
        return connection;
    }

    /// <summary>
    /// Waits on a named hub message. Both the hub's broadcasts and its errors are separate messages
    /// that arrive *after* <c>InvokeAsync</c> returns, so every assertion here has to wait for one
    /// or wait a grace period out — never read a list immediately.
    /// </summary>
    private class MessageSink
    {
        private readonly SemaphoreSlim _received = new(0);

        protected MessageSink(HubConnection connection, string method) =>
            connection.On<object>(method, _ => _received.Release());

        public Task<bool> WaitAsync(TimeSpan timeout) => _received.WaitAsync(timeout);
    }

    private sealed class UpdateSink : MessageSink
    {
        public UpdateSink(HubConnection connection) : base(connection, "memoryUpdated") { }
    }

    private sealed class ErrorSink : MessageSink
    {
        public ErrorSink(HubConnection connection) : base(connection, "error") { }
    }
}
