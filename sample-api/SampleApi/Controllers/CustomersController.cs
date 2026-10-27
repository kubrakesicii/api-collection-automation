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

    [HttpPost]
    public ActionResult<CustomerResponse> Create(CreateCustomerRequest request)
    {
        var customer = store.Add(new Customer { FullName = request.FullName, Email = request.Email });
        return CreatedAtAction(nameof(GetById), new { id = customer.Id }, ToResponse(customer));
    }

    [HttpPut("{id:int}")]
    public ActionResult<CustomerResponse> Update(int id, UpdateCustomerRequest request)
    {
        if (store.Get(id) is not { } customer) return NotFound();
        customer.FullName = request.FullName;
        customer.Email = request.Email;
        return ToResponse(customer);
    }

    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id) => store.Remove(id) ? NoContent() : NotFound();

    private static CustomerResponse ToResponse(Customer c) => new(c.Id, c.FullName, c.Email);
}
