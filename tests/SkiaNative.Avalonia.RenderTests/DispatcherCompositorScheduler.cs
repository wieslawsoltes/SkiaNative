using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace Avalonia.Skia.RenderTests;

public sealed class DispatcherCompositorScheduler : ICompositorScheduler
{
    public void CommitRequested(Compositor compositor)
    {
        Dispatcher.UIThread.Post(() => compositor.Commit(), DispatcherPriority.UiThreadRender);
    }
}
