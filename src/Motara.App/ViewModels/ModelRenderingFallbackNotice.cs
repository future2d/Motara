using Motara.ModelRuntime.Abstractions;

namespace Motara.App.ViewModels;

internal sealed record ModelRenderingFallbackNotice(ModelRenderingBackendFaultReason Reason);
