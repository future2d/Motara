namespace Motara.App.Models;

public interface IModelImportSourcePicker
{
    Task<string?> PickDescriptorAsync(CancellationToken cancellationToken);
}

internal sealed class ModelImportSourcePicker(
    Func<CancellationToken, Task<string?>> pickDescriptor) : IModelImportSourcePicker
{
    public Task<string?> PickDescriptorAsync(CancellationToken cancellationToken) =>
        pickDescriptor(cancellationToken);
}
