namespace GrimList.Core;

public enum ImportStatus { Available, AvailableWithWarnings, Unavailable, SourceDataError }
public record ImportDiagnostic(string Kind, string Catalogue, string Unit, string ReferenceId, string RuleId,
    string Purpose, string Message, string Revision = "", string ContentHash = "");

public static class ImportDiagnostics
{
    internal static InvalidDataException BrokenReference(string catalogue, string unit, string id, string rule, string purpose)
    {
        var message = $"Missing {purpose} '{id}' in catalogue '{catalogue}', unit '{unit}', rule '{rule}'. No matching entry or link exists in the imported catalogue graph.";
        var exception = new InvalidDataException(message);
        exception.Data[nameof(ImportDiagnostic)] = new ImportDiagnostic("SourceDataError", catalogue, unit, id, rule, purpose, message);
        return exception;
    }
    internal static ImportDiagnostic? From(Exception exception, SourceInfo source) => exception.Data[nameof(ImportDiagnostic)] is ImportDiagnostic diagnostic
        ? diagnostic with { Revision = source.Version, ContentHash = source.ContentHash } : null;
}
