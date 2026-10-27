using System.ComponentModel.DataAnnotations;

namespace SampleApi.Dtos;

public sealed record ProductResponse(int Id, string Name, decimal Price);

public sealed record CreateProductRequest(
    [property: Required, StringLength(100)] string Name,
    [property: Range(0, 1_000_000)] decimal Price);

public sealed record UpdateProductRequest(
    [property: Required, StringLength(100)] string Name,
    [property: Range(0, 1_000_000)] decimal Price);
