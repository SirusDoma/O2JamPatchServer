using Encore.Server;
using Microsoft.Extensions.Logging;

namespace O2JamPatchServer.Controllers.Filters;

public class ControllerLoggerFilter(ILogger<ControllerLoggerFilter> logger) : CommandFilter
{
    private IDisposable? _scope = null;

    public override void OnActionExecuting(CommandExecutingContext context)
    {
        _scope = logger.BeginScope("Controller");
    }

    public override void OnActionExecuted(CommandExecutedContext context)
    {
        _scope?.Dispose();
    }
}
