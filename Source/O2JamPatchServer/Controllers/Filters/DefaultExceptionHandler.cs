using Encore.Server;

namespace O2JamPatchServer.Controllers.Filters;

public class DefaultExceptionHandler : CommandExceptionHandler
{
    public override void Handle(CommandExceptionHandlerContext context)
    {
        // Suppress exception, prevent leaking outside command dispatcher
        context.Handled = true;
    }
}
