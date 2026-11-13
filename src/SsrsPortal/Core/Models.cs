namespace SsrsPortal.Core;

public sealed class PortalException(string code, int status = 502, string? message = null, Exception? innerException = null)
    : Exception(message ?? code, innerException)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public static string Description(string code) => code switch
    {
        "session" => "This report session expired or is unavailable. Run the report again.",
        "capacity" => "The report service is busy. Cancel an open report or try again shortly.",
        "values" => "Review the required inputs and choose current valid values.",
        "selection-limit" => "This selection exceeds the supported selection or request size limit. Select fewer choices to continue.",
        "query" => "Report choices are temporarily unavailable. Try again later.",
        "timeout" => "The report request timed out. Try a smaller range or run it again.",
        "cancelled" => "The report request was cancelled. Run the report again when ready.",
        "credentials" => "SSRS could not authorize this request. Reports requiring a data-source credential prompt cannot run in this portal. Contact support.",
        "limit" => "This report exceeds a configured size or choice limit. Narrow the earlier inputs, try a smaller range, or ask an administrator to review the configured limits.",
        "empty" => "The report returned no output. Review the inputs or contact support.",
        "notfound" => "This report is unavailable under the current catalog policy.",
        "catalog" => "The report library is temporarily unavailable. Try again shortly.",
        "format" => "This output format is unsupported.",
        "resource" => "A report resource could not be displayed safely.",
        "view-stale" => "This report view has changed. Reload report output before using its controls.",
        "synchronize" => "The report state may have changed. The displayed output is out of date. Reload report output to synchronize it.",
        "toggle-missing" => "This expand or collapse control is no longer available. Reload report output to get current controls.",
        _ => "The report service is unavailable. Try again later or contact support."
    };
}

public sealed record CatalogItem(string Path, string Name, string Type, bool Hidden);
public sealed record CatalogEntry(string Id, string? ParentId, string Name, string Type, string? ReportUrl = null);
public sealed record CatalogReport(string Id, string Name, string Path, string Policy, string? ParentId = null);
public sealed record ParameterValue(string Name, string? Value);
public sealed record OptionValue(string? Value, string Label);
public sealed record ReportParameter(string Name, string Prompt, string Type, string State, bool Nullable,
    bool AllowBlank, bool MultiValue, bool Hidden, bool QueryBased, bool QueryDefault, string[] Dependencies,
    string?[] Defaults, OptionValue[] Options);
public sealed record ParameterField(string Name, string Prompt, string Type, string State, bool Nullable,
    bool AllowBlank, bool MultiValue, bool QueryBased, string[] Dependencies, string?[] Selected,
    OptionValue[] Options, int OptionCount, string? Error, OptionValue[] SelectedOptions, bool HasOptions,
    int SelectionLimit = 5000, bool AllSelected = false, string OptionsKey = "", string Width = "text");
public sealed record ParameterForm(ParameterField[] Fields, Dictionary<string, string?[]> Values, bool CanRun, string[] Defaulted, string[]? LinkErrors = null);
public sealed record ReportLinkInput(string Query);
public sealed record ReportLinkForm(ParameterForm Form, bool HasParameters, bool NoExecute);
// Missing keys initialize from SSRS. Present [] means intentionally cleared;
// [null] and [""] are distinct selected values. Changed identifies user edits
// so dependent defaults can follow the new parent context. Defaulted tracks which
// selections still came from SSRS, preserving valid choices edited by the user.
public sealed record ParameterInput(Dictionary<string, string?[]>? Values, bool Initial = false, string[]? Changed = null, string[]? Defaulted = null, string? ReplaceToken = null);
public sealed record ParameterChoices(OptionValue[] Options, string OptionsKey);
public sealed record OptionSelection(OptionValue[] Options);
public sealed record ExecutionInfo(string ExecutionId, int NumPages, bool CredentialsRequired, bool ParametersRequired);
public sealed record RenderOutput(byte[] Bytes, string MimeType, string Extension, string Encoding, string[] Streams);
public sealed record RunResult(string Token, string Name, int Page, int? Pages, long Revision = 0);
public sealed record PageResult(int Page, int? Pages, string ViewerUrl, long Revision = 0);
public sealed record OutputFormat(string Soap, string Mime, string Extension)
{
    public static readonly IReadOnlyDictionary<string, OutputFormat> All = new Dictionary<string, OutputFormat>(StringComparer.Ordinal)
    {
        ["pdf"] = new("PDF", "application/pdf", "pdf"),
        ["xlsx"] = new("EXCELOPENXML", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
        ["docx"] = new("WORDOPENXML", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx"),
        ["csv"] = new("CSV", "text/csv", "csv")
    };
}
