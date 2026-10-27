using System.ComponentModel.DataAnnotations;

namespace SampleApi.Dtos;

public sealed record OrderResponse(int Id, int CustomerId, int ProductId, int Quantity, DateTime CreatedAt);

public sealed record CreateOrderRequest(
    [property: Range(1, int.MaxValue)] int CustomerId,
    [property: Range(1, int.MaxValue)] int ProductId,
    [property: Range(1, 1000)] int Quantity);

public sealed record UpdateOrderRequest(
    [property: Range(1, 1000)] int Quantity);
