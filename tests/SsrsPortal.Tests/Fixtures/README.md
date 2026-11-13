# Independent SOAP and renderer fixtures

The literal XML envelopes are independent of `SsrsPortal.Mock` response builders. Names and sequence are grounded in Microsoft's published WSDLs; paths and report content are synthetic. Tests use the literals against the SOAP client and separately validate raw requests against the in-process server. This evidence anchors wire shape, not live SSRS compatibility, authentication, data, rendering, or export fidelity.

## SOAP services and execution contract

Microsoft documents the management endpoint as `/ReportServer/ReportService2010.asmx` and the execution endpoint as `/ReportServer/ReportExecution2005.asmx` ([SOAP API](https://learn.microsoft.com/en-us/sql/reporting-services/report-server-web-service/accessing-the-soap-api?view=sql-server-ver17), [endpoint names](https://learn.microsoft.com/en-us/sql/reporting-services/report-server-web-service/methods/report-server-web-service-endpoints?view=sql-server-ver17)). `properties.xml` anchors the management `GetProperties` request (`Properties/Property/Name`) and response (`GetPropertiesResponse/Values/Property/Name/Value`) shape to the [management WSDL](https://learn.microsoft.com/en-us/openspecs/sql_server_protocols/ms-rswsrm2010/8ec433c2-0f56-42d0-af06-a4e40370ba96).

The execution service uses namespace `http://schemas.microsoft.com/sqlserver/2005/06/30/reporting/reportingservices`. SOAP 1.1 `SOAPAction` is that namespace, a slash, and the exact operation name, quoted. The fixture set covers these wrappers:

| Operation | Request members | Response members | Execution header |
| --- | --- | --- | --- |
| `LoadReport` | `Report`, optional `HistoryID` | `executionInfo` | Output |
| `SetExecutionParameters` | `Parameters`, `ParameterLanguage` | `executionInfo` | Input |
| `GetExecutionInfo` | None | `executionInfo` | Input |
| `Render` | `Format`, `DeviceInfo` | `Result`, `Extension`, `MimeType`, `Encoding`, `Warnings`, `StreamIds` | Input |
| `RenderStream` | `Format`, `StreamID`, `DeviceInfo` | `Result`, `Encoding`, `MimeType` | Input |
| `ToggleItem` | `ToggleID` | exactly one required `Found` Boolean | Input |

`GetExecutionInfo` has an empty request type. `DeviceInfo` is an XML-escaped string. `LoadReport` returns the execution ID in the SOAP header and `executionInfo`; later operations send it in the execution header. The other listed operations do not declare an output execution header. `Result` carries base64 bytes and `StreamIds/string` carries secondary stream identifiers. See the [execution WSDL](https://sqlprotocoldocs-cgcjdngdb5dee9c6.b02.azurefd.net/MS-RSWSRE2005/%5BMS-RSWSRE2005%5D.pdf), linked from Microsoft's [protocol index](https://learn.microsoft.com/en-us/openspecs/sql_server_protocols/ms-rswsre2005/96c33524-52c1-4358-a23a-6921db74211c).

The XML Boolean parser accepts `true`, `false`, `1`, and `0` with schema whitespace handling. Missing or malformed required `GetExecutionInfo` `CredentialsRequired` / `ParametersRequired` flags and `ToggleItemResponse/Found` fail closed. Parameter `xsi:nil="true"` or `"1"` represents null. For execution values, null omits the optional `Value` element while blank sends an included empty `Value`; these states are not interchangeable. `ParameterValue` members follow WSDL order: `Name`, `Value`, `Label`.

`ExecutionSoapContractTests` keeps expected namespaces, actions, request bodies, and endpoint names as fixed literals independent of both implementations. `SoapEndpointContractTests` covers the documented endpoint names and rejects legacy/swapped service URLs. Do not replace these expectations with constants imported from the client or mock. The older Microsoft CustomSecuritySample proxy uses a legacy namespace and is not the protocol authority.

## Parameter defaults and dates

`parameter-defaults.xml` and `cascade-defaults.xml` cover typed defaults, `Dependency`, omitted values, explicit blank and nil values, multivalue defaults, hidden parameters, and current-parent resolution. `ParameterDefaultsTests` consumes these fixed envelopes without using the synthetic response builder, then checks the resulting `SetExecutionParameters` representation.

`dev-date-defaults.xml` is a reconstructed regression envelope containing the reported strings `9/1/2026 12:00:00 AM` and `9/30/2026 11:59:59 PM`; it is not a new live capture. The accepted invariant form `M/d/yyyy h:mm:ss tt` normalizes to local clock text without timezone conversion, alongside supported ISO forms. The [workspace reference](../../../docs/behavior/report-workspace.md#parameter-state-dependencies-and-dates) documents parsing, offsets, provenance, refresh, and boundary limits. Known-record filtering acceptance remains open in the [date boundary acceptance note](../../../docs/date-parameter-boundary-acceptance.md).

## ReportViewer toggle markup and layout

`dev-reportviewer-toggle.html` is a redacted structural fixture based on a real DEV standard-viewer HTML capture. It preserves the relevant callback/keypress grammar, state markers, resource shape, and runtime toggle identifier, but it is not a capture of the portal's SOAP HTML5 fragment. `ReportViewerToggleTests` derives structural cases from it. Actual SOAP-fragment compatibility remains a live gate in the [report expansion reference](../../../docs/behavior/report-expand-collapse.md#live-dev-acceptance).

`toggle.xml` anchors the required `ToggleItemResponse/Found` response, including Boolean lexical forms and strict shape rejection. `Found=true` means the renderer recognized the ID; it does not report the resulting expanded state. The [same-execution update contract](../../../docs/behavior/report-expand-collapse.md#same-execution-update-and-recovery) describes synchronization and recovery.

`holds-ratio-layout.html` reconstructs nested HTML5 tables and an eighteen-column sizing row from operator measurements: 1,881.9px total, with reference columns near 66px, 312px, 229px, and 64px and 2pt header padding. Other dimensions, labels, and values are invented. The mock embeds the file in its Render pipeline; Chromium compares raw and sanitized rendering at six widths. This fixture documents one authored layout shape and does not establish general live report fidelity. See [authored report layout](../../../docs/behavior/report-expand-collapse.md#authored-report-layout).
