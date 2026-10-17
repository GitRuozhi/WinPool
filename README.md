# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a Windows desktop application for viewing storage topology, editing simulated systems, monitoring devices, and performing supported local storage operations through a controlled workflow.

The current code version is **V0.60**. **V0.61** simplification and performance work is being planned; the documentation targets that version, but implementation has not started. The code version will advance when that work is complete. The first public test build is due before V0.70 for dedicated testers in different environments. Work and evidence currently come from the developer environment. See the [active plan](docs/Plan.md) and [verification scope](docs/Quality.md).

## Using WinPool

- Manage shows pools, tiers, physical and virtual disks, partitions, and their relationships. Developer mode in Settings exposes the hardware report, testing roadmap, and current-run messages.
- Storage structure previews a draft before applying its changes together. Disk/partition submits individual actions. New pools generate a virtual disk and partition goals by default; existing RAW virtual disks are not automatically partitioned. Drafts survive page navigation during the current App lifetime.
- Simulation and JSON import/export do not modify real disks. Simulation results do not prove that Windows can execute the same configuration.
- Real editing starts off and uses exact targets and the current controlled confirmation. Available operations depend on Windows/provider capabilities. Unknown outcomes block further writes and are not automatically replayed. Real MBR-to-GPT conversion, extending existing VDs/tiers to MAX, and direct pool-member migration remain unsupported; see [Product](docs/Product.md) for the complete boundaries.
- Monitoring records to a separate database, rotates at 1 GiB, and archives with the bundled 7-Zip. Historical archives are not automatically deleted. CSV export covers the active database only; there is no monitoring-history browser.
- English, Simplified Chinese, themes, and keyboard controls are available. Hardware fields retain their original values; review their contents before sharing exports.

Known slow or failed collection at ordinary privilege, connection timeouts, and repeated processing await V0.61 fixes. Diagnostic speed improvements do not describe an optimization already deployed in the current product. Sources are listed in the [review index](docs/Review/README.md).

## Running and building

The support target starts at Windows 10 22H2 x64, with Windows 11 24H2/25H2 x64 as primary platforms. Coverage across environments remains external testing work; these targets are not a completed verification matrix.

Use the complete x64 portable runtime directory with matching App and Agent files. Run `WinPool.App.exe`; the Agent appears in the user tray. Exit both before replacing files. Data defaults to `%LocalAppData%\WinPool`, with an explicitly selected writable `Data` directory beside the program also supported. No formal MSIX or Store release is available.

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

No license is granted for WinPool's own code by this repository. All rights are reserved. Third-party components retain their respective licenses; the bundled 7-Zip component's [license](assets/ThirdParty/7zip/26.03/License.txt) and [source notice](assets/ThirdParty/7zip/26.03/NOTICE.txt) are provided separately.
