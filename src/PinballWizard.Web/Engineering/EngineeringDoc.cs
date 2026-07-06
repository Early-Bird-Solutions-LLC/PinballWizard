using Markdig.Syntax;

namespace PinballWizard.Web.Engineering;

/// <summary>Engineering documentation page loaded from embedded resources.</summary>
public sealed record EngineeringDoc(string Slug, string Title, string Group, int Order, MarkdownDocument Ast, string SourceGitHubUrl);

/// <summary>Architecture Decision Record loaded from embedded ADR markdown.</summary>
public sealed record AdrEntry(int Number, string Title, string Status, string Date, string Slug, MarkdownDocument Ast);
