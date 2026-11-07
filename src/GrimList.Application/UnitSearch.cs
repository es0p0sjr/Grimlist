using GrimList.Core;

namespace GrimList.Application;

public static class UnitSearch
{
    // Shared by the session's library search and Android's cached picker rows.
    public static bool Matches(UnitDefinition unit, string query)
    {
        query = query.Trim();
        return query.Length == 0 || unit.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || unit.Profile?.Keywords?.Any(keyword => keyword.Contains(query, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
