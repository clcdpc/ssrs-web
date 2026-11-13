using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NLog;
using NLog.Layouts;
using SsrsPortal.Soap;

namespace SsrsPortal.Logging;

// Deliberately thread-sensitive: NLog precalculates this layout BEFORE its async queue,
// while ILogger scopes and request secrets are still available on the producer thread.
internal sealed class SafeLogLayout(Func<string?> clientSecret, IHttpContextAccessor requests) : Layout
{
    internal const int MaximumEventCharacters = 128 * 1024;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly ConditionalWeakTable<Exception, object> _renderedExceptions = new();
    internal bool HasException(Exception exception) => _renderedExceptions.TryGetValue(exception, out _);

    protected override string GetFormattedMessage(LogEventInfo logEvent)
    {
        try { return FormatRecord(logEvent); }
        catch (Exception exception)
        {
            // A hostile value/formatter must not silently suppress the event or expose it
            // unfiltered. Keep a safe actionable record with the renderer failure type.
            return $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logEvent.Level.Name.ToUpperInvariant()}] SsrsPortal.Logging\nMessage=Log event formatting failed ({exception.GetType().Name}); inspect the event's structured value types.\n---";
        }
    }

    private string FormatRecord(LogEventInfo logEvent)
    {
        var request = requests.HttpContext;
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        var budget = new ValueBudget();
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in ScopeContext.GetAllProperties().Take(128)) { properties[pair.Key] = SafeValue(pair.Key, pair.Value, 0, secrets, budget); }
        foreach (var pair in logEvent.Properties.Take(128))
        {
            var key = pair.Key.ToString() ?? "Property";
            properties[key] = SafeValue(key, pair.Value, 0, secrets, budget);
        }
        if (request is not null) { properties["Trace"] = request.TraceIdentifier; }
        var nested = ScopeContext.GetAllNestedStates().Take(16).Select(value => SafeValue("Scope", value, 0, secrets, budget)).ToArray();
        if (nested.Length > 0) { properties["Scopes"] = nested; }

        var text = new StringBuilder();
        text.Append(new DateTimeOffset(logEvent.TimeStamp.ToLocalTime()).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(logEvent.Level.Name.ToUpperInvariant()).Append("] ").AppendLine(logEvent.LoggerName);
        text.Append("Message=").AppendLine(FailureDiagnostics.Bound(FailureDiagnostics.Filter(logEvent.FormattedMessage, clientSecret(), request, secrets), 32768));
        text.Append("Template=").AppendLine(FailureDiagnostics.Bound(FailureDiagnostics.Filter(logEvent.Message, clientSecret(), request, secrets), 8192));
        text.Append("Properties=").AppendLine(JsonSerializer.Serialize(properties, Json));
        if (logEvent.Exception is { } exception)
        {
            text.AppendLine("Exception=").AppendLine(FailureDiagnostics.ExceptionText(exception, 65536, value => FailureDiagnostics.Filter(value, clientSecret(), request, secrets)));
            _renderedExceptions.GetValue(exception, _ => new object());
        }
        // Filter the entire rendered record, including raw exceptions, templates and scopes.
        // Do this BEFORE final truncation so credentials cannot leak at a truncation boundary.
        return FailureDiagnostics.Bound(FailureDiagnostics.Filter(text.ToString(), clientSecret(), request, secrets), MaximumEventCharacters) + "\n---";
    }

    private object? SafeValue(string key, object? value, int depth, HashSet<string> secrets, ValueBudget budget)
    {
        if (FailureDiagnostics.SecretKey(key))
        {
            CollectSecrets(value, secrets, 0);
            return "[redacted]";
        }
        if (--budget.Nodes < 0 || budget.Characters < 32) { return "[truncated]"; }
        if (value is null || value is bool || value is byte || value is int || value is long || value is float || value is double || value is decimal)
        {
            return value;
        }
        if (depth >= 8) { return "[truncated]"; }
        if (value is string text)
        {
            var result = FailureDiagnostics.Bound(FailureDiagnostics.Filter(text, clientSecret(), requests.HttpContext, secrets), Math.Min(key == "DiagnosticDetails" ? 98304 : 8192, budget.Characters));
            budget.Characters -= result.Length;
            return result;
        }
        if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            var result = new Dictionary<string, object?>();
            foreach (var pair in pairs.Take(65))
            {
                if (result.Count >= 64) { result["[truncated]"] = true; break; }
                result[pair.Key] = SafeValue(pair.Key, pair.Value, depth + 1, secrets, budget);
            }
            return result;
        }
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>();
            foreach (DictionaryEntry item in dictionary)
            {
                if (result.Count >= 64) { result["[truncated]"] = true; break; }
                var name = item.Key.ToString() ?? "Property";
                result[name] = SafeValue(name, item.Value, depth + 1, secrets, budget);
            }
            return result;
        }
        if (value is IEnumerable sequence)
        {
            return sequence.Cast<object?>().Take(65).Select((item, index) => index == 64 ? "[truncated]" : SafeValue(key, item, depth + 1, secrets, budget)).ToArray();
        }
        return SafeValue(key, Convert.ToString(value, CultureInfo.InvariantCulture), depth + 1, secrets, budget);
    }

    private static void CollectSecrets(object? value, HashSet<string> secrets, int depth)
    {
        if (value is null || depth >= 8 || secrets.Count >= 256) { return; }
        if (value is string text)
        {
            if (text.Length > 0) { secrets.Add(text); }
        }
        else if (value is IDictionary dictionary)
        {
            var count = 0;
            foreach (DictionaryEntry pair in dictionary)
            {
                if (count++ >= 64) { break; }
                CollectSecrets(pair.Value, secrets, depth + 1);
            }
        }
        else if (value is IEnumerable sequence)
        {
            foreach (var item in sequence.Cast<object?>().Take(64)) { CollectSecrets(item, secrets, depth + 1); }
        }
        else
        {
            var secret = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(secret)) { secrets.Add(secret); }
        }
    }

    private sealed class ValueBudget
    {
        internal int Nodes = 256;
        internal int Characters = 98304;
    }
}
