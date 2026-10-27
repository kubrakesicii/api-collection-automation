namespace ApiCollection.Analysis.Discovery;

/// <summary>
/// A missing, rejected or unresolvable example. Codes:
/// EX001 no request example; EX002 property without example; EX003 example rejected (conversion/contract);
/// EX004 response example not statically resolvable; EX005 metadata entry matches nothing; EX006 serializer options customized.
/// </summary>
public sealed record ExampleDiagnostic(string Code, string? Endpoint, string Target, string Message);
