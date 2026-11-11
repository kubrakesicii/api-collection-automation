using Microsoft.AspNetCore.Mvc;
using SampleApi.Data;
using SampleApi.Dtos;
using SampleApi.Entities;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(InMemoryStore<Order> store) : ControllerBase
{
    [HttpPost]
    public ActionResult<OrderResponse> Create(CreateOrderRequest request)
    {
        var order = store.Add(new Order
        {
            CustomerId = request.CustomerId,
            ProductId = request.ProductId,
            Quantity = request.Quantity,
            CreatedAt = DateTime.UtcNow,
            Note = request.Note
        });
        return CreatedAtAction("Get", new { id = order.Id }, ToResponse(order));
    }
    
    private static OrderResponse ToResponse(Order o) => new(o.Id, o.CustomerId, o.ProductId, o.Quantity, o.CreatedAt);
}
