using Condux.Core.DebugFiles;
using Condux.Core.Events;
using Condux.Storage.Postgres;

namespace Condux.Storage.DebugFiles;

/// <summary>
/// The read-time pass that puts a Flutter app's own class names back in an event's exception types and
/// messages, from the obfuscation map CI uploaded for the build the event names (<see cref="DartObfuscationMaps"/>).
/// One query per event that carries both an ELF or Mach-O image with a debug id and a name to look up, and
/// none for any other.
/// </summary>
public sealed class DartTypeNames(DartSymbolMapRepository maps)
{
    public async Task<Event> SymbolicateAsync(long projectId, Event e, CancellationToken cancellationToken)
    {
        var debugIds = DartObfuscationMaps.ToDebugIds(e);
        var names = DartObfuscationMaps.ToNames(e);
        if (debugIds.Count == 0 || names.Count == 0)
        {
            return e;
        }
        var restored = await maps.LookUpAsync(projectId, debugIds, names, cancellationToken);
        return DartObfuscationMaps.Restore(e, restored);
    }
}
