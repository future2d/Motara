namespace Motara.ModelRuntime.Abstractions;

public enum ModelErrorCode
{
    InvalidDescriptor = 0,
    MissingReference = 1,
    PathEscape = 2,
    SizeLimitExceeded = 3,
    UnsupportedArchive = 4,
    ArchiveModelCount = 5,
    NameConflict = 6,
    IncompatibleMoc3 = 7,
    TextureDecodeFailed = 8,
    NativeLibraryUnavailable = 9,
    NativeCallFailed = 10,
    GpuResourceFailed = 11,
    DeviceRecoveryFailed = 12,
    IoFailure = 13,
}

public sealed record ModelError
{
    public ModelError(ModelErrorCode code, string? subject = null)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        if (subject is not null && string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("Error subject cannot be blank.", nameof(subject));
        }

        Code = code;
        Subject = subject;
    }

    public ModelErrorCode Code { get; }

    public string? Subject { get; }
}
