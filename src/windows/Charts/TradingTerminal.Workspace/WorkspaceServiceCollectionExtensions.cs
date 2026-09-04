using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Workspace.Canvases;

namespace TradingTerminal.Workspace;

/// <summary>
/// DI for the workspace shell and its canvases.
///
/// <para>The canvas list is built by REGISTRATION, the same way strategy plugins are, and that is what
/// keeps the base shell free of references it should not have. A Professional-only surface registers
/// itself from the Pro composition root; the shell resolves <c>IEnumerable&lt;WorkspaceCanvas&gt;</c>
/// and has never heard of it.</para>
/// </summary>
public static class WorkspaceServiceCollectionExtensions
{
    /// <summary>The shell, its view-model, and the canvases every edition has.</summary>
    public static IServiceCollection AddWorkspace(this IServiceCollection services)
    {
        services.AddTransient<WorkspaceViewModel>();
        services.AddTransient<WorkspaceShell>();

        // Registration order is the picker's order, so the price chart leads by being first.
        services.AddWorkspaceCanvas(PriceChartCanvas.Descriptor);
        return services;
    }

    /// <summary>Adds one canvas to the picker. Call it from whichever composition root owns the
    /// surface — that is how a canvas joins without the shell referencing its project.</summary>
    public static IServiceCollection AddWorkspaceCanvas(this IServiceCollection services, WorkspaceCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        services.AddSingleton(canvas);
        return services;
    }
}
