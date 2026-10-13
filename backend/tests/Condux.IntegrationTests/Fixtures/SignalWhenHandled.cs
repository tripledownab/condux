using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Completes once the server has finished handling a request whose path ends with the suffix, which a
/// client that hung up cannot otherwise observe.
/// </summary>
public sealed class SignalWhenHandled(TaskCompletionSource handled, string pathSuffix) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, inner) =>
        {
            try
            {
                await inner(context);
            }
            finally
            {
                if (context.Request.Path.Value?.EndsWith(pathSuffix, StringComparison.Ordinal) == true)
                {
                    handled.TrySetResult();
                }
            }
        });
        next(app);
    };
}
