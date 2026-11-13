using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using SsrsPortal.Mock;
namespace SsrsPortal.Tests;

public sealed class LogHygieneTests
{
    [Fact]
    public async Task ApplicationAndSoapLogsRetainFaultsWithoutSuccessfulOutputOrSessionTokens()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var id = await user.Id();
        var input = Harness.Inputs(); input.Values!["note"] = ["RAW_SELECTION_SENTINEL"];
        var success = await user.Post("/api/reports/" + id + "/run", input); success.EnsureSuccessStatusCode();
        f.Mock.Failure = MockFailure.Fault; f.Mock.FailOperation = "GetItemParameters";
        var response = await user.Post("/api/reports/" + id + "/parameters", input); Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var logs = string.Join('\n', f.Logs.Messages);
        foreach (var value in new[] { "Synthetic library", "Synthetic collection", "synthetic-execution", "Demonstration row" })
        {
            Assert.DoesNotContain(value, logs);
        }
        Assert.Contains("SOAP operation", logs); Assert.Contains("Portal failure service", logs);
        Assert.Contains("faultstring", logs); Assert.Contains("private SQL", logs);
    }
}

public sealed class CaptureProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Capture(Messages);
    public void Dispose() { }
    private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
    }
}
