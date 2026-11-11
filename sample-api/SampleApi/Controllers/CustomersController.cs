using Microsoft.AspNetCore.Mvc;
using SampleApi.Data;
using SampleApi.Dtos;
using SampleApi.Entities;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CustomersController(InMemoryStore<Customer> store) : ControllerBase
{
    [HttpGet("{id:int}")]
    public ActionResult<CustomerResponse> GetById(int id) =>
        store.Get(id) is { } customer ? ToResponse(customer) : NotFound();

    private static CustomerResponse ToResponse(Customer c) => new(c.Id, c.FullName, c.Email);
}
