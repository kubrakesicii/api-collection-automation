using Microsoft.AspNetCore.Mvc;
using SampleApi.Data;
using SampleApi.Dtos;
using SampleApi.Entities;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ProductsController(InMemoryStore<Product> store) : ControllerBase
{
    [HttpPut("{id:int}")]
    public ActionResult<ProductResponse> Update(int id, UpdateProductRequest request)
    {
        if (store.Get(id) is not { } product) return NotFound();
        product.Name = request.Name;
        product.Price = request.Price;
        return ToResponse(product);
    }

    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id) => store.Remove(id) ? NoContent() : NotFound();

    private static ProductResponse ToResponse(Product p) => new(p.Id, p.Name, p.Price);
}
