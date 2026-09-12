# Third-party dependencies

The project source is licensed under the repository's MIT License. Dependencies remain under their own licenses; the MIT License does not replace or relicense them.

| Component | Source of license terms |
| --- | --- |
| Microsoft.Debugging.Platform.DbgX | NuGet package `dbgx_license.txt`; Microsoft terms. DbgX binaries are not redistributed by this build. |
| Fluent.Ribbon | MIT; compile-time reference to WinDbg-provided Fluent controls. Fluent and ControlzEx binaries are not redistributed by this build. |
| Microsoft.Web.WebView2 | Microsoft WebView2 NuGet license and Evergreen Runtime terms |
| GitHub.Copilot.SDK and redistributed Microsoft.Extensions assemblies | MIT |
| GitHub Copilot CLI | Platform npm package `LICENSE.md`; separate product/subscription terms |
| Bundled web production dependencies | MIT, ISC, BSD-3-Clause, Apache-2.0, OFL-1.1, Unlicense, or an available alternative license as recorded in `web/package-lock.json` |
| Source Sans 3 and JetBrains Mono | Font package license files (SIL Open Font License) |

The binary build includes the application `LICENSE` and a `third-party-licenses` directory. Web dependency notices are generated from the exact production dependency graph in `web/package-lock.json`; a missing license text or unreviewed license expression fails the build. The CLI license permits redistribution of unmodified copies as part of an application with material independent functionality, requires its license and notices to remain, and explicitly permits the application to use an independent open-source license. WebView2 permits binary redistribution when its copyright, conditions, disclaimer and notices accompany the distribution. Font files remain under OFL-1.1. DOMPurify is distributed under its Apache-2.0 option. These terms do not prohibit licensing this project's original source under MIT.

Dependencies remain subject to their respective license terms. GitHub service use is also subject to the GitHub Terms of Service and GitHub Copilot terms.