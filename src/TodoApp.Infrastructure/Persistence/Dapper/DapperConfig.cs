using Dapper;

namespace TodoApp.Infrastructure.Persistence.Dapper;

/// <summary>
/// One-time registration of Dapper's global type handlers. Idempotent and thread-safe so it
/// can be called from both app startup and the test harness without double-registering.
/// </summary>
public static class DapperConfig
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            // DateTimeOffset and Guid are in Dapper's built-in type map, so LookupDbType would
            // short-circuit to the default provider handling on the parameter (write) path and
            // never consult a custom handler. Removing them from the map lets the handlers below
            // own both reads and writes (ticks for DateTimeOffset, text for Guid).
            SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
            SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
            SqlMapper.RemoveTypeMap(typeof(Guid));
            SqlMapper.RemoveTypeMap(typeof(Guid?));

            SqlMapper.AddTypeHandler(new DateTimeOffsetTicksHandler());
            SqlMapper.AddTypeHandler(new GuidTypeHandler());
            _registered = true;
        }
    }
}
