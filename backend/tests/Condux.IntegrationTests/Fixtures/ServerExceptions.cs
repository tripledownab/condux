using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Hands a test host's unhandled exception to the test that made the request, rather than a 500.
/// WebApplicationFactory runs a host in Development, where the developer exception page turns the
/// exception into a 500, and a test reading through GetFromJsonAsync or asserting a status then reports
/// only the number. Under load that was all a failing signup ever said. A filter that refuses to render
/// makes the page rethrow, and the test server passes the exception, stack and all, to the caller.
/// That holds before the response starts; once it has, the page rethrows without asking the filter, and
/// the test meets the exception inside an IOException when it reads the body, as it always did. The page
/// logs each exception a second time, as a failure to display the error page: that line is this filter.
/// For a host the test calls; a host that stands in for a dependency of the code under test (the
/// symbolizer) keeps its 500, which is what that code meets in production.
/// </summary>
public static class ServerExceptions
{
    /// <summary>Makes the host's unhandled exceptions reach the test instead of a 500.</summary>
    public static IWebHostBuilder SurfaceServerExceptions(this IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IDeveloperPageExceptionFilter, RethrowToTheTest>());

    // Fails rendering, which the page answers by rethrowing the original exception. A BadHttpRequestException
    // is the caller's error, not the server's: Development makes minimal APIs throw one for a body or query
    // that does not bind, where production answers 400, so it is left to the page, which answers 400 too.
    private sealed class RethrowToTheTest : IDeveloperPageExceptionFilter
    {
        public Task HandleExceptionAsync(ErrorContext errorContext, Func<ErrorContext, Task> next) =>
            errorContext.Exception is BadHttpRequestException
                ? next(errorContext)
                : Task.FromException(errorContext.Exception);
    }
}
