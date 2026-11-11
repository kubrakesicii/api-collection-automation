using Microsoft.AspNetCore.Mvc;
using SampleApi.Data;
using SampleApi.Dtos;
using SampleApi.Entities;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CustomersController(InMemoryStore<Customer> store) : ControllerBase
{
    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id) => store.Remove(id) ? NoContent() : NotFound();

    private static CustomerResponse ToResponse(Customer c) => new(c.Id, c.FullName, c.Email);
}
