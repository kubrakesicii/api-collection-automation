using System.ComponentModel.DataAnnotations;

namespace SampleApi.Dtos;

public sealed record CustomerResponse(int Id, string FullName, string Email);

public sealed record CreateCustomerRequest(
    [property: Required, StringLength(100)] string FullName,
    [property: Required, EmailAddress] string Email);

public sealed record UpdateCustomerRequest(
    [property: Required, StringLength(100)] string FullName,
    [property: Required, EmailAddress] string Email);
