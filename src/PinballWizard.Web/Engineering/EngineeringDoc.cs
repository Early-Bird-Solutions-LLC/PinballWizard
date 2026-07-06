using Markdig.Syntax;

namespace PinballWizard.Web.Engineering;

public sealed record EngineeringDoc(string Slug, string Title, string Group, int Order, MarkdownDocument Ast, string SourceGitHubUrl);

public sealed record AdrEntry(int Number, string Title, string Status, string Date, string Slug, MarkdownDocument Ast);
