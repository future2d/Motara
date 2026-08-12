# Third-Party Notices

[English](THIRD-PARTY-NOTICES.md) | [简体中文](THIRD-PARTY-NOTICES.zh-CN.md)

> Last reviewed: 2026-08-13

This inventory distinguishes the public source tree from a future binary
release. The repository contains source, project metadata, selected license
texts, and vendored Lucide icon geometry. It does **not** contain build output,
NuGet package payloads, optional native binaries, model files, private test
material, or a release bundle.

A reference to a dependency or external runtime does not grant a right to copy
or redistribute it. A distributor must review the exact artifact, version,
license, notices, attribution, and redistribution conditions before including
it in a binary release.

## Components Present In This Repository

### Motara Source

Unless a file states otherwise, Motara-authored source under `src/` and
`native/` is available under [MPL-2.0](LICENSE). See [NOTICE.md](NOTICE.md) for
the scope of that grant and the treatment of documentation and legal material.
The MPL-2.0 does not grant rights in product or organization marks; see the
[trademark policy](TRADEMARKS.md).

### Lucide 0.468.0

- **Project:** [Lucide](https://lucide.dev/)
- **Source used:** `lucide-static` 0.468.0
- **License:** ISC, reproduced at `LICENSES/LUCIDE-ISC.txt`.
- **Repository scope:** generated vector geometry used by the desktop shell's
  navigation, menu, and control icons. No icon package is resolved at runtime.

### Third-Party License Texts

The `LICENSES/` directory also retains verbatim license texts for components
whose source or dependency relationship is recorded here: AvaloniaEdit,
Bouncy Castle, PurismCore, and SharpCompress. CC BY 4.0 and CC0 1.0 are included
for files that state those licenses. Keeping a license text here does not mean
that the associated binary is included in the public source tree.

## Managed Dependencies Restored By Developers

The following production dependencies are centrally pinned in
`Directory.Packages.props`; exact direct and transitive resolution is recorded
in the production projects' `packages.lock.json` files. They are restored from
NuGet by a developer or binary builder, rather than being checked into this
repository.

| Component | Version(s) | License | Purpose |
| --- | --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Skia, Avalonia.Themes.Fluent | 12.1.0 | MIT | Cross-platform desktop host, UI framework, theme, and Skia integration. |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | Formula-editor text editing behavior. |
| SkiaSharp | 3.119.4 | MIT | Texture decoding and model rendering. |
| Microsoft.Extensions.Logging, Microsoft.Extensions.Logging.Abstractions | 10.0.10 | MIT | Logging abstractions and application logging composition. |
| Serilog.Extensions.Logging, Serilog.Sinks.File, Serilog | 10.0.0, 7.0.0, 4.2.0 | Apache-2.0 | Structured, rolling local diagnostic logs. |
| SharpCompress | 0.50.1 | MIT | Local RAR and 7Z model-archive import. |
| BouncyCastle.Cryptography | 2.6.2 | MIT | Ed25519 signing and verification for collaboration. |
| System.Security.Cryptography.ProtectedData | 10.0.10 | MIT | Windows DPAPI protection for the local collaboration identity. |

The package license expressions above are taken from the resolved NuGet package
metadata. A binary release must retain every applicable direct and transitive
package notice and license, including native assets resolved by Avalonia,
SkiaSharp, and their dependencies. The current source checkout intentionally
does not claim that these restored package payloads are part of a GitHub source
distribution.

## Conditional Native And Runtime Dependencies

`src/Motara.App/Motara.App.csproj` includes optional content only when a local
path exists. None of the following binaries, model files, SDKs, or runtime
payloads is tracked in this repository or included by a clean checkout.

| Component | Source-tree relationship | Public source distribution | Release requirement |
| --- | --- | --- | --- |
| [PurismCore](https://github.com/MotaraSoft/PurismCore) | Separately maintained MOC3 parser loaded by Motara's runtime adapter. Current reviewed source: `97dd2a5cf4a0c37947319b77671c578bfa99ad96`, default v6 ABI. | No PurismCore source or native binary is copied here. | Record the exact artifact/version/provenance and retain its MIT notice. See [compliance record](docs/legal/PurismCore-Compliance.md). |
| FFmpeg shared runtime | Optional video decode/probe executable and DLLs. | Not included. | Confirm the exact build and configuration, preserve its license and notices, and comply with the selected LGPL/GPL and patent obligations before distribution. |
| Spout2 SDK/runtime | Optional Windows bridge source expects a separately supplied Spout2 SDK and can link its static libraries. | No SDK, static library, bridge binary, or license file is included. | Identify the exact SDK/source revision and license; satisfy its notice and redistribution terms for both the bridge and any linked code. |
| NDI Runtime / SDK | Optional Windows bridge dynamically loads a user-installed NDI runtime. | No NDI runtime, SDK, header, or bridge binary is included. | Follow the NDI SDK/runtime license and redistribution terms. Do not imply that the Motara source license covers NDI. |
| MediaPipe Face Landmarker | Optional local-camera bridge and `.task` model. | No bridge binary, MediaPipe runtime, model, or supporting assets are included. | Record the exact MediaPipe build, model provenance, licenses, notices, and redistribution terms. |
| OpenSeeFace and ESCAPI | Optional local OpenSeeFace process and camera library. | No executable, model, camera library, or license file is included. | Record the exact release/provenance and preserve all applicable licenses and notices. |

The native bridge source in `native/` is Motara-authored unless a file states
otherwise. It does not incorporate an external runtime merely by loading it or
by naming its ABI. The source nevertheless does not resolve the distributor's
separate obligations for any binary it links, bundles, or invokes.

## Excluded Materials

User models, model archives, recordings, screenshots, logs, private local test
material, Live2D sample models, build outputs, and packaging artifacts are not
part of this repository's source distribution and are not covered by the
Motara source license unless their own file states otherwise.
