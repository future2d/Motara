namespace Motara.ModelLibrary;

public interface IAppDataPaths
{
    string DataRoot { get; }

    string ModelsRoot { get; }

    string ModelImportStagingRoot { get; }

    string SourceMappingsRoot { get; }

    string InputBindingsPath { get; }

    string ParameterPriorityPath { get; }

    string CollaborationRoot { get; }
}

public sealed class AppDataPaths : IAppDataPaths
{
    public AppDataPaths()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    public AppDataPaths(string localApplicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataRoot);
        string normalizedRoot = Path.GetFullPath(localApplicationDataRoot);
        DataRoot = Path.Combine(normalizedRoot, "Motara", "Data");
        ModelsRoot = Path.Combine(DataRoot, "Live2DModels");
        ModelImportStagingRoot = Path.Combine(DataRoot, "ModelImportStaging");
        SourceMappingsRoot = Path.Combine(DataRoot, "Mappings");
        InputBindingsPath = Path.Combine(
            DataRoot,
            "InputBindings",
            "bindings.motara.json");
        ParameterPriorityPath = Path.Combine(
            DataRoot,
            "Settings",
            "parameter-priority.motara.json");
        CollaborationRoot = Path.Combine(DataRoot, "Collaboration");
    }

    public string DataRoot { get; }

    public string ModelsRoot { get; }

    public string ModelImportStagingRoot { get; }

    public string SourceMappingsRoot { get; }

    public string InputBindingsPath { get; }

    public string ParameterPriorityPath { get; }

    public string CollaborationRoot { get; }
}
