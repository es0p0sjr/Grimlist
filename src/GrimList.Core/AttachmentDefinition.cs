namespace GrimList.Core;

// Compiled attachment permissions only; these records do not own reference cards or
// enhancement/upgrade eligibility. Keep each kind of permission separate:
// BodyguardTargets names recipients; AdditionalAttachments permits partners;
// BodyguardRules sets recipient-side limits; Retinues requires a qualifying partner.
// A future "can lead X, can lead this" rule belongs to recipient eligibility and
// must not implicitly copy X's partner permissions, slot limits or other abilities.
// An exception belongs to its granting character, never to the bodyguard's global slots.
// Empty BodyguardTargets means the granting unit's ordinary target list still applies.
public record AdditionalAttachmentRule(List<string> BodyguardTargets, bool AnyLeader, List<string> PartnerTargets,
    bool ForbidDuplicateLeaders, string SourceText);
public record BodyguardAttachmentRule(int LeaderLimit, int? RequiredModels, bool ForbidDuplicateLeaders,
    string LimitedKeyword, int? KeywordLimit, string SourceText);
public record RetinueAttachmentRule(string LeaderKeywords, string UniqueKeyword);
public record InheritedLeaderRule(string BodyguardTarget, string LeaderKeywords, string ExcludedKeywords,
    bool RequiresLeader, string SourceText);
public record AttachmentDefinition(int Version, string SourceFingerprint, bool CanAttach, bool IsSupport,
    List<string> BodyguardTargets, bool AllowsLeaderAndSupport, List<AdditionalAttachmentRule> AdditionalAttachments,
    List<BodyguardAttachmentRule> BodyguardRules, AssociationDefinition? Associations = null,
    List<RetinueAttachmentRule>? Retinues = null, List<InheritedLeaderRule>? InheritedLeaders = null);
