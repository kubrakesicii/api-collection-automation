using Microsoft.AspNetCore.Mvc;
using SampleApi.Data;
using SampleApi.Dtos;
using SampleApi.Entities;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CustomersController(InMemoryStore<Customer> store) : ControllerBase
{
    [HttpGet]
    public ActionResult<IEnumerable<CustomerResponse>> GetAll() =>
        Ok(store.GetAll().Select(ToResponse));

    [HttpGet("{id:int}")]
    public ActionResult<CustomerResponse> GetById(int id) =>
        store.Get(id) is { } customer ? ToResponse(customer) : NotFound();

    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id) => store.Remove(id) ? NoContent() : NotFound();

    private static CustomerResponse ToResponse(Customer c) => new(c.Id, c.FullName, c.Email);
}
