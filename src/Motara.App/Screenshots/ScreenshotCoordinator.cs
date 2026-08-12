using System.ComponentModel;
using Motara.App.ViewModels;

namespace Motara.App.Screenshots;

internal sealed class ScreenshotCoordinator : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan PreviewDuration = TimeSpan.FromSeconds(3);
    private readonly IScreenshotService service;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();
    private CancellationTokenSource? previewCancellation;
    private TaskCompletionSource previewShown = NewCompletionSource();
    private int? countdownRemaining;
    private bool isCanvasLocked;
    private byte[]? previewPng;
    private string? savedFilePath;
    private bool captureActive;
    private bool disposed;

    public ScreenshotCoordinator(IScreenshotService service, TimeProvider timeProvider)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int? CountdownRemaining
    {
        get => countdownRemaining;
        private set => Set(ref countdownRemaining, value, nameof(CountdownRemaining));
    }

    public bool IsCanvasLocked
    {
        get => isCanvasLocked;
        private set => Set(ref isCanvasLocked, value, nameof(IsCanvasLocked));
    }

    public byte[]? PreviewPng
    {
        get => previewPng;
        private set
        {
            if (ReferenceEquals(previewPng, value))
            {
                return;
            }

            previewPng = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewPng)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPreviewVisible)));
        }
    }

    public string? SavedFilePath
    {
        get => savedFilePath;
        private set => Set(ref savedFilePath, value, nameof(SavedFilePath));
    }

    public bool IsPreviewVisible => PreviewPng is not null;

    public async Task CaptureAsync(
        ScreenshotCaptureRequest request,
        ScreenshotRenderRequest renderRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(renderRequest);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (captureActive)
            {
                throw new InvalidOperationException("A screenshot capture is already active.");
            }

            captureActive = true;
            previewShown = NewCompletionSource();
        }

        try
        {
            for (int remaining = request.Settings.CountdownSeconds; remaining > 0; remaining--)
            {
                CountdownRemaining = remaining;
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
            }

            CountdownRemaining = null;
            IsCanvasLocked = true;
            ScreenshotResult result;
            try
            {
                result = await service.CaptureAsync(
                    renderRequest,
                    request.Settings.SaveDirectory,
                    cancellationToken);
            }
            finally
            {
                IsCanvasLocked = false;
            }

            PreviewPng = result.PreviewPng;
            SavedFilePath = result.FilePath;
            previewShown.TrySetResult();
            using var preview = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (gate)
            {
                previewCancellation = preview;
            }

            try
            {
                await Task.Delay(PreviewDuration, timeProvider, preview.Token);
            }
            catch (OperationCanceledException) when (
                preview.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
            }
        }
        finally
        {
            lock (gate)
            {
                previewCancellation = null;
                captureActive = false;
            }

            CountdownRemaining = null;
            PreviewPng = null;
            IsCanvasLocked = false;
        }
    }

    public Task WaitForPreviewAsync()
    {
        lock (gate)
        {
            return previewShown.Task;
        }
    }

    public void DismissPreview()
    {
        lock (gate)
        {
            previewCancellation?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            previewCancellation?.Cancel();
        }
    }

    private static TaskCompletionSource NewCompletionSource() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
