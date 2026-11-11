using Microsoft.AspNetCore.Mvc;
using SampleApi.Data;
using SampleApi.Dtos;
using SampleApi.Entities;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(InMemoryStore<Order> store) : ControllerBase
{
    [HttpGet]
    public ActionResult<IEnumerable<OrderResponse>> GetAll() =>
        Ok(store.GetAll().Select(ToResponse));

    [HttpGet("{id:int}")]
    public ActionResult<OrderResponse> GetById(int id) =>
        store.Get(id) is { } order ? ToResponse(order) : NotFound();

    [HttpPost]
    public ActionResult<OrderResponse> Create(CreateOrderRequest request)
    {
        var order = store.Add(new Order
        {
            CustomerId = request.CustomerId,
            ProductId = request.ProductId,
            Quantity = request.Quantity,
            CreatedAt = DateTime.UtcNow
        });
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, ToResponse(order));
    }

    [HttpPut("{id:int}")]
    public ActionResult<OrderResponse> Update(int id, UpdateOrderRequest request)
    {
        if (store.Get(id) is not { } order) return NotFound();
        order.Quantity = request.Quantity;
        return ToResponse(order);
    }

    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id) => store.Remove(id) ? NoContent() : NotFound();

    private static OrderResponse ToResponse(Order o) => new(o.Id, o.CustomerId, o.ProductId, o.Quantity, o.CreatedAt);
}
