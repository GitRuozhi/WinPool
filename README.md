# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a Windows desktop application for viewing storage topology, editing simulated systems, monitoring devices, and performing supported local storage operations through a controlled workflow.

**The source version is V0.62, currently undergoing developer-machine acceptance under the approved [active plan](docs/Plan.md). No public release has been made.** See [Quality](docs/Quality.md) for actual results, candidate identity and outstanding external verification. The [V0.61 stage record](docs/Archive/20261018-v061-completed/Plan.md) is preserved as history; [Directory.Build.props](Directory.Build.props) is the version source.

## Using WinPool

- Manage shows pools, tiers, physical and virtual disks, partitions, and their relationships. Developer mode in Settings exposes the hardware report, testing roadmap, and current-run messages.
- Storage structure previews a draft before the Agent executes the confirmed structural job. Disk/partition submits individual actions. New pools generate virtual disk and partition goals by default; existing RAW virtual disks are not automatically partitioned. Drafts survive page navigation during the current App lifetime, and simulation documents load on demand.
- Valid simulation Apply/Enter actions show a local candidate and save merged edits in the background. Save failures retain the local work and report its state. JSON import/export and simulation do not modify real disks; complete canonical document envelopes support up to 64 MiB. Simulation results do not prove that Windows can execute the same configuration.
- Real editing starts off and uses exact targets and the current controlled confirmation. An accepted job may continue after the App closes while the Agent remains alive; an Agent restart performs read-only recovery without continuing writes. Unknown outcomes block further writes. Capabilities depend on Windows/provider support. Real MBR-to-GPT conversion, extending existing VDs/tiers to MAX, and direct pool-member migration remain unsupported; see [Product](docs/Product.md).
- Monitoring supports 0.2–20 Hz and pushes batches to a shared chart/alert cache, reseeding its window after connection or sequence gaps. Recording uses a separate database, rotates at 1 GiB, and archives with bundled 7-Zip. Historical archives are not automatically deleted. CSV export covers the active database only; there is no monitoring-history browser.
- English, Simplified Chinese, themes, and keyboard controls are available. Hardware fields retain their original values; review their contents before sharing exports.

Implementation does not establish coverage across every environment or performance scenario. Quality records actual ordinary-privilege startup, hardware-operation, and candidate results; historical reviews and speed measurements do not replace current verification.

## Running and building

The support target starts at Windows 10 22H2 x64, with Windows 11 24H2/25H2 x64 as primary platforms. Coverage across environments remains external testing work; these targets are not a completed verification matrix.

Use the complete x64 portable runtime directory with matching App and Agent files. Run `WinPool.App.exe`; the Agent appears in the user tray. Exit both before replacing files. Data defaults to `%LocalAppData%\WinPool`, with an explicitly selected writable `Data` directory beside the program also supported. No formal MSIX or Store release is available.

The runtime uses the system Windows PowerShell 5.1; PowerShell 7 and development SDKs are unnecessary for running the portable package. Project compatibility testing is separate from upstream .NET support; Windows 10 Pro 22H2 is not listed in the .NET 10 support matrix.

Candidates provide a ZIP containing `TESTING.md` and a separate SHA-256 checksum file. Extract the complete folder before running. Feedback should include the version, Windows/device environment, reproduction steps, and relevant errors. Candidate preparation and internal acceptance do not constitute a public release.

On Windows with PowerShell and the SDK specified by `global.json`:

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
.\artifacts\Release\WinPool.App.exe
```

Contributor rules are in [AGENTS](AGENTS.md); build and data ownership are in [Development](docs/Development.md). Internal development documents are maintained in Chinese.

## Research background

```text
64K interleave + 64K NTFS cluster = current tested recommendation.
Windows 11 has not yet received equivalent testing because current storage hardware prices and the author's practical budget do not allow a second full test platform.
```

The research findings cover their recorded samples. Software platform testing and storage reliability research have different scopes.

## Rights

No license is granted for WinPool's own code by this repository. All rights are reserved. Third-party components retain their respective licenses; see the [runtime license index](assets/ThirdParty/RuntimeLicenses/README.md) and the bundled 7-Zip [license](assets/ThirdParty/7zip/26.04/License.txt) and [source notice](assets/ThirdParty/7zip/26.04/NOTICE.txt).
