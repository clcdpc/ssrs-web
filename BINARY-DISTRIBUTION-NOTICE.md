# Binary distribution notice

Project-authored code is licensed under **GNU AGPL-3.0-only**. Third-party components retain their own licenses and notices. The accompanying `THIRD-PARTY-NOTICES.md` is generated from the build's `SsrsPortal.deps.json` and identifies its package versions, copyright metadata, licenses, and included license texts.

## Microsoft SQL Server native SNI component

The Windows x64 application publish includes the unmodified `Microsoft.Data.SqlClient.SNI.dll` from [`Microsoft.Data.SqlClient.SNI.runtime` 7.1.0](https://www.nuget.org/packages/Microsoft.Data.SqlClient.SNI.runtime/7.1.0). This separate native component uses [Microsoft Software License Terms](THIRD-PARTY-LICENSES/Microsoft.Data.SqlClient.SNI.runtime-7.1.0-LICENSE.txt), not the MIT license for [`Microsoft.Data.SqlClient` 7.1.1](https://www.nuget.org/packages/Microsoft.Data.SqlClient/7.1.1). See also the [upstream SNI notice](https://github.com/dotnet/SqlClient/blob/v7.1.1/NOTICE.txt).

Microsoft permits distribution of SNI object code as part of an application. Its distribution requirements include:

- Distribute SNI with the application, not as a standalone component.
- Require distributors and external end users to agree to terms that protect SNI at least as much as the Microsoft terms.
- Indemnify, defend, and hold Microsoft harmless for claims related to distribution or use of the application, except to the extent a claim is based solely on unmodified SNI.

Retain the applicable license texts and notices when redistributing the application. The complete Microsoft terms govern SNI; the project's AGPL license does not replace them.
