using BGPLite.Api;

namespace BGPLite.Tests;

/// <summary>
/// Unit tests for <see cref="ManagementApi.ResolveCorsOrigin"/> (#99): the request Origin is echoed
/// back only when it is non-empty and exactly allowlisted (case-insensitive); absent/empty Origin
/// and an empty/absent allowlist both yield <c>null</c>, which AddCorsHeaders maps to "no CORS
/// headers emitted" (CORS disabled — the secure default that replaces the previous blanket "*").
/// </summary>
public class CorsOriginResolverTests
{
    private static readonly string[] Allowed =
    [
        "https://operator.example.com",
        "https://bgp.example.net"
    ];

    [Fact]
    public void Allowlisted_Origin_Is_Returned() =>
        Assert.Equal("https://operator.example.com",
            ManagementApi.ResolveCorsOrigin("https://operator.example.com", Allowed));

    [Fact]
    public void NonAllowlisted_Origin_Returns_Null() =>
        Assert.Null(ManagementApi.ResolveCorsOrigin("https://evil.example.org", Allowed));

    [Fact]
    public void Empty_Allowlist_Returns_Null() =>
        // No origins configured => CORS disabled regardless of the request origin.
        Assert.Null(ManagementApi.ResolveCorsOrigin("https://operator.example.com", Array.Empty<string>()));

    [Fact]
    public void Null_Allowlist_Returns_Null() =>
        // Default config value (CorsAllowedOrigins unset) => CORS disabled.
        Assert.Null(ManagementApi.ResolveCorsOrigin("https://operator.example.com", null));

    [Fact]
    public void Null_Origin_Returns_Null() =>
        // No Origin header on the request (same-origin / non-browser client) => nothing to reflect.
        Assert.Null(ManagementApi.ResolveCorsOrigin(null, Allowed));

    [Fact]
    public void Empty_Origin_Returns_Null() =>
        Assert.Null(ManagementApi.ResolveCorsOrigin("", Allowed));

    [Fact]
    public void Match_Is_Case_Insensitive() =>
        // Browsers send a canonical lowercase Origin; the operator's entry may differ in casing.
        Assert.Equal("HTTPS://Operator.Example.COM",
            ManagementApi.ResolveCorsOrigin("HTTPS://Operator.Example.COM", Allowed));

    [Fact]
    public void Allowlisted_Second_Entry_Is_Returned() =>
        Assert.Equal("https://bgp.example.net",
            ManagementApi.ResolveCorsOrigin("https://bgp.example.net", Allowed));

    [Fact]
    public void Origin_With_Trailing_Slash_Not_Matched() =>
        // Scheme://host[:port] only — a path-prefixed spoof is not a match.
        Assert.Null(ManagementApi.ResolveCorsOrigin("https://operator.example.com/", Allowed));

    [Fact]
    public void Partial_Origin_Not_Matched() =>
        // A substring/embedded host must not satisfy the allowlist.
        Assert.Null(ManagementApi.ResolveCorsOrigin("https://notoperator.example.com", Allowed));
}
