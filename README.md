# Motara

[English](README.md) | [简体中文](README.zh-CN.md)

Motara is an open-source desktop application for facial-tracking data
processing, virtual-avatar control, and scene composition.

> **Project status:** active development. This repository has no official
> binary release yet, and its APIs, file formats, network behavior, and
> supported-platform scope are not stable compatibility commitments.

## What Is Implemented In This Source Tree

- An Avalonia desktop shell with English and Simplified Chinese localization,
  local UI/window settings, structured local diagnostics, menus, and shortcut
  profiles.
- A scene workspace with local scene persistence, model assignment, image and
  video backgrounds, Spout2 and NDI signal attachments, effects, and
  screenshots.
- A local model library that imports Cubism model descriptor directories and
  ZIP, RAR, or 7Z archives with bounded validation. It also stores model
  parameter mappings and source-mapping profiles locally.
- MOC3 parsing and Skia rendering code through the separately maintained,
  MIT-licensed [PurismCore](https://github.com/MotaraSoft/PurismCore) native
  component. The renderer includes texture loading, masks, blend modes, GPU
  fallback, and screenshots.
- Tracking adapters for iFacialMocap UDP, local-camera MediaPipe, and a local
  OpenSeeFace process, together with mappings, formulas, calibration/filtering
  pipeline components, parameter priorities, and bounded frame processing.
- A Live2D Cubism Editor external-API output adapter with a configurable
  parameter mapping.
- Early collaboration components: installation identity storage, friend and
  session invitation formats, encrypted peer frames, local friend records, and
  model-package transfer primitives.

Some of those features require an optional native component or an external
application. They are present as source code but are unavailable from a clean
checkout until their dependencies are provided as described below.

## Not Implemented Or Not Ready To Ship

- VTube Studio output is not implemented.
- The public plugin SDK, a plugin host, and stable third-party extension
  contracts are not implemented.
- The repository contains no official redistributable bundle for PurismCore,
  FFmpeg, Spout2, NDI, MediaPipe, OpenSeeFace, camera support, or their model
  files. A clean checkout intentionally omits those binaries and data files.
- MediaPipe and OpenSeeFace input, Spout2/NDI signals, video decoding, and
  MOC3 rendering need their respective local runtime components. Their source
  paths handle missing components explicitly; availability is not promised by
  this repository alone.
- Windows DPAPI protects the current collaboration identity. Equivalent
  macOS Keychain and Linux Secret Service implementations are not provided.
- Collaboration networking, the presence service, native runtimes, platform
  support, security review, installation/update flow, and end-to-end
  interoperability have not reached release-ready status.

## Build And Run The Source

Install the .NET SDK selected by [global.json](global.json) (`10.0.302`), then:

```powershell
dotnet restore --locked-mode
dotnet build Motara.slnx --no-restore
dotnet run --project src/Motara.App/Motara.App.csproj --no-build
```

For an optional local Release verification, run:

```powershell
dotnet build Motara.slnx --configuration Release --no-restore
```

The public source repository intentionally omits test projects, build outputs,
packaging material, private models, and optional native/runtime artifacts.
The build is expected to succeed without them; runtime-dependent features will
remain unavailable until their dependencies are supplied locally.

## Runtime And Distribution Boundaries

`src/Motara.App/Motara.App.csproj` copies optional runtimes only when the
corresponding local paths exist. Those paths are not source-controlled and do
not grant redistribution rights. In particular:

- PurismCore is a separately maintained MIT component; its current source and
  compliance record are linked below.
- FFmpeg, Spout2, NDI, MediaPipe, OpenSeeFace, ESCAPI, and associated models
  are not part of this repository's source distribution.
- A future binary release must identify every included artifact's exact version
  and provenance, retain its license/notice files, satisfy its redistribution
  conditions, and update the third-party inventory before publication.

See [Third-Party Notices](THIRD-PARTY-NOTICES.md) for the tracked dependency
inventory and release requirements.

## Legal And Notices

- [License](LICENSE): Motara-authored source code is licensed under MPL-2.0,
  unless a file states otherwise.
- [Notice](NOTICE.md): scope of the project license and treatment of bundled
  notices and public legal material.
- [Third-Party Notices](THIRD-PARTY-NOTICES.md): dependencies, vendored icon
  geometry, optional runtimes, and distribution requirements.
- [Trademark Policy](TRADEMARKS.md): names and visual marks are not licensed
  by the source-code license.
- [PurismCore Compliance Record](docs/legal/PurismCore-Compliance.md) and
  [Guiding Case No. 48 record](docs/legal/Guiding-Case-48.md): preserved legal
  material; they are not legal advice and do not determine any dispute.

Motara is independent and is not affiliated with or endorsed by Apple, Live2D
Inc., DenchiSoft, or the developers of iFacialMocap. ARKit, Live2D, Cubism,
VTube Studio, iFacialMocap, NDI, FFmpeg, MediaPipe, OpenSeeFace, Spout, and
other third-party names or marks belong to their respective owners. Users are
responsible for having sufficient rights to their models, plugins, recordings,
and other assets.

## Related Repositories

- [Motara.PluginSdk](https://github.com/MotaraSoft/Motara.PluginSdk): reserved
  for future public extension contracts and plugin development tools.
- [motarasoft.github.io](https://github.com/MotaraSoft/motarasoft.github.io):
  source for the Motara website.

## Contributing

Bug reports and narrowly scoped proposals are welcome through GitHub Issues.
Before submitting implementation changes, discuss any public API, file-format,
protocol, licensing, or third-party-runtime impact because these areas are not
stable yet.

For general or private organization enquiries, contact
[hello@motara.org](mailto:hello@motara.org). Please report security issues as
described in the organization [security policy](https://github.com/MotaraSoft/.github/security/policy).
