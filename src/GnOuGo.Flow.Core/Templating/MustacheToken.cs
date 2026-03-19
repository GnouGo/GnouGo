namespace GnOuGo.Flow.Core.Templating;

/// <summary>
/// Tokens produced by the Mustache parser.
/// </summary>
public abstract record MustacheToken;

/// <summary>Raw text segment.</summary>
public sealed record TextToken(string Text) : MustacheToken;

/// <summary>Variable interpolation {{name}} (HTML-escaped).</summary>
public sealed record VariableToken(string Name) : MustacheToken;

/// <summary>Unescaped variable {{{name}}}.</summary>
public sealed record RawVariableToken(string Name) : MustacheToken;

/// <summary>Section {{#name}} ... {{/name}}.</summary>
public sealed record SectionToken(string Name, List<MustacheToken> Children) : MustacheToken;

/// <summary>Inverted section {{^name}} ... {{/name}}.</summary>
public sealed record InvertedSectionToken(string Name, List<MustacheToken> Children) : MustacheToken;

