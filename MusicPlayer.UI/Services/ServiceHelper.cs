using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;

namespace MusicPlayer.UI.Services;

/// <summary>
/// Resolves services for pages created by Shell's DataTemplate factory. Shell does not run
/// page constructors through dependency injection, so pages take a parameterless constructor
/// and resolve their view model here.
/// </summary>
public static class ServiceHelper
{
    public static T GetRequiredService<T>() where T : notnull
    {
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException(
                "MAUI services are not available yet. ServiceHelper must be used after the app is built.");

        return services.GetRequiredService<T>();
    }

    public static T? GetService<T>() where T : class
        => IPlatformApplication.Current?.Services.GetService<T>();
}
